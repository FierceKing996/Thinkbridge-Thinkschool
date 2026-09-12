namespace QuotesApi.Models;

// The observable side effect of the audit-log subscription (Day 19's "second
// subscription" to the quotes.events topic) - a durable row proves the fan-out
// actually reached a second, independent consumer, not just the search-index one.
public class AuditLogEntry
{
    public int Id { get; private set; }
    public Guid MessageId { get; private set; }
    public string EventType { get; private set; } = string.Empty;
    public string Payload { get; private set; } = string.Empty;
    public DateTimeOffset RecordedAt { get; private set; }

    private AuditLogEntry() { }

    public static AuditLogEntry Create(Guid messageId, string eventType, string payload, DateTimeOffset recordedAt) =>
        new() { MessageId = messageId, EventType = eventType, Payload = payload, RecordedAt = recordedAt };
}
