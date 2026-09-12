using Microsoft.EntityFrameworkCore;
using QuotesApi.Data;
using QuotesApi.Dtos;

namespace QuotesApi.Repositories;

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
