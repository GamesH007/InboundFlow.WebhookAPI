using System.Threading.Channels;

// One customer text waiting to be handled: who sent it, to which Twilio number, and what it said.
public record IncomingSms(string From, string To, string Body);

// Takes customer texts off the queue one at a time and hands each one to a
// fresh SmsConversationHandler, so Twilio never waits on the slow AI work.
public class SmsQueueWorker : BackgroundService
{
    private readonly Channel<IncomingSms> queue;
    private readonly IServiceScopeFactory scopeFactory;

    public SmsQueueWorker(Channel<IncomingSms> queue, IServiceScopeFactory scopeFactory)
    {
        this.queue = queue;
        this.scopeFactory = scopeFactory;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (var text in queue.Reader.ReadAllAsync(stoppingToken))
        {
            try
            {
                using var scope = scopeFactory.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<SmsConversationHandler>();
                await handler.HandleAsync(text.From, text.To, text.Body);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ERROR] Handling text from {text.From} failed: {ex.Message}");
            }
        }
    }
}