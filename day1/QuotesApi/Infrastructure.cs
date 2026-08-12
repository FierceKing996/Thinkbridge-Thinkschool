using Microsoft.EntityFrameworkCore;

namespace QuotesApi;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Quote> Quotes => Set<Quote>();
    public DbSet<Collection> Collections => Set<Collection>();
    public DbSet<User> Users => Set<User>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Quote>(builder =>
        {
            builder.Property(q => q.Author).IsRequired().HasMaxLength(Quote.MaxAuthorLength);
            builder.Property(q => q.Text).IsRequired().HasMaxLength(Quote.MaxTextLength);
            builder.Property(q => q.IsDeleted).IsRequired();
            builder.Property(q => q.CreatedByUserId).IsRequired();
            builder.HasOne<User>().WithMany().HasForeignKey(q => q.CreatedByUserId);
        });

        modelBuilder.Entity<User>(builder =>
        {
            builder.Property(u => u.Email).IsRequired();
            builder.Property(u => u.PasswordHash).IsRequired();
            builder.Property(u => u.Scopes).IsRequired();
            builder.HasIndex(u => u.Email).IsUnique();
        });

        modelBuilder.Entity<RefreshToken>(builder =>
        {
            builder.Property(t => t.TokenHash).IsRequired();
            builder.HasIndex(t => t.TokenHash).IsUnique();
            builder.Property(t => t.UserId).IsRequired();
            builder.Property(t => t.ExpiresAt).IsRequired();
            builder.HasOne<User>().WithMany().HasForeignKey(t => t.UserId);
        });

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

    public async Task<Quote> CreateAsync(Quote quote, CancellationToken ct)
    {
        logger.LogInformation("Creating new quote by {Author}", quote.Author);
        db.Quotes.Add(quote);
        await db.SaveChangesAsync(ct);
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

public interface IUserRepository
{
    Task<User?> GetByIdAsync(int id, CancellationToken ct);
    Task<User?> GetByEmailAsync(string email, CancellationToken ct);
}

public class UserRepository(AppDbContext db) : IUserRepository
{
    public Task<User?> GetByIdAsync(int id, CancellationToken ct) =>
        db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);

    public Task<User?> GetByEmailAsync(string email, CancellationToken ct) =>
        db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
}

public interface IRefreshTokenRepository
{
    Task AddAsync(RefreshToken token, CancellationToken ct);
    Task<RefreshToken?> GetByTokenHashAsync(string tokenHash, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}

public class RefreshTokenRepository(AppDbContext db) : IRefreshTokenRepository
{
    public async Task AddAsync(RefreshToken token, CancellationToken ct) =>
        await db.RefreshTokens.AddAsync(token, ct);

    public Task<RefreshToken?> GetByTokenHashAsync(string tokenHash, CancellationToken ct) =>
        db.RefreshTokens.FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);

    public Task SaveChangesAsync(CancellationToken ct) => db.SaveChangesAsync(ct);
}