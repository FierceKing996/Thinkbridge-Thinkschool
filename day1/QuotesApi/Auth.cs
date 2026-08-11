using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace QuotesApi;

public class JwtOptions
{
    public const string SectionName = "Jwt";

    public required string Issuer { get; set; }
    public required string Audience { get; set; }

    // Base64-encoded, must decode to >= 32 bytes (256 bits) for HS256.
    public required string SigningKey { get; set; }

    public int AccessTokenExpirySeconds { get; set; } = 900;
}

public record LoginRequest(string Email, string Password);

public class LoginResponse
{
    [JsonPropertyName("access_token")]
    public required string AccessToken { get; init; }

    [JsonPropertyName("refresh_token")]
    public required string RefreshToken { get; init; }

    [JsonPropertyName("expires_in")]
    public required int ExpiresIn { get; init; }
}

public interface ITokenService
{
    (string AccessToken, int ExpiresIn) CreateAccessToken(User user);
    string CreateRefreshToken();
}

// Singleton: reads its signing key from config once at construction and does no
// per-call I/O - nothing here needs to be re-created per scope or per call.
public class TokenService : ITokenService
{
    private readonly JwtOptions _options;
    private readonly SymmetricSecurityKey _signingKey;

    public TokenService(IOptions<JwtOptions> options)
    {
        _options = options.Value;
        _signingKey = new SymmetricSecurityKey(Convert.FromBase64String(_options.SigningKey));
    }

    public (string AccessToken, int ExpiresIn) CreateAccessToken(User user)
    {
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        var credentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            expires: DateTime.UtcNow.AddSeconds(_options.AccessTokenExpirySeconds),
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), _options.AccessTokenExpirySeconds);
    }

    // Opaque, not a JWT - this exercise only asks the login response to carry a
    // refresh token, not a working refresh/rotation endpoint, so there is nowhere
    // this needs to be parsed or validated (yet).
    public string CreateRefreshToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
}
