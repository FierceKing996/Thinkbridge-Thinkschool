using System.Text.Json;
using RabbitMQ.Client;

namespace QuotesApi.Messaging;

// One short-lived channel per publish. RabbitMQ.Client's IChannel isn't meant
// to be shared across concurrent publishers (it serializes frame writes), and
// the outbox relay is the only caller here, publishing one message at a time
// off a background loop - so "open, publish, close" costs nothing that
// pooling would meaningfully save, and it sidesteps ever needing to reason
// about channel-level error recovery.
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

        await channel.BasicPublishAsync(
            options.Exchange, routingKey, mandatory: false, properties, body, cancellationToken: ct);
    }
}
