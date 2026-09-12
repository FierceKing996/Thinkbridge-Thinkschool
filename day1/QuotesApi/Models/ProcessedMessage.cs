namespace QuotesApi.Models;

// One row per (consumer, message) pair that has been successfully handled.
// RabbitMQ only guarantees at-least-once delivery - the outbox relay
// (Day 20) can and will redeliver a MessageId it already published once, if
// it crashes between the publish call succeeding and marking the outbox row
// processed. This table is what turns that at-least-once delivery into
// effectively-once processing: a handler checks for an existing row before
// doing any side effect, and skips (but still acks) if one exists.
public class ProcessedMessage
{
    public int Id { get; private set; }
    public Guid MessageId { get; private set; }
    public string Consumer { get; private set; } = string.Empty;
    public DateTimeOffset ProcessedAt { get; private set; }

    private ProcessedMessage() { }

    public static ProcessedMessage Create(Guid messageId, string consumer, DateTimeOffset processedAt) =>
        new() { MessageId = messageId, Consumer = consumer, ProcessedAt = processedAt };
}
