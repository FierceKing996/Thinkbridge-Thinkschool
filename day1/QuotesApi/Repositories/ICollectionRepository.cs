using QuotesApi.Models;

namespace QuotesApi.Repositories;

public interface ICollectionRepository
{
    Task<Collection?> GetByIdAsync(int id, CancellationToken ct);
    Task<Collection> AddAsync(Collection collection, CancellationToken ct);
    Task UpdateAsync(Collection collection, CancellationToken ct);
    Task<bool> DeleteAsync(int id, CancellationToken ct);
}
