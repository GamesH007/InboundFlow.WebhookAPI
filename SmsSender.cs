using Twilio;
using Twilio.Rest.Api.V2010.Account;

public interface ISmsSender
{
    Task SendAsync(string from, string to, string body);
}

public class TwilioSmsSender : ISmsSender
{
    public TwilioSmsSender(string accountSid, string authToken)
    {
        TwilioClient.Init(accountSid, authToken);
    }

    public async Task SendAsync(string from, string to, string body)
    {
        await MessageResource.CreateAsync(
            to: new Twilio.Types.PhoneNumber(to),
            from: new Twilio.Types.PhoneNumber(from),
            body: body);
    }
}

// Used whenever TWILIO_ACCOUNT_SID isn't set: every text is printed instead of
// sent, so the whole flow (call -> text -> AI conversation -> owner handoff)
// can be tested locally before a Twilio account or 10DLC approval exists.
public class DryRunSmsSender : ISmsSender
{
    public Task SendAsync(string from, string to, string body)
    {
        Console.WriteLine($"[DRY RUN SMS] {from} -> {to}:\n{body}\n");
        return Task.CompletedTask;
    }
}
