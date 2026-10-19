using System.Text;
using System.Text.Json;

public record QualifierResult(
    string Reply,
    string? CustomerName,
    string? Problem,
    string? Address,
    string? Urgency,
    string? PreferredTime,
    bool Handoff,
    string? HandoffReason);

// Writes the AI side of the SMS conversation via Gemini and pulls the job
// details out of it. Same REST call shape, model alias and retry policy as
// InboundFlow.OutreachWriter, so both tools break (and get fixed) the same way.
public class LeadQualifier
{
    private static readonly HttpClient client = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly string apiKey;
    private readonly string model = Environment.GetEnvironmentVariable("GEMINI_MODEL") ?? "gemini-flash-latest";

    public LeadQualifier(string apiKey)
    {
        this.apiKey = apiKey;
    }

    public async Task<QualifierResult> NextReplyAsync(ClientConfig business, Lead lead, List<Message> history, bool forceHandoff)
    {
        var requestBody = JsonSerializer.Serialize(new
        {
            contents = new[] { new { parts = new[] { new { text = BuildPrompt(business, lead, history, forceHandoff) } } } },
            generationConfig = new { responseMimeType = "application/json" }
        });

        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";

        // 429 / 503 are transient -- retry with backoff. Anything else fails fast.
        string responseBody = "";
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(requestBody, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-Goog-Api-Key", apiKey);

            var response = await client.SendAsync(request);
            responseBody = await response.Content.ReadAsStringAsync();
            if (response.IsSuccessStatusCode) break;

            bool transient = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                || response.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable;
            if (!transient || attempt == 3)
                throw new Exception($"Gemini API error ({(int)response.StatusCode}): {responseBody}");

            await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt))); // 2s, 4s
        }

        using var doc = JsonDocument.Parse(responseBody);
        var text = doc.RootElement.GetProperty("candidates")[0]
            .GetProperty("content").GetProperty("parts")[0].GetProperty("text").GetString() ?? "{}";

        using var result = JsonDocument.Parse(text);
        var root = result.RootElement;
        string? Field(string name) =>
            root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(v.GetString())
                ? v.GetString()
                : null;

        return new QualifierResult(
            Reply: Field("reply") ?? $"Thanks! Someone from {business.BusinessName} will reach out shortly.",
            CustomerName: Field("customerName"),
            Problem: Field("problem"),
            Address: Field("address"),
            Urgency: Field("urgency"),
            PreferredTime: Field("preferredTime"),
            Handoff: forceHandoff || (root.TryGetProperty("handoff", out var h) && h.ValueKind == JsonValueKind.True),
            HandoffReason: Field("handoffReason") ?? (forceHandoff ? "Conversation reached the message limit" : null));
    }

    private static string BuildPrompt(ClientConfig business, Lead lead, List<Message> history, bool forceHandoff)
    {
        var transcript = string.Join("\n", history.Select(m =>
            $"{(m.Direction == "in" ? "CUSTOMER" : "YOU")}: {m.Body}"));

        return $$"""
            You are the text-message assistant for {{business.BusinessName}}, a garage door
            repair company serving {{business.ServiceArea}}. About the business: {{business.About}}

            A customer called, nobody could pick up, and you texted them. Your job is to
            find out what they need so the owner can call back ready to book the job.
            Collect, one question at a time, only what's still missing:
            1. What's wrong with the garage door
            2. Their name
            3. The address or at least the area/neighborhood
            4. How urgent it is (car stuck inside, door stuck open, can wait a few days)
            5. Best time for the owner to call or come out

            Rules:
            - Sound like a friendly person from a small local business. Short texts, under
              300 characters, plain text, no emojis, no em dashes.
            - Never quote prices, estimates, or arrival times. If asked, say the owner will
              go over that when they call.
            - If asked whether you're a real person, be honest: you're the company's
              automated text assistant, and a real person from the team will call them.
            - Hand off immediately (handoff = true) if: it's an emergency (car trapped,
              door stuck open overnight, anyone hurt, safety risk), the customer asks for
              a person or a call, or the customer seems annoyed.
            - Hand off as soon as you have the problem, the area, and the urgency. Name and
              time are nice to have, not required.
            - When handing off, the reply tells the customer someone from the team will
              call them shortly. Don't ask another question in that reply.
            {{(forceHandoff ? "- This conversation has hit its message limit. Hand off NOW with whatever you have." : "")}}

            What's known so far (may be empty): name={{lead.CustomerName}}; problem={{lead.Problem}};
            address={{lead.Address}}; urgency={{lead.Urgency}}; preferredTime={{lead.PreferredTime}}

            Conversation so far:
            {{transcript}}

            Respond with JSON only, in exactly this shape. Use null for anything not known yet,
            and carry forward details already known:
            {
              "reply": "the next text to send the customer",
              "customerName": string or null,
              "problem": string or null,
              "address": string or null,
              "urgency": string or null,
              "preferredTime": string or null,
              "handoff": true or false,
              "handoffReason": "short reason, only when handoff is true" or null
            }
            """;
    }
}
