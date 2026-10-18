using Microsoft.EntityFrameworkCore;
using Twilio.Security;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite("Data Source=inboundflow.db"));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

// Twilio signs every webhook request with X-Twilio-Signature; without checking
// it, anyone who finds this URL can post fake missed calls. Set via
// `setx TWILIO_AUTH_TOKEN "your-token"` -- never hardcoded, since appsettings.json
// is not gitignored in this repo and would leak it to GitHub.
var twilioAuthToken = Environment.GetEnvironmentVariable("TWILIO_AUTH_TOKEN");

app.MapPost("/api/webhooks/missed-call", async (HttpRequest request, AppDbContext db) =>
{
    if (string.IsNullOrEmpty(twilioAuthToken))
    {
        return Results.Problem("TWILIO_AUTH_TOKEN is not set on the server.", statusCode: 500);
    }

    // Real Twilio webhooks are application/x-www-form-urlencoded, not JSON --
    // binding this as a JSON body (the original version) rejected every real
    // call with a 415 before it ever reached this code.
    var form = await request.ReadFormAsync();

    var signature = request.Headers["X-Twilio-Signature"].ToString();
    var requestUrl = $"{request.Scheme}://{request.Host}{request.Path}";
    var formParams = form.ToDictionary(kv => kv.Key, kv => kv.Value.ToString());

    var validator = new RequestValidator(twilioAuthToken);
    if (!validator.Validate(requestUrl, formParams, signature))
    {
        return Results.Unauthorized();
    }

    var from = form["From"].ToString();
    var to = form["To"].ToString();
    if (string.IsNullOrEmpty(from) || string.IsNullOrEmpty(to))
    {
        return Results.BadRequest(new { message = "Missing From/To in webhook payload" });
    }

    var lead = new Lead
    {
        CustomerPhone = from,
        ClientPhoneNumber = to,
        Timestamp = DateTime.UtcNow,
        Status = "Pending SMS Follow-up"
    };

    db.Leads.Add(lead);
    await db.SaveChangesAsync();

    Console.WriteLine($"[ALERT] Missed call logged from: {lead.CustomerPhone} (client line: {lead.ClientPhoneNumber})");

    return Results.Ok(new { message = "Lead successfully captured", leadId = lead.Id });
});

app.Run();

public class Lead
{
    public int Id { get; set; }
    public string CustomerPhone { get; set; } = string.Empty;
    // Which client's Twilio number was called -- the only way to tell clients'
    // leads apart once more than one client is live on this same database.
    public string ClientPhoneNumber { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string Status { get; set; } = string.Empty;
}

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
    public DbSet<Lead> Leads => Set<Lead>();
}
