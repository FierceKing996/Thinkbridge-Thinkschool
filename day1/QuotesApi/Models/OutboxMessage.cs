using System.Text.Json;

namespace QuotesApi.Models;

// Day 20: the transactional outbox. A row here is written in the same
// SaveChangesAsync call (== the same DB transaction) as the domain change it
// describes - see QuoteRepository.CreateAsync - so "the quote was created" and
// "an event describing it exists to be published" can never diverge: either
// both commit or neither does. OutboxRelayService is the separate process that
// turns unprocessed rows into RabbitMQ publishes; ProcessedAt is null until a
// publish attempt actually succeeds.
public class OutboxMessage
{
    public Guid Id { get; private set; }
    public string Type { get; private set; } = string.Empty;
    public string Payload { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset? ProcessedAt { get; private set; }

    private OutboxMessage() { }

    public static OutboxMessage For<T>(string type, T payload, DateTimeOffset occurredAt) =>
        new()
        {
            Id = Guid.NewGuid(),
            Type = type,
            Payload = JsonSerializer.Serialize(payload),
            OccurredAt = occurredAt,
        };

    // Called only after the relay's publish call has actually returned
    // successfully - never before, and never speculatively. That ordering (publish,
    // then mark) rather than the reverse is the entire crash-safety property: see
    // DAY20_OUTBOX.md for the crash scenario this is built to survive.
    public void MarkProcessed(DateTimeOffset processedAt) => ProcessedAt = processedAt;
}
