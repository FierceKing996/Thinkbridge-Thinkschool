using Microsoft.EntityFrameworkCore;

namespace QuotesApi;

public class Quote
{
    public int Id { get; set; }
    public required string Author { get; set; }
    public required string Text { get; set; }
}

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Quote> Quotes => Set<Quote>();
}

public interface IQuoteRepository
{
    Task<List<Quote>> GetPagedAsync(int page, int size, CancellationToken ct);
    Task<Quote?> GetByIdAsync(int id, CancellationToken ct);
    Task<Quote> CreateAsync(Quote quote, CancellationToken ct);
    Task<bool> DeleteAsync(int id, CancellationToken ct);
}

public class QuoteRepository(AppDbContext db, ILogger<QuoteRepository> logger) : IQuoteRepository
{
    public async Task<List<Quote>> GetPagedAsync(int page, int size, CancellationToken ct)
    {
        logger.LogInformation("Fetching quotes page {Page} with size {Size}", page, size);
        return await db.Quotes
            .Skip((page - 1) * size)
            .Take(size)
            .ToListAsync(ct);
    }

    public async Task<Quote?> GetByIdAsync(int id, CancellationToken ct)
    {
        logger.LogInformation("Fetching quote with ID {Id}", id);
        return await db.Quotes.FindAsync([id], ct);
    }

    public async Task<Quote> CreateAsync(Quote quote, CancellationToken ct)
    {
        logger.LogInformation("Creating new quote by {Author}", quote.Author);
        db.Quotes.Add(quote);
        await db.SaveChangesAsync(ct);
        return quote;
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct)
    {
        logger.LogInformation("Deleting quote with ID {Id}", id);
        var quote = await db.Quotes.FindAsync([id], ct);
        if (quote is null) return false;

        db.Quotes.Remove(quote);
        await db.SaveChangesAsync(ct);
        return true;
    }
}

public record CreateQuoteRequest(string Author, string Text);