using Microsoft.EntityFrameworkCore;

// Everything that happens when a customer texts the number: opt-out
// keywords, HELP, saving the message, the AI reply, the handoff to the owner.
public class SmsConversationHandler
{
    const int MaxCustomerMessages = 6;
    static readonly HashSet<string> optOutWords = new() { "STOP", "STOPALL", "UNSUBSCRIBE", "CANCEL", "END", "QUIT", "REVOKE", "OPTOUT" };
    static readonly HashSet<string> optInWords = new() { "START", "UNSTOP", "YES" };
    static readonly HashSet<string> helpWords = new() { "HELP", "INFO" };

    private readonly AppDbContext db;
    private readonly ClientRegistry clients;
    private readonly ISmsSender sms;
    private readonly LeadQualifier? qualifier;

    public SmsConversationHandler(AppDbContext db, ClientRegistry clients, ISmsSender sms, LeadQualifier? qualifier = null)
    {
        this.db = db;
        this.clients = clients;
        this.sms = sms;
        this.qualifier = qualifier;
    }

    public async Task HandleAsync(string from, string to, string body)
    {
        var business = clients.Find(to);

        if (business is null)
        {
            Console.WriteLine($"[WARN] Text to {to}, which isn't in clients.json -- ignored.");
            return;
        }

        // Twilio's own opt-out handling sends the STOP/START confirmation texts;
        // here we just record it so we never text (or AI-process) them again.
        var keyword = body.ToUpperInvariant();
        if (optOutWords.Contains(keyword))
        {
            if (!await db.OptOuts.AnyAsync(o => o.CustomerPhone == from && o.ClientPhoneNumber == to))
            {
                db.OptOuts.Add(new OptOut { CustomerPhone = from, ClientPhoneNumber = to, Timestamp = DateTime.UtcNow });
                await db.SaveChangesAsync();
            }
            Console.WriteLine($"[INFO] {from} opted out of {business.BusinessName}.");
            return;
        }

        // HELP gets the business's contact line even from opted-out numbers --
        // carriers expect it answered either way. Not a lead message, so no AI
        // and no owner ping.
        if (helpWords.Contains(keyword))
        {
            var contact = new List<string>();
            if (!string.IsNullOrWhiteSpace(business.ContactPhone)) contact.Add($"call {business.ContactPhone}");
            if (!string.IsNullOrWhiteSpace(business.ContactEmail)) contact.Add($"email {business.ContactEmail}");
            var reachUs = contact.Count > 0 ? $" For help, {string.Join(" or ", contact)}." : "";
            await sms.SendAsync(to, from, $"{business.BusinessName}:{reachUs} Reply STOP to opt out. Msg & data rates may apply.");
            Console.WriteLine($"[INFO] {from} sent {keyword} to {business.BusinessName} -- help reply sent.");
            return;
        }

        var optOut = await db.OptOuts.FirstOrDefaultAsync(o => o.CustomerPhone == from && o.ClientPhoneNumber == to);
        if (optOut is not null)
        {
            if (optInWords.Contains(keyword))
            {
                db.OptOuts.Remove(optOut);
                await db.SaveChangesAsync();
                Console.WriteLine($"[INFO] {from} opted back in to {business.BusinessName}.");
            }
            return;
        }

        var lead = await db.Leads
            .Where(l => l.CustomerPhone == from && l.ClientPhoneNumber == to)
            .OrderByDescending(l => l.Timestamp)
            .FirstOrDefaultAsync();
        if (lead is null)
        {
            // Texted the number without calling first -- still a lead.
            lead = new Lead { CustomerPhone = from, ClientPhoneNumber = to, Timestamp = DateTime.UtcNow, Status = LeadStatus.Qualifying };
            db.Leads.Add(lead);
            await db.SaveChangesAsync();
        }

        db.Messages.Add(new Message { LeadId = lead.Id, Direction = "in", Body = body, Timestamp = DateTime.UtcNow });
        await db.SaveChangesAsync();

        // Already handed off: the owner is on it, so the AI stays out of the way
        // and anything new goes straight to the owner.
        if (lead.Status == LeadStatus.Qualified || qualifier is null)
        {
            await sms.SendAsync(to, business.OwnerCell, $"New text from {lead.CustomerName ?? from} ({from}): {body}");
            if (lead.Status != LeadStatus.Qualified)
            {
                lead.Status = LeadStatus.Qualified;
                lead.HandoffReason = "AI qualifier unavailable (GEMINI_API_KEY not set)";
                await db.SaveChangesAsync();
            }
            return;
        }

        lead.Status = LeadStatus.Qualifying;
        var history = await db.Messages.Where(m => m.LeadId == lead.Id).OrderBy(m => m.Id).ToListAsync();
        bool forceHandoff = history.Count(m => m.Direction == "in") >= MaxCustomerMessages;

        QualifierResult result;
        try
        {
            result = await qualifier.NextReplyAsync(business, lead, history, forceHandoff);
        }
        catch (Exception ex)
        {
            // AI down: never leave the customer hanging -- hand off with what we have.
            Console.WriteLine($"[ERROR] Gemini failed for lead {lead.Id}: {ex.Message}");
            result = new QualifierResult(
                $"Thanks! Someone from {business.BusinessName} will call you shortly.",
                null, null, null, null, null, true, "AI assistant error, handed off early");
        }

        lead.CustomerName = result.CustomerName ?? lead.CustomerName;
        lead.Problem = result.Problem ?? lead.Problem;
        lead.Address = result.Address ?? lead.Address;
        lead.Urgency = result.Urgency ?? lead.Urgency;
        lead.PreferredTime = result.PreferredTime ?? lead.PreferredTime;

        await sms.SendAsync(to, from, result.Reply);
        db.Messages.Add(new Message { LeadId = lead.Id, Direction = "out", Body = result.Reply, Timestamp = DateTime.UtcNow });

        if (result.Handoff)
        {
            lead.Status = LeadStatus.Qualified;
            lead.HandoffReason = result.HandoffReason;
            await sms.SendAsync(to, business.OwnerCell,
                $"""
            New lead, call them back:
            Name: {lead.CustomerName ?? "unknown"}
            Phone: {from}
            Problem: {lead.Problem ?? "unknown"}
            Address: {lead.Address ?? "unknown"}
            Urgency: {lead.Urgency ?? "unknown"}
            Best time: {lead.PreferredTime ?? "unknown"}
            Why now: {lead.HandoffReason ?? "details collected"}
            Their last text: {body}
            """);
            Console.WriteLine($"[ALERT] Lead {lead.Id} qualified and handed off to {business.BusinessName}.");
        }

        await db.SaveChangesAsync();
    }
}