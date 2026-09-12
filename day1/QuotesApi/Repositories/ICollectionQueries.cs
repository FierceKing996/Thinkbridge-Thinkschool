using QuotesApi.Dtos;

namespace QuotesApi.Repositories;

// ---- Read model: denormalized query for the collection-detail screen ----
// Deliberately bypasses the Collection aggregate entirely - the screen doesn't
// need invariant-checked domain objects, it needs a flat Author/Text/AddedAt
// shape. One projection query, no repository, no private-setter hydration.
public interface ICollectionQueries
{
    Task<CollectionDetailResponse?> GetDetailAsync(int id, CancellationToken ct);
}
