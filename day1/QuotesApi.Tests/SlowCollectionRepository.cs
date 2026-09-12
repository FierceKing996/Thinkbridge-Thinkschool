using QuotesApi.Auth;
using QuotesApi.Data;
using QuotesApi.Dtos;
using QuotesApi.Models;
using QuotesApi.Repositories;
using QuotesApi.Services;

namespace QuotesApi.Tests;

// Wraps the real repository with an artificial delay before GetByIdAsync so tests
// have a reliable window to cancel mid-request instead of racing an operation that
// finishes in microseconds against an in-memory SQLite database. Delaying via
// Task.Delay(delay, ct) also directly proves the token that reaches this class is
// the live one - if cancellation didn't actually flow this far down, the delay
// would just run to completion instead of throwing early.
public class SlowCollectionRepository : ICollectionRepository
{
    private readonly ICollectionRepository _inner;
    private readonly TimeSpan _delay;

    public SlowCollectionRepository(ICollectionRepository inner, TimeSpan delay)
    {
        _inner = inner;
        _delay = delay;
    }

    public async Task<Collection?> GetByIdAsync(int id, CancellationToken ct)
    {
        await Task.Delay(_delay, ct);
        return await _inner.GetByIdAsync(id, ct);
    }

    public Task<Collection> AddAsync(Collection collection, CancellationToken ct) =>
        _inner.AddAsync(collection, ct);

    public Task UpdateAsync(Collection collection, CancellationToken ct) =>
        _inner.UpdateAsync(collection, ct);

    public Task<bool> DeleteAsync(int id, CancellationToken ct) =>
        _inner.DeleteAsync(id, ct);
}
