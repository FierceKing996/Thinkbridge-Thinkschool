using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;
using QuotesApi.Models;
using QuotesApi.Services;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace QuotesApi.Messaging;

// The second of Day 19's "two subscriptions" to the quotes.events topic - an
// independent queue, bound to the same routing pattern as the search-index
// queue, consumed by a single worker (not a competing-consumer group; audit
// entries must land in the order they were published, which multiple
// concurrent consumers on one queue can't guarantee). Same idempotent-dedupe
// shape as the search-index consumer, just against its own ConsumerName so the
// two subscriptions' dedupe tables never collide on the same MessageId.
public class QuoteAuditLogConsumerService(
    RabbitMqConnection connection,
    RabbitMqOptions options,
    IServiceScopeFactory scopeFactory,
    ILogger<QuoteAuditLogConsumerService> logger) : BackgroundService
{
    private const string ConsumerName = "audit-log";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        IChannel? channel = null;
        try
        {
            channel = await connection.CreateChannelAsync(stoppingToken);
            await RabbitMqTopology.DeclareAsync(channel, options, stoppingToken);
            await channel.BasicQosAsync(0, prefetchCount: 1, global: false, stoppingToken);

            var consumer = new AsyncEventingBasicConsumer(channel);
            consumer.ReceivedAsync += (_, ea) => HandleDeliveryAsync(channel, ea, stoppingToken);

            await channel.BasicConsumeAsync(options.AuditLogQueue, autoAck: false, consumer,
                cancellationToken: stoppingToken);

            logger.LogInformation("Audit-log consumer listening on {Queue}.", options.AuditLogQueue);

            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Expected shutdown path.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Audit-log consumer stopped unexpectedly.");
        }
        finally
        {
            if (channel is not null) await channel.DisposeAsync();
        }
    }

    private async Task HandleDeliveryAsync(IChannel channel, BasicDeliverEventArgs ea, CancellationToken ct)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<EventEnvelope>(ea.Body.Span)
                ?? throw new InvalidOperationException("Empty envelope.");

            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var clock = scope.ServiceProvider.GetRequiredService<IClock>();

            var alreadyProcessed = await db.ProcessedMessages
                .AnyAsync(m => m.Consumer == ConsumerName && m.MessageId == envelope.MessageId, ct);

            if (!alreadyProcessed)
            {
                db.AuditLogEntries.Add(
                    AuditLogEntry.Create(envelope.MessageId, envelope.Type, envelope.Payload, clock.UtcNow));
                db.ProcessedMessages.Add(ProcessedMessage.Create(envelope.MessageId, ConsumerName, clock.UtcNow));
                await db.SaveChangesAsync(ct);
            }

            await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Audit-log consumer failed to process delivery tag {DeliveryTag}.", ea.DeliveryTag);
            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, ct);
        }
    }
}
