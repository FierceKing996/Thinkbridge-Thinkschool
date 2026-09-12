using QuotesApi.Models;

namespace QuotesApi.Repositories;

public interface IRefreshTokenRepository
{
    Task AddAsync(RefreshToken token, CancellationToken ct);
    Task<RefreshToken?> GetByTokenHashAsync(string tokenHash, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}
