using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;
using QuotesApi.Models;

namespace QuotesApi.Repositories;

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
