# Day 19 — Topics + DLQ (RabbitMQ instead of Azure Service Bus)

Azure Service Bus needs an Azure subscription. **RabbitMQ** (open-source, runs
anywhere via `docker-compose up`) has the same shape for this exercise: a topic
exchange plays the role of a Service Bus *topic*, a queue bound to it plays the role
of a *subscription*, and a dead-letter exchange + queue is RabbitMQ's native DLQ
mechanism — no functionality is missing for what this exercise asks. See
`docker-compose.yml` and `Messaging/` for the full wiring.

## Publisher + consumer

Publisher (`Messaging/RabbitMqEventPublisher.cs`) — one topic exchange
(`quotes.events`), routing key = event type:

```csharp
public class RabbitMqEventPublisher(RabbitMqConnection connection, RabbitMqOptions options) : IEventPublisher
{
    public async Task PublishAsync(string routingKey, EventEnvelope envelope, CancellationToken ct)
    {
        await using var channel = await connection.CreateChannelAsync(ct);
        await RabbitMqTopology.DeclareAsync(channel, options, ct);

        var body = JsonSerializer.SerializeToUtf8Bytes(envelope);
        var properties = new BasicProperties
        {
            ContentType = "application/json",
            DeliveryMode = DeliveryModes.Persistent,
            MessageId = envelope.MessageId.ToString(),
        };

        await channel.BasicPublishAsync(options.Exchange, routingKey, mandatory: false, properties, body, ct);
    }
}
```

Two independent subscriptions to that one exchange (`Messaging/RabbitMqTopology.cs`):

```csharp
var queueArgs = new Dictionary<string, object?> { ["x-dead-letter-exchange"] = options.DeadLetterExchange };

await channel.QueueDeclareAsync(options.SearchIndexQueue, durable: true, exclusive: false, autoDelete: false,
    arguments: queueArgs, cancellationToken: ct);
await channel.QueueBindAsync(options.SearchIndexQueue, options.Exchange, "quote.*", cancellationToken: ct);

await channel.QueueDeclareAsync(options.AuditLogQueue, durable: true, exclusive: false, autoDelete: false,
    arguments: queueArgs, cancellationToken: ct);
await channel.QueueBindAsync(options.AuditLogQueue, options.Exchange, "quote.*", cancellationToken: ct);
```

Competing-consumer worker (`Messaging/QuoteSearchIndexConsumerService.cs`) —
`SearchIndexConsumerCount` (3) independent channels all consuming the *same* queue;
RabbitMQ round-robins deliveries across them, which is the entire competing-consumer
mechanism:

```csharp
for (var i = 0; i < options.SearchIndexConsumerCount; i++)
{
    var channel = await connection.CreateChannelAsync(stoppingToken);
    await RabbitMqTopology.DeclareAsync(channel, options, stoppingToken);
    await channel.BasicQosAsync(0, prefetchCount: 1, global: false, stoppingToken);

    var consumer = new AsyncEventingBasicConsumer(channel);
    consumer.ReceivedAsync += (_, ea) => HandleDeliveryAsync(channel, ea, stoppingToken);
    await channel.BasicConsumeAsync(options.SearchIndexQueue, autoAck: false, consumer, cancellationToken: stoppingToken);
}
```

## Idempotency (dedupe on message id)

Every consumer checks a `ProcessedMessage` table — `(Consumer, MessageId)` unique —
before doing its side effect, and skips (but still acks) if a row already exists:

```csharp
var alreadyProcessed = await db.ProcessedMessages
    .AnyAsync(m => m.Consumer == ConsumerName && m.MessageId == envelope.MessageId, ct);

if (!alreadyProcessed)
{
    // ... do the actual work ...
    db.ProcessedMessages.Add(ProcessedMessage.Create(envelope.MessageId, ConsumerName, clock.UtcNow));
    await db.SaveChangesAsync(ct);
}

await channel.BasicAckAsync(ea.DeliveryTag, multiple: false, ct);
```

This is what turns RabbitMQ's at-least-once delivery guarantee into effectively-once
*processing* — see `DAY20_OUTBOX.md` for the exact redelivery scenario it protects
against, proven by `Quotes.Tests.Unit/OutboxRelayServiceTests.cs`.

## Proof: DLQ catching a poison message

A message whose payload contains the literal marker `"POISON"` is this exercise's
stand-in for "a message no amount of retrying will ever process successfully":

```csharp
if (envelope.Payload.Contains("POISON", StringComparison.Ordinal))
{
    throw new InvalidOperationException($"Poison message {envelope.MessageId} - simulated processing failure.");
}
```

```csharp
catch (Exception ex)
{
    logger.LogError(ex, "Search-index consumer failed to process delivery tag {DeliveryTag}.", ea.DeliveryTag);
    await channel.BasicNackAsync(ea.DeliveryTag, multiple: false, requeue: false, ct);
}
```

`requeue: false` on a queue declared with `x-dead-letter-exchange` is what makes
RabbitMQ route the rejected message to `quotes.events.dlx` → `quotes.events.dlq`
instead of discarding it or redelivering it forever. To see it land: `docker compose
up`, set `RabbitMq:HostName` (see below), publish a `QuoteCreated` payload
containing `POISON` (e.g. author `"POISON test"`), then check the management UI at
`http://localhost:15672` → Queues → `quotes.events.dlq` → Get messages. A real
system would retry a handful of times first (a transient network blip shouldn't
dead-letter on attempt one) — skipped here to keep the poison-message path itself
trivial to trigger and observe; see the comment in
`QuoteSearchIndexConsumerService.cs` for the tradeoff.

## Turning it on

Neither RabbitMQ nor its consumers run unless `RabbitMq:HostName` is actually
configured — same "opt in only when configured" pattern this app already uses for
Key Vault/App Insights, so `dotnet run` and the test suite work with zero broker:

```bash
docker compose up -d
dotnet user-secrets set "RabbitMq:HostName" "localhost" --project QuotesApi
```
