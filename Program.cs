using Microsoft.EntityFrameworkCore;
using Twilio.Security;
using Twilio.TwiML;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite("Data Source=inboundflow.db"));

// Twilio signs every webhook request with X-Twilio-Signature; without checking
// it, anyone who finds this URL can post fake missed calls. Set via
// `setx TWILIO_AUTH_TOKEN "your-token"` -- never hardcoded, since appsettings.json
// is not gitignored in this repo and would leak it to GitHub.
var twilioAuthToken = Environment.GetEnvironmentVariable("TWILIO_AUTH_TOKEN");
var twilioAccountSid = Environment.GetEnvironmentVariable("TWILIO_ACCOUNT_SID");
var geminiApiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY");

ISmsSender smsSender = !string.IsNullOrEmpty(twilioAccountSid) && !string.IsNullOrEmpty(twilioAuthToken)
    ? new TwilioSmsSender(twilioAccountSid, twilioAuthToken)
    : new DryRunSmsSender();
builder.Services.AddSingleton(smsSender);
builder.Services.AddSingleton(new ClientRegistry(Path.Combine(builder.Environment.ContentRootPath, "clients.json")));
if (!string.IsNullOrEmpty(geminiApiKey))
    builder.Services.AddSingleton(new LeadQualifier(geminiApiKey));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

Console.WriteLine(smsSender is DryRunSmsSender
    ? "[DRY RUN] TWILIO_ACCOUNT_SID not set -- texts will be printed, not sent."
    : "Twilio SMS sending is live.");
if (string.IsNullOrEmpty(geminiApiKey))
    Console.WriteLine("[WARN] GEMINI_API_KEY not set -- customer replies will be forwarded to the owner without AI qualifying.");

const int MaxCustomerMessages = 6;
var optOutWords = new HashSet<string> { "STOP", "STOPALL", "UNSUBSCRIBE", "CANCEL", "END", "QUIT", "REVOKE", "OPTOUT" };
var optInWords = new HashSet<string> { "START", "UNSTOP", "YES" };
var helpWords = new HashSet<string> { "HELP", "INFO" };

// Replies are processed one at a time in the background: Twilio gives a
// webhook ~15s before it gives up, and a Gemini call with retries can take
// longer than that. One-at-a-time also stops two quick texts from the same
// customer racing each other into duplicate AI replies.
var conversationLock = new SemaphoreSlim(1, 1);

// Validates the signature and returns the form, or null if the request didn't
// genuinely come from Twilio.
async Task<IFormCollection?> ReadVerifiedTwilioForm(HttpRequest request)
{
    // Real Twilio webhooks are application/x-www-form-urlencoded, not JSON --
    // binding this as a JSON body (the original version) rejected every real
    // call with a 415 before it ever reached this code.
    var form = await request.ReadFormAsync();

    var signature = request.Headers["X-Twilio-Signature"].ToString();
    var requestUrl = $"{request.Scheme}://{request.Host}{request.Path}";
    var formParams = form.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());

    return new RequestValidator(twilioAuthToken).Validate(requestUrl, formParams, signature) ? form : null;
}

// The client forwards unanswered calls to their Twilio number, so every call
// that lands here is a missed call. Twilio's Voice webhook expects TwiML back
// -- the original version returned JSON, which makes the caller hear
// "an application error has occurred".
app.MapPost("/api/webhooks/missed-call", async (HttpRequest request, AppDbContext db, ClientRegistry clients, ISmsSender sms) =>
{
    if (string.IsNullOrEmpty(twilioAuthToken))
        return Results.Problem("TWILIO_AUTH_TOKEN is not set on the server.", statusCode: 500);

    var form = await ReadVerifiedTwilioForm(request);
    if (form is null) return Results.Unauthorized();

    var from = form["From"].ToString();
    var to = form["To"].ToString();
    if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
        return Results.BadRequest(new { message = "Missing From/To in webhook payload" });

    var business = clients.Find(to);
    var voice = new VoiceResponse();

    if (business is null)
    {
        Console.WriteLine($"[WARN] Call to {to}, which isn't in clients.json -- ignored.");
        voice.Say("Sorry, we can't take your call right now. Please try again later.");
        voice.Hangup();
        return Results.Content(voice.ToString(), "text/xml");
    }

    voice.Say("Sorry we missed your call. We're sending you a text right now so we can help you faster.");
    voice.Hangup();

    // Whatever goes wrong below, the caller still hears the message above.
    try
    {
        bool optedOut = await db.OptOuts.AnyAsync(o => o.CustomerPhone == from && o.ClientPhoneNumber == to);
        var since = DateTime.UtcNow.AddHours(-24);
        var recentLead = await db.Leads
            .Where(l => l.CustomerPhone == from && l.ClientPhoneNumber == to && l.Timestamp > since)
            .OrderByDescending(l => l.Timestamp)
            .FirstOrDefaultAsync();

        if (optedOut)
        {
            Console.WriteLine($"[INFO] Missed call from {from} (opted out) -- not texting.");
            await sms.SendAsync(to, business.OwnerCell, $"Missed call from {from}. They opted out of texts, so call them back directly.");
        }
        else if (recentLead is not null)
        {
            // Same caller again within a day: don't spam them with a second
            // intro text, just tell the owner.
            Console.WriteLine($"[INFO] Repeat call from {from} -- lead {recentLead.Id} already open.");
            await sms.SendAsync(to, business.OwnerCell, $"{from} called again (missed). They're already in a text conversation with your assistant.");
        }
        else
        {
            var lead = new Lead
            {
                CustomerPhone = from,
                ClientPhoneNumber = to,
                Timestamp = DateTime.UtcNow,
                Status = LeadStatus.AwaitingReply
            };
            db.Leads.Add(lead);
            await db.SaveChangesAsync();

            var intro = $"Hey, this is {business.BusinessName}. Sorry we missed your call! What's going on with your garage door? Reply STOP to opt out.";
            await sms.SendAsync(to, from, intro);
            db.Messages.Add(new Message { LeadId = lead.Id, Direction = "out", Body = intro, Timestamp = DateTime.UtcNow });
            await db.SaveChangesAsync();

            await sms.SendAsync(to, business.OwnerCell, $"Missed call from {from}. We texted them automatically. You'll get the job details here as soon as they reply.");
            Console.WriteLine($"[ALERT] Missed call from {from} for {business.BusinessName} -- lead {lead.Id}, intro text sent.");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"[ERROR] Missed-call follow-up failed for {from}: {ex.Message}");
    }

    return Results.Content(voice.ToString(), "text/xml");
});

// Customer texts back (or texts the number directly). Answer Twilio right
// away with empty TwiML, then do the AI work in the background.
app.MapPost("/api/webhooks/sms", async (HttpRequest request, IServiceScopeFactory scopeFactory) =>
{
    if (string.IsNullOrEmpty(twilioAuthToken))
        return Results.Problem("TWILIO_AUTH_TOKEN is not set on the server.", statusCode: 500);

    var form = await ReadVerifiedTwilioForm(request);
    if (form is null) return Results.Unauthorized();

    var from = form["From"].ToString();
    var to = form["To"].ToString();
    var body = form["Body"].ToString().Trim();
    if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
        return Results.BadRequest(new { message = "Missing From/To in webhook payload" });

    _ = Task.Run(async () =>
    {
        await conversationLock.WaitAsync();
        try
        {
            using var scope = scopeFactory.CreateScope();
            await HandleIncomingSms(scope.ServiceProvider, from, to, body);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[ERROR] Handling text from {from} failed: {ex.Message}");
        }
        finally
        {
            conversationLock.Release();
        }
    });

    return Results.Content(new MessagingResponse().ToString(), "text/xml");
});

async Task HandleIncomingSms(IServiceProvider services, string from, string to, string body)
{
    var db = services.GetRequiredService<AppDbContext>();
    var business = services.GetRequiredService<ClientRegistry>().Find(to);
    var sms = services.GetRequiredService<ISmsSender>();
    var qualifier = services.GetService<LeadQualifier>();

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

app.Run();
