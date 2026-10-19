using Microsoft.EntityFrameworkCore;

public class Lead
{
    public int Id { get; set; }
    public string CustomerPhone { get; set; } = string.Empty;
    // Which client's Twilio number was called -- the only way to tell clients'
    // leads apart once more than one client is live on this same database.
    public string ClientPhoneNumber { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
    public string Status { get; set; } = string.Empty;

    // Filled in by the AI qualifier as the SMS conversation goes on; whatever
    // it has at handoff time is what the owner gets texted.
    public string? CustomerName { get; set; }
    public string? Problem { get; set; }
    public string? Address { get; set; }
    public string? Urgency { get; set; }
    public string? PreferredTime { get; set; }
    public string? HandoffReason { get; set; }
}

public class Message
{
    public int Id { get; set; }
    public int LeadId { get; set; }
    public string Direction { get; set; } = string.Empty; // "in" (customer) or "out" (us)
    public string Body { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}

// One row per customer who texted STOP to a given client's number. Twilio
// blocks those sends on its own too, but checking here means we never even
// try -- and never burn a Gemini call on someone who opted out.
public class OptOut
{
    public int Id { get; set; }
    public string CustomerPhone { get; set; } = string.Empty;
    public string ClientPhoneNumber { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}

public static class LeadStatus
{
    public const string AwaitingReply = "Texted - awaiting reply";
    public const string Qualifying = "Qualifying";
    public const string Qualified = "Qualified - handed off";
}

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }
    public DbSet<Lead> Leads => Set<Lead>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<OptOut> OptOuts => Set<OptOut>();
}
