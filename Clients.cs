using System.Text.Json;

// One entry per contractor on the retainer. Lives in clients.json next to the
// app (gitignored -- client names and the owner's cell never go to GitHub);
// clients.example.json shows the shape.
public record ClientConfig(
    string TwilioNumber,
    string BusinessName,
    string OwnerCell,
    string ServiceArea,
    string About);

public class ClientRegistry
{
    private readonly Dictionary<string, ClientConfig> byNumber;

    public ClientRegistry(string path)
    {
        if (!File.Exists(path))
        {
            Console.WriteLine($"[WARN] {path} not found -- no clients configured, every call will be ignored.");
            byNumber = new();
            return;
        }

        var clients = JsonSerializer.Deserialize<List<ClientConfig>>(
            File.ReadAllText(path),
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
        byNumber = clients.ToDictionary(c => c.TwilioNumber);
        Console.WriteLine($"Loaded {byNumber.Count} client(s) from {path}.");
    }

    // Twilio always sends E.164 (+15551234567), so an exact match is enough
    // as long as clients.json is written the same way.
    public ClientConfig? Find(string twilioNumber) =>
        byNumber.TryGetValue(twilioNumber, out var client) ? client : null;
}
