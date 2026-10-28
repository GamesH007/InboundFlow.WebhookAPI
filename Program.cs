using System.Threading.Channels;
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

builder.Services.AddScoped<SmsConversationHandler>();
builder.Services.AddSingleton(Channel.CreateUnbounded<IncomingSms>(new UnboundedChannelOptions { SingleReader = true }));
builder.Services.AddHostedService<SmsQueueWorker>();
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
// away with empty TwiML; SmsQueueWorker does the AI work from the queue.
app.MapPost("/api/webhooks/sms", async (HttpRequest request, Channel<IncomingSms> queue) =>
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

    await queue.Writer.WriteAsync(new IncomingSms(from, to, body));

    return Results.Content(new MessagingResponse().ToString(), "text/xml");
});

app.Run();
