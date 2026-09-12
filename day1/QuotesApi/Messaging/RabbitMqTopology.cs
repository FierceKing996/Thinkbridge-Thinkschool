using RabbitMQ.Client;

namespace QuotesApi.Messaging;

// Declares the whole topology - exchange, both subscription queues, the DLX,
// and the DLQ. Declare calls in RabbitMQ are idempotent (same name + same
// arguments = no-op) so it's safe for every hosted service that needs the
// topology to just declare it again on its own channel at startup, in
// whatever order the host happens to start them in - no separate
// "provisioning" step or startup ordering to get right.
public static class RabbitMqTopology
{
    public static async Task DeclareAsync(IChannel channel, RabbitMqOptions options, CancellationToken ct)
    {
        await channel.ExchangeDeclareAsync(options.Exchange, ExchangeType.Topic, durable: true, cancellationToken: ct);

        // The dead-letter exchange is a plain fanout: anything a queue dead-letters
        // (via the x-dead-letter-exchange argument below) lands here and is routed,
        // unconditionally, to the one DLQ - good enough for "prove poison messages
        // get caught somewhere inspectable" without a second topic-routing scheme
        // to maintain in parallel with the main one.
        await channel.ExchangeDeclareAsync(options.DeadLetterExchange, ExchangeType.Fanout, durable: true, cancellationToken: ct);
        await channel.QueueDeclareAsync(options.DeadLetterQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: null, cancellationToken: ct);
        await channel.QueueBindAsync(options.DeadLetterQueue, options.DeadLetterExchange, routingKey: "", cancellationToken: ct);

        var queueArgs = new Dictionary<string, object?>
        {
            ["x-dead-letter-exchange"] = options.DeadLetterExchange,
        };

        await channel.QueueDeclareAsync(options.SearchIndexQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: queueArgs, cancellationToken: ct);
        await channel.QueueBindAsync(options.SearchIndexQueue, options.Exchange, "quote.*", cancellationToken: ct);

        await channel.QueueDeclareAsync(options.AuditLogQueue, durable: true, exclusive: false, autoDelete: false,
            arguments: queueArgs, cancellationToken: ct);
        await channel.QueueBindAsync(options.AuditLogQueue, options.Exchange, "quote.*", cancellationToken: ct);
    }
}
