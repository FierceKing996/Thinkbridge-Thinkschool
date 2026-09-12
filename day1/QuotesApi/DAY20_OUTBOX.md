# Day 20 — The outbox pattern

## The outbox table

`Models/OutboxMessage.cs`:

```csharp
public class OutboxMessage
{
    public Guid Id { get; private set; }
    public string Type { get; private set; } = string.Empty;
    public string Payload { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; private set; }
    public DateTimeOffset? ProcessedAt { get; private set; }

    public static OutboxMessage For<T>(string type, T payload, DateTimeOffset occurredAt) => new()
    {
        Id = Guid.NewGuid(),
        Type = type,
        Payload = JsonSerializer.Serialize(payload),
        OccurredAt = occurredAt,
    };

    public void MarkProcessed(DateTimeOffset processedAt) => ProcessedAt = processedAt;
}
```

The domain write + the outbox row, in one transaction (`Repositories/QuoteRepository.cs`):

```csharp
public async Task<Quote> CreateAsync(Quote quote, CancellationToken ct)
{
    await using var transaction = await db.Database.BeginTransactionAsync(ct);

    db.Quotes.Add(quote);
    await db.SaveChangesAsync(ct); // Quote.Id is IDENTITY - only known after this

    var payload = new QuoteCreatedPayload(quote.Id, quote.Author, quote.Text, quote.CreatedByUserId);
    db.OutboxMessages.Add(OutboxMessage.For(QuoteEvents.QuoteCreatedType, payload, clock.UtcNow));
    await db.SaveChangesAsync(ct);

    await transaction.CommitAsync(ct);
    return quote;
}
```

An explicit transaction, not just "one `SaveChangesAsync` call", because
`Quote.Id` is database-generated — the outbox payload can't be built until the
first `SaveChangesAsync` returns it, which means this needs *two* `SaveChangesAsync`
calls, and two calls are two transactions unless wrapped like this. If the second
call throws, the transaction rolls back the quote insert too, ruling out the
"quote committed, no outbox row" divergence the pattern exists to prevent.

## The relay

`Services/OutboxRelayService.cs` — poll, publish, *then* mark processed:

```csharp
foreach (var message in pending)
{
    try
    {
        var envelope = new EventEnvelope(message.Id, message.Type, message.Payload, message.OccurredAt);
        await publisher.PublishAsync(RoutingKeyFor(message.Type), envelope, ct);

        message.MarkProcessed(clock.UtcNow);
        await db.SaveChangesAsync(ct);
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Failed to publish outbox message {MessageId}; leaving unprocessed for retry.", message.Id);
    }
}
```

## The crash scenario tested, and why no message is lost or duplicated

**Scenario:** the relay's `PublishAsync` call succeeds — the message is genuinely on
the broker — but the process is killed (crash, pod eviction, deploy) before
`MarkProcessed` + `SaveChangesAsync` commit. The row is left exactly as a real crash
would leave it: unprocessed, even though it was already delivered once.

**Why no message is lost:** the relay's poll query is `WHERE ProcessedAt IS NULL`.
A row only ever gets `ProcessedAt` set *after* a successful publish, never before and
never speculatively — so a crash between those two steps can only leave a message
looking "not yet sent" (safe, it gets retried) and can never leave one looking "sent"
when it wasn't. At-least-once delivery, by construction.

**Why no message is duplicated (at the processing level):** the relay reuses the
outbox row's own `Id` as the republished message's `MessageId`. Every consumer
(Day 19) checks a `ProcessedMessage` row keyed on `(Consumer, MessageId)` before
doing its side effect. So the redelivered copy carries the *same* MessageId as the
one already processed, the dedupe check finds it, and the side effect (indexing,
audit row, whatever) runs exactly once even though the message was delivered twice.

**Test:** `Quotes.Tests.Unit/OutboxRelayServiceTests.cs`,
`RelayBatch_AfterCrashBetweenPublishAndMarkProcessed_RedeliversWithoutDuplicatingSideEffect`
recreates this precisely, with no real broker involved:

1. Create a quote → one outbox row, unprocessed.
2. Publish it "manually" via a fake idempotent publisher (standing in for the
   relay's crashed first attempt) — **do not** mark it processed. Assert the row
   is still unprocessed and the publisher was called once.
3. Run the real `OutboxRelayService.RelayBatchAsync` against the same unprocessed
   row. It republishes under the same `MessageId` and this time successfully marks
   it processed. Assert the publisher was called a second time.
4. Assert that despite two publish attempts, exactly **one** `ProcessedMessage` row
   and **one** `AuditLogEntry` row exist — the dedupe check absorbed the duplicate.

A second test, `RelayBatch_PublisherThrows_LeavesMessageUnprocessedForRetry`, proves
the simpler half of the same guarantee: if the publish call itself throws (broker
unreachable), the row is left unprocessed for the next poll rather than the
exception propagating out of the batch.

```
Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3 - OutboxRelayServiceTests
```
