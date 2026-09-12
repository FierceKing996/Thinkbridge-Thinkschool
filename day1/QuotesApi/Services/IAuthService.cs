using QuotesApi.Dtos;

namespace QuotesApi.Services;

public interface IAuthService
{
    Task<LoginResponse?> LoginAsync(string email, string password, CancellationToken ct);
    Task<RefreshResult> RefreshAsync(string rawRefreshToken, CancellationToken ct);
    Task LogoutAsync(string rawRefreshToken, CancellationToken ct);
}
