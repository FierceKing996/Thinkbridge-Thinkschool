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
    public DbSet<Collection> Collections => Set<Collection>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Collection>(builder =>
        {
            builder.HasKey(c => c.Id);
            builder.Property(c => c.Name).IsRequired().HasMaxLength(Collection.MaxNameLength);
            builder.Property(c => c.OwnerId).IsRequired();

            // Items is exposed only as IReadOnlyList with a private backing field;
            // EF must write through the field, not the (nonexistent) setter.
            builder.Navigation(c => c.Items).UsePropertyAccessMode(PropertyAccessMode.Field);

            builder.OwnsMany(c => c.Items, items =>
            {
                items.WithOwner().HasForeignKey("CollectionId");
                items.Property(i => i.QuoteId).IsRequired().ValueGeneratedNever();
                items.Property(i => i.AddedAt).IsRequired();
                items.HasKey("CollectionId", "QuoteId");
                items.ToTable("CollectionItems");
            });
        });
    }
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

public interface ICollectionRepository
{
    Task<Collection?> GetByIdAsync(int id, CancellationToken ct);
    Task<Collection> AddAsync(Collection collection, CancellationToken ct);
    Task UpdateAsync(Collection collection, CancellationToken ct);
    Task<bool> DeleteAsync(int id, CancellationToken ct);
}

public class CollectionRepository(AppDbContext db, ILogger<CollectionRepository> logger) : ICollectionRepository
{
    public async Task<Collection?> GetByIdAsync(int id, CancellationToken ct)
    {
        logger.LogInformation("Fetching collection {Id}", id);
        return await db.Collections.Include(c => c.Items).FirstOrDefaultAsync(c => c.Id == id, ct);
    }

    public async Task<Collection> AddAsync(Collection collection, CancellationToken ct)
    {
        logger.LogInformation("Creating collection {Name} for owner {OwnerId}", collection.Name, collection.OwnerId);
        db.Collections.Add(collection);
        await db.SaveChangesAsync(ct);
        return collection;
    }

    public async Task UpdateAsync(Collection collection, CancellationToken ct)
    {
        logger.LogInformation("Updating collection {Id}", collection.Id);
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct)
    {
        logger.LogInformation("Deleting collection {Id}", id);
        var collection = await db.Collections.FindAsync([id], ct);
        if (collection is null) return false;

        db.Collections.Remove(collection);
        await db.SaveChangesAsync(ct);
        return true;
    }
}

public record CreateCollectionRequest(string Name, int OwnerId);
public record AddCollectionItemRequest(int QuoteId);