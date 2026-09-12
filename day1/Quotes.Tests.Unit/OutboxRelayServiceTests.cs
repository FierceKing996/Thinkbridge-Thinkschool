using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using QuotesApi.Data;
using QuotesApi.Messaging;
using QuotesApi.Models;
using QuotesApi.Repositories;
using QuotesApi.Services;
using Xunit;

namespace Quotes.Tests.Unit;

// Day 20: proves the outbox's core promise without a real broker - "no message
// is lost if the publish step crashes", and (because Day 19's consumers are
// idempotent) no message is double-processed either, even though the relay's
// retry after that crash republishes it.
//
// The crash scenario modeled here: OutboxRelayService.RelayBatchAsync calls
// IEventPublisher.PublishAsync, then calls OutboxMessage.MarkProcessed and
// SaveChangesAsync. A crash between those two steps - process killed, pod
// evicted, whatever - means the message was genuinely published, but the row
// is still sitting there with ProcessedAt == null. The test recreates exactly
// that: publish once "manually" (standing in for the relay's crashed attempt),
// leave the row unprocessed, then run the real relay - which republishes the
// same row under the same MessageId. A fake consumer with its own
// ProcessedMessage dedupe check (identical in shape to
// QuoteAuditLogConsumerService's) proves the duplicate delivery doesn't turn
// into a duplicate side effect.
public class OutboxRelayServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly AppDbContext _db;
    private readonly IClock _clock = Substitute.For<IClock>();

    public OutboxRelayServiceTests()
    {
        _connection.Open();
        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(_connection).Options;
        _db = new AppDbContext(options);
        _db.Database.EnsureCreated();
        _clock.UtcNow.Returns(DateTimeOffset.UtcNow);

        // Quote.CreatedByUserId is a real FK - a quote needs an owning user to exist
        // before it can be inserted, unrelated to anything this test is about.
        _db.Users.Add(new User { Email = "author@example.com", PasswordHash = "n/a", Scopes = "quotes.write" });
        _db.SaveChanges();
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    // Fake, idempotent "consumer" living entirely inside the test: publishing to
    // it applies the exact same dedupe-by-MessageId check the real RabbitMQ
    // consumers use, against the same ProcessedMessages table, so it exercises
    // the real idempotency contract rather than a simplified stand-in for it.
    private class IdempotentFakePublisher(AppDbContext db, IClock clock) : IEventPublisher
    {
        public const string ConsumerName = "fake-consumer";
        public int PublishAttempts { get; private set; }

        public async Task PublishAsync(string routingKey, EventEnvelope envelope, CancellationToken ct)
        {
            PublishAttempts++;

            var alreadyProcessed = await db.ProcessedMessages
                .AnyAsync(m => m.Consumer == ConsumerName && m.MessageId == envelope.MessageId, ct);
            if (alreadyProcessed) return;

            db.AuditLogEntries.Add(AuditLogEntry.Create(envelope.MessageId, envelope.Type, envelope.Payload, clock.UtcNow));
            db.ProcessedMessages.Add(ProcessedMessage.Create(envelope.MessageId, ConsumerName, clock.UtcNow));
            await db.SaveChangesAsync(ct);
        }
    }

    private ServiceProvider BuildServices(IEventPublisher publisher)
    {
        var services = new ServiceCollection();
        services.AddSingleton(_db);
        services.AddSingleton(publisher);
        services.AddSingleton(_clock);
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task CreateAsync_WritesQuoteAndOutboxRow_InSameTransaction()
    {
        var repo = new QuoteRepository(_db, NullLogger<QuoteRepository>.Instance, _clock);
        var quote = Quote.Create("Author", "Text", createdByUserId: 1).Quote!;

        await repo.CreateAsync(quote, CancellationToken.None);

        var outboxRows = await _db.OutboxMessages.ToListAsync();
        outboxRows.Should().HaveCount(1);
        outboxRows[0].Type.Should().Be(QuoteEvents.QuoteCreatedType);
        outboxRows[0].ProcessedAt.Should().BeNull();
    }

    [Fact]
    public async Task RelayBatch_AfterCrashBetweenPublishAndMarkProcessed_RedeliversWithoutDuplicatingSideEffect()
    {
        var repo = new QuoteRepository(_db, NullLogger<QuoteRepository>.Instance, _clock);
        var quote = Quote.Create("Author", "Text", createdByUserId: 1).Quote!;
        await repo.CreateAsync(quote, CancellationToken.None);

        var outboxMessage = await _db.OutboxMessages.SingleAsync();
        var publisher = new IdempotentFakePublisher(_db, _clock);

        // Step 1: simulate the relay's first attempt - it publishes successfully,
        // then crashes before MarkProcessed/SaveChangesAsync ever run. The row is
        // left exactly as a real crash would leave it: unprocessed, even though the
        // message was genuinely delivered once already.
        var envelope = new EventEnvelope(outboxMessage.Id, outboxMessage.Type, outboxMessage.Payload, outboxMessage.OccurredAt);
        await publisher.PublishAsync(QuoteEvents.QuoteCreatedType, envelope, CancellationToken.None);

        (await _db.OutboxMessages.SingleAsync()).ProcessedAt.Should().BeNull("the crash happened before MarkProcessed committed");
        publisher.PublishAttempts.Should().Be(1);

        // Step 2: the process restarts; the real relay runs its normal loop
        // against the still-unprocessed row and republishes it under the same
        // MessageId - proving the message was never lost.
        using var services = BuildServices(publisher);
        var relay = new OutboxRelayService(
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<OutboxRelayService>.Instance);

        await relay.RelayBatchAsync(CancellationToken.None);

        publisher.PublishAttempts.Should().Be(2, "the relay redelivers the row it didn't get to mark processed");
        (await _db.OutboxMessages.SingleAsync()).ProcessedAt.Should().NotBeNull("this attempt completed both the publish and the mark");

        // Proving no duplication: despite two publish attempts for the same
        // MessageId, the idempotent consumer's dedupe check means only one
        // ProcessedMessage/AuditLogEntry pair was ever written.
        (await _db.ProcessedMessages.CountAsync(m => m.Consumer == IdempotentFakePublisher.ConsumerName)).Should().Be(1);
        (await _db.AuditLogEntries.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task RelayBatch_PublisherThrows_LeavesMessageUnprocessedForRetry()
    {
        var repo = new QuoteRepository(_db, NullLogger<QuoteRepository>.Instance, _clock);
        var quote = Quote.Create("Author", "Text", createdByUserId: 1).Quote!;
        await repo.CreateAsync(quote, CancellationToken.None);

        var failingPublisher = Substitute.For<IEventPublisher>();
        failingPublisher
            .PublishAsync(Arg.Any<string>(), Arg.Any<EventEnvelope>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("broker unreachable"));

        using var services = BuildServices(failingPublisher);
        var relay = new OutboxRelayService(
            services.GetRequiredService<IServiceScopeFactory>(), NullLogger<OutboxRelayService>.Instance);

        // A publish failure for one message must not throw out of the batch -
        // it's caught and logged so the next poll can retry.
        await relay.RelayBatchAsync(CancellationToken.None);

        (await _db.OutboxMessages.SingleAsync()).ProcessedAt.Should().BeNull();
    }
}
