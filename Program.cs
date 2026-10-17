using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddDbContext<AppDbContext>(options => 
    options.UseSqlite("Data Source=inboundflow.db"));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    db.Database.EnsureCreated();
}

app.MapPost("/api/webhooks/missed-call", async (MissedCallPayload payload, AppDbContext db) =>
{
    var lead = new Lead 
    { 
        CustomerPhone = payload.From, 
        Timestamp = DateTime.UtcNow, 
        Status = "Pending SMS Follow-up" 
    };
    
    db.Leads.Add(lead);
    await db.SaveChangesAsync();
    
    Console.WriteLine($"[ALERT] Missed call logged from: {lead.CustomerPhone}");
    
    return Results.Ok(new { message = "Lead successfully captured", leadId = lead.Id });
});

app.Run();

public class MissedCallPayload { public string From { get; set; } = string.Empty; }

public class Lead 
{ 
    public int Id { get; set; } 
    public string CustomerPhone { get; set; } = string.Empty; 
    public DateTime Timestamp { get; set; } 
    public string Status { get; set; } = string.Empty; 
}

public class AppDbContext : DbContext 
{ 
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { } 
    public DbSet<Lead> Leads => Set<Lead>(); 
}