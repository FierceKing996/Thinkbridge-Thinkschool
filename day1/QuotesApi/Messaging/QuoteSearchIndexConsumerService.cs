using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;
using QuotesApi.Models;
using QuotesApi.Services;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace QuotesApi.Messaging;

// The "consume with a competing-consumer worker" half of Day 19: SearchIndexConsumerCount
// (default 3) independent channels all consume the *same* queue. RabbitMQ round-robins
// deliveries across whichever consumers are attached to a queue - that's the whole
// competing-consumer mechanism, there's no extra code here to distribute work, only
// to run several consumers instead of one.
//
// A message containing the literal marker "POISON" in its payload is this
// service's stand-in for a message no amount of retrying will ever process
// successfully (a malformed payload, a violated invariant). It's rejected with
// requeue: false, which - given the queue's x-dead-letter-exchange argument
// (see RabbitMqTopology) - is what makes RabbitMQ route it to the DLQ instead of
// redelivering it forever. A real system would retry a handful of times first
// (transient failures shouldn't dead-letter on the first attempt); skipping that
// here keeps the poison-message path itself easy to trigger and observe.
public class QuoteSearchIndexConsumerService(
    RabbitMqConnection connection,
    RabbitMqOptions options,
    IServiceScopeFactory scopeFactory,
    ILogger<QuoteSearchIndexConsumerService> logger) : BackgroundService
{
    private const string ConsumerName = "search-index";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var channels = new List<IChannel>();
        try
        {
            for (var i = 0; i < options.SearchIndexConsumerCount; i++)
            {
                var channel = await connection.CreateChannelAsync(stoppingToken);
                await RabbitMqTopology.DeclareAsync(channel, options, stoppingToken);
                await channel.BasicQosAsync(0, prefetchCount: 1, global: false, stoppingToken);

                var consumer = new AsyncEventingBasicConsumer(channel);
                consumer.ReceivedAsync += (_, ea) => HandleDeliveryAsync(channel, ea, stoppingToken);

                await channel.BasicConsumeAsync(options.SearchIndexQueue, autoAck: false, consumer,
                    cancellationToken: stoppingToken);
                channels.Add(channel);
            }

            logger.LogInformation(
                "{Count} competing consumers listening on {Queue}.", channels.Count, options.SearchIndexQueue);

            // Nothing left to do but wait for shutdown - all the work happens in
            // HandleDeliveryAsync, invoked by RabbitMQ.Client's own dispatch loop.
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Expected shutdown path.
        }
        catch (Exception ex)
        {
            // No broker running (e.g. RabbitMq:HostName points somewhere nothing is
            // listening) must not crash the whole host - BackgroundService rethrowing
            // here would stop IHostApplicationLifetime for the entire app, not just
            // this worker.
            logger.LogWarning(ex, "Search-index consumer stopped unexpectedly.");
        }
        finally
        {
            foreach (var channel in channels)
            {
                await channel.DisposeAsync();
            }
        }
    }

    private async Task HandleDeliveryAsync(IChannel channel, BasicDeliverEventArgs ea, CancellationToken ct)
    {
        try
        {
            var envelope = JsonSerializer.Deserialize<EventEnvelope>(ea.Body.Span)
                ?? throw new InvalidOperationException("Empty envelope.");

            if (envelope.Payload.Contains("POISON", StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Poison message {envelope.MessageId} - simulated processing failure.");
            }

            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var clock = scope.ServiceProvider.GetRequiredService<IClock>();

            // Idempotency: a message already recorded as processed by this consumer
            // is acked and skipped, not reprocessed - this is what makes redelivery
            // (the outbox relay republishing after a crash, or RabbitMQ's own
            // at-least-once guarantee) safe.
            var alreadyProcessed = await db.ProcessedMessages
                .AnyAsync(m => m.Consumer == ConsumerName && m.MessageId == envelope.MessageId, ct);

            if (!alreadyProcessed)
            {
                // Stands in for the real "update the search index" side effect - the
                // point of this exercise is the delivery/idempotency/DLQ plumbing
                // around that call, not the indexing logic itself.
                logger.LogInformation(
                    "Search-index: indexing {MessageType} {MessageId}.", envelope.Type, envelope.MessageId);

                db.ProcessedMessages.Add(ProcessedMessage.Create(envelope.MessageId, ConsumerName, clock.UtcNow));
                await db.SaveChangesAsync(ct);
            }

            await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, ct);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Search-index consumer failed to process delivery tag {DeliveryTag}.", ea.DeliveryTag);
            await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, ct);
        }
    }
}
