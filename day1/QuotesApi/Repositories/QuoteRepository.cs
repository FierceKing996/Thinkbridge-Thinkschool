using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;
using QuotesApi.Messaging;
using QuotesApi.Models;
using QuotesApi.Services;

namespace QuotesApi.Repositories;

public class QuoteRepository(AppDbContext db, ILogger<QuoteRepository> logger, IClock clock) : IQuoteRepository
{
    public async Task<List<Quote>> GetPagedAsync(int page, int size, CancellationToken ct)
    {
        logger.LogInformation("Fetching quotes page {Page} with size {Size}", page, size);
        return await db.Quotes
            .Where(q => !q.IsDeleted)
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync(ct);
    }

    public async Task<Quote?> GetByIdAsync(int id, CancellationToken ct)
    {
        logger.LogInformation("Fetching quote with ID {Id}", id);
        var quote = await db.Quotes.FindAsync([id], ct);
        return quote is { IsDeleted: false } ? quote : null;
    }

    // Single WHERE Id IN (...) round trip for a whole batch of ids - the fix for
    // the N+1 that CollectionEndpoints' GET /{id} used to do (one GetByIdAsync
    // call per collection item instead of one call for the whole collection).
    public async Task<List<Quote>> GetByIdsAsync(IReadOnlyCollection<int> ids, CancellationToken ct)
    {
        logger.LogInformation("Fetching {Count} quotes by ID", ids.Count);
        return await db.Quotes
            .Where(q => ids.Contains(q.Id) && !q.IsDeleted)
            .ToListAsync(ct);
    }

    // Day 20 outbox: the quote row and its outbox row commit together or not at
    // all. Quote.Id is database-generated (IDENTITY/AUTOINCREMENT), so it isn't
    // known until the first SaveChangesAsync returns - the outbox payload can only
    // be built after that - which is exactly why this needs an explicit
    // transaction spanning both SaveChangesAsync calls rather than relying on the
    // "one SaveChangesAsync = one transaction" default: two calls here means two
    // transactions unless this wraps them in one itself. If the second
    // SaveChangesAsync throws, the transaction rolls back the quote insert too -
    // the alternative (quote committed, no outbox row) is exactly the divergence
    // the outbox pattern exists to rule out.
    public async Task<Quote> CreateAsync(Quote quote, CancellationToken ct)
    {
        logger.LogInformation("Creating new quote by {Author}", quote.Author);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        db.Quotes.Add(quote);
        await db.SaveChangesAsync(ct);

        var payload = new QuoteCreatedPayload(quote.Id, quote.Author, quote.Text, quote.CreatedByUserId);
        db.OutboxMessages.Add(OutboxMessage.For(QuoteEvents.QuoteCreatedType, payload, clock.UtcNow));
        await db.SaveChangesAsync(ct);

        await transaction.CommitAsync(ct);
        return quote;
    }

    // Soft delete: Quote has no public setters and no update method, so the only
    // way to retire one is to flip the flag through the aggregate's own Delete().
    public async Task<bool> DeleteAsync(int id, CancellationToken ct)
    {
        logger.LogInformation("Deleting quote with ID {Id}", id);
        var quote = await db.Quotes.FindAsync([id], ct);
        if (quote is null || quote.IsDeleted) return false;

        quote.Delete();
        await db.SaveChangesAsync(ct);
        return true;
    }
}
