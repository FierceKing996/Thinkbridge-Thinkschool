using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;
using QuotesApi.Messaging;

namespace QuotesApi.Services;

// Day 20's relay half of the outbox pattern. Polls for unprocessed rows,
// publishes each, and marks it processed - but only *after* PublishAsync
// returns successfully. See DAY20_OUTBOX.md for the crash scenario this
// ordering is built around and the test that proves it.
public class OutboxRelayService(
    IServiceScopeFactory scopeFactory,
    ILogger<OutboxRelayService> logger) : BackgroundService
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);
    private const int BatchSize = 50;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RelayBatchAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A single bad batch (e.g. the broker connection drops mid-poll) must
                // not stop the relay loop entirely - the next tick tries again, and
                // every row that didn't get marked processed is still sitting there
                // to retry, exactly as designed.
                logger.LogError(ex, "Outbox relay batch failed; will retry next poll.");
            }

            try
            {
                await Task.Delay(PollInterval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    public async Task RelayBatchAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var publisher = scope.ServiceProvider.GetRequiredService<IEventPublisher>();
        var clock = scope.ServiceProvider.GetRequiredService<IClock>();

        var pending = await db.OutboxMessages
            .Where(m => m.ProcessedAt == null)
            .OrderBy(m => m.OccurredAt)
            .Take(BatchSize)
            .ToListAsync(ct);

        foreach (var message in pending)
        {
            try
            {
                // MessageId reused as-is: if this exact row was already published once
                // (relay published successfully, then crashed before the MarkProcessed
                // SaveChangesAsync below committed), the consumer sees the same
                // MessageId again and its ProcessedMessage dedupe check absorbs the
                // duplicate - at-least-once delivery, effectively-once processing.
                var envelope = new EventEnvelope(message.Id, message.Type, message.Payload, message.OccurredAt);
                await publisher.PublishAsync(RoutingKeyFor(message.Type), envelope, ct);

                message.MarkProcessed(clock.UtcNow);
                await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                // Deliberately per-message, not per-batch: one message failing to
                // publish (broker unreachable, etc.) must not block every other
                // message in the batch from getting its own attempt.
                logger.LogWarning(ex, "Failed to publish outbox message {MessageId}; leaving unprocessed for retry.", message.Id);
            }
        }
    }

    private static string RoutingKeyFor(string messageType) => messageType;
}
