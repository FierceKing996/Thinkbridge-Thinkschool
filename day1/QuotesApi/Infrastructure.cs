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
            // GET /api/reports/authors groups by Author for every request -
            // without this, that GROUP BY (and any WHERE Author = ...) is a
            // full table scan.
            builder.HasIndex(q => q.Author);
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
    Task<List<Quote>> GetByIdsAsync(IReadOnlyCollection<int> ids, CancellationToken ct);
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

// ---- Write model: command + handler for "add a quote to a collection" ----
// Normalized (FK-shaped: CollectionId + QuoteId, no denormalized author/text
// along for the ride) and validated - every mutation goes through the
// Collection aggregate, so duplicate-quote and max-items-per-collection
// invariants can't be bypassed.
public record AddCollectionItemCommand(int CollectionId, int QuoteId);

public class AddCollectionItemResult
{
    public Collection? Collection { get; }
    public bool NotFound { get; }
    public string? Error { get; }
    public bool Succeeded => Collection is not null;

    private AddCollectionItemResult(Collection? collection, bool notFound, string? error)
    {
        Collection = collection;
        NotFound = notFound;
        Error = error;
    }

    public static AddCollectionItemResult Success(Collection collection) => new(collection, false, null);
    public static AddCollectionItemResult CollectionNotFound() => new(null, true, null);
    public static AddCollectionItemResult Fail(string error) => new(null, false, error);
}

public interface IAddCollectionItemCommandHandler
{
    Task<AddCollectionItemResult> HandleAsync(AddCollectionItemCommand command, CancellationToken ct);
}

public class AddCollectionItemCommandHandler(ICollectionRepository repo, IClock clock) : IAddCollectionItemCommandHandler
{
    public async Task<AddCollectionItemResult> HandleAsync(AddCollectionItemCommand command, CancellationToken ct)
    {
        var collection = await repo.GetByIdAsync(command.CollectionId, ct);
        if (collection is null) return AddCollectionItemResult.CollectionNotFound();

        try
        {
            collection.AddItem(command.QuoteId, clock.UtcNow);
        }
        catch (DomainException ex)
        {
            return AddCollectionItemResult.Fail(ex.Message);
        }

        await repo.UpdateAsync(collection, ct);
        return AddCollectionItemResult.Success(collection);
    }
}

// ---- Read model: denormalized query for the collection-detail screen ----
// Deliberately bypasses the Collection aggregate entirely - the screen doesn't
// need invariant-checked domain objects, it needs a flat Author/Text/AddedAt
// shape. One projection query, no repository, no private-setter hydration.
public interface ICollectionQueries
{
    Task<CollectionDetailResponse?> GetDetailAsync(int id, CancellationToken ct);
}

public class CollectionQueries(AppDbContext db) : ICollectionQueries
{
    public async Task<CollectionDetailResponse?> GetDetailAsync(int id, CancellationToken ct)
    {
        var header = await db.Collections
            .Where(c => c.Id == id)
            .Select(c => new { c.Id, c.Name, c.OwnerId })
            .FirstOrDefaultAsync(ct);
        if (header is null) return null;

        var items = await (
            from ci in db.Collections.Where(c => c.Id == id).SelectMany(c => c.Items)
            join q in db.Quotes on ci.QuoteId equals q.Id
            select new CollectionItemDetail(ci.QuoteId, q.Author, q.Text, ci.AddedAt)
        ).ToListAsync(ct);

        return new CollectionDetailResponse(header.Id, header.Name, header.OwnerId, items);
    }
}

public record CreateCollectionRequest(string Name, int OwnerId);
public record AddCollectionItemRequest(int QuoteId);
public record CollectionItemDetail(int QuoteId, string Author, string Text, DateTimeOffset AddedAt);
public record CollectionDetailResponse(int Id, string Name, int OwnerId, IReadOnlyList<CollectionItemDetail> Items);

public record AuthorSummary(string Author, int QuoteCount, string? MostRecentQuoteText);

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