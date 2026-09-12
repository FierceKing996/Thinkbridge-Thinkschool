using QuotesApi.Models;

namespace QuotesApi.Repositories;

public interface IQuoteRepository
{
    Task<List<Quote>> GetPagedAsync(int page, int size, CancellationToken ct);
    Task<Quote?> GetByIdAsync(int id, CancellationToken ct);
    Task<List<Quote>> GetByIdsAsync(IReadOnlyCollection<int> ids, CancellationToken ct);
    Task<Quote> CreateAsync(Quote quote, CancellationToken ct);
    Task<bool> DeleteAsync(int id, CancellationToken ct);
}
