using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using QuotesApi.Auth;
using QuotesApi.Models;

namespace QuotesApi.Services;

// Singleton: reads its signing key from config once at construction and does no
// per-call I/O - nothing here needs to be re-created per scope or per call.
public class TokenService : ITokenService
{
    private readonly JwtOptions _options;
    private readonly SymmetricSecurityKey _signingKey;
    private readonly IClock _clock;

    public TokenService(IOptions<JwtOptions> options, IClock clock)
    {
        _options = options.Value;
        _signingKey = new SymmetricSecurityKey(Convert.FromBase64String(_options.SigningKey));
        _clock = clock;
    }

    public (string AccessToken, int ExpiresIn) CreateAccessToken(User user)
    {
        var claims = new List<Claim>
        {
            new(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new(JwtRegisteredClaimNames.Email, user.Email),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };

        // One "scope" claim per granted scope, not a single space-separated value -
        // RequireClaim("scope", "quotes.write") looks for a claim of that type whose
        // value matches exactly, so each scope needs its own claim instance.
        claims.AddRange(
            user.Scopes
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Select(scope => new Claim("scope", scope)));

        var credentials = new SigningCredentials(_signingKey, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            issuer: _options.Issuer,
            audience: _options.Audience,
            claims: claims,
            expires: _clock.UtcNow.AddSeconds(_options.AccessTokenExpirySeconds).UtcDateTime,
            signingCredentials: credentials);

        return (new JwtSecurityTokenHandler().WriteToken(token), _options.AccessTokenExpirySeconds);
    }

    // Opaque, not a JWT - it's a bearer secret looked up by hash in RefreshTokens,
    // not something the server needs to decode or inspect.
    public string CreateRefreshToken() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    // Deterministic (unsalted) on purpose: refresh tokens are already 256 bits of
    // randomness, not a low-entropy password, so there's no rainbow-table risk to
    // guard against - and a deterministic hash is what makes "look this up by
    // exact match" possible at all. BCrypt's per-hash random salt would make that
    // lookup impossible without scanning every row.
    public string HashRefreshToken(string rawToken) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(rawToken)));
}
