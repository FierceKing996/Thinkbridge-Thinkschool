using QuotesApi.Models;

namespace QuotesApi.Services;

public interface ITokenService
{
    (string AccessToken, int ExpiresIn) CreateAccessToken(User user);
    string CreateRefreshToken();
    string HashRefreshToken(string rawToken);
}
