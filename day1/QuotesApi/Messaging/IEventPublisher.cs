namespace QuotesApi.Messaging;

public interface IEventPublisher
{
    // routingKey is a dotted topic pattern (e.g. "quote.created") - RabbitMQ's
    // topic exchange is what turns that single publish into delivery to every
    // queue whose binding pattern matches it, with zero fan-out code here.
    Task PublishAsync(string routingKey, EventEnvelope envelope, CancellationToken ct);
}
