namespace QuotesApi.Messaging;

// The whole wire format: one JSON object as the message body, no broker-specific
// headers relied on for anything semantic. MessageId is the idempotency key both
// the consumer's dedupe table (ProcessedMessage) and the Day 20 outbox relay key
// off - the relay reuses the outbox row's own Id as MessageId so a message
// republished after a relay crash still dedupes correctly downstream.
public record EventEnvelope(Guid MessageId, string Type, string Payload, DateTimeOffset OccurredAt);
