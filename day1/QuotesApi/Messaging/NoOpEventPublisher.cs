namespace QuotesApi.Messaging;

// Registered instead of RabbitMqEventPublisher when RabbitMq:HostName isn't
// configured (see ServiceCollectionExtensions). This is what lets the Day 20
// outbox relay run in every environment - local `dotnet run`, the test suite,
// CI - without a broker: rows are written and read back exactly the same, they
// just never get marked processed, which is the correct, honest behavior for
// "no broker configured" rather than the relay silently pretending to deliver.
public class NoOpEventPublisher(ILogger<NoOpEventPublisher> logger) : IEventPublisher
{
    public Task PublishAsync(string routingKey, EventEnvelope envelope, CancellationToken ct)
    {
        logger.LogDebug(
            "RabbitMq not configured - dropping publish of {MessageType} ({MessageId}) to {RoutingKey}.",
            envelope.Type, envelope.MessageId, routingKey);
        throw new InvalidOperationException(
            "RabbitMq is not configured (RabbitMq:HostName is unset). Publishing is unavailable.");
    }
}
