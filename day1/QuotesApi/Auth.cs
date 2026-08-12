using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
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

// Populated from an Entra app registration you create in the Azure Portal - not
// something this code can provide values for. Section is optional: if it's absent
// from configuration, the Entra scheme simply isn't registered and every token is
// validated against the internal (HS256) scheme only.
public class EntraOptions
{
    public const string SectionName = "Entra";

    public required string TenantId { get; set; }
    public required string Audience { get; set; }
}

public record LoginRequest(string Email, string Password);
public record RefreshRequest([property: JsonPropertyName("refresh_token")] string RefreshToken);
public record LogoutRequest([property: JsonPropertyName("refresh_token")] string RefreshToken);

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
    string HashRefreshToken(string rawToken);
}

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

public class RefreshResult
{
    public LoginResponse? Tokens { get; }
    public string? Error { get; }
    public bool Succeeded => Error is null;

    private RefreshResult(LoginResponse? tokens, string? error)
    {
        Tokens = tokens;
        Error = error;
    }

    public static RefreshResult Success(LoginResponse tokens) => new(tokens, null);
    public static RefreshResult Fail(string error) => new(null, error);
}

public interface IAuthService
{
    Task<LoginResponse?> LoginAsync(string email, string password, CancellationToken ct);
    Task<RefreshResult> RefreshAsync(string rawRefreshToken, CancellationToken ct);
    Task LogoutAsync(string rawRefreshToken, CancellationToken ct);
}

public class AuthService(
    AppDbContext db,
    IRefreshTokenRepository refreshTokens,
    ITokenService tokenService,
    IClock clock,
    ILogger<AuthService> logger) : IAuthService
{
    private static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(7);

    public async Task<LoginResponse?> LoginAsync(string email, string password, CancellationToken ct)
    {
        var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
        if (user is null || !BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
        {
            return null;
        }

        return await IssueTokenPairAsync(user, ct);
    }

    public async Task<RefreshResult> RefreshAsync(string rawRefreshToken, CancellationToken ct)
    {
        var hash = tokenService.HashRefreshToken(rawRefreshToken);
        var stored = await refreshTokens.GetByTokenHashAsync(hash, ct);

        if (stored is null)
        {
            return RefreshResult.Fail("invalid");
        }

        if (stored.ReplacedByTokenHash is not null)
        {
            // This exact token was already rotated once. Being presented again
            // means whoever holds it now is not the legitimate holder of record -
            // the token leaked at some point. Kill every token descended from here
            // and force the real user back through a full login.
            logger.LogWarning(
                "Refresh token reuse detected for user {UserId}. Revoking entire token chain.",
                stored.UserId);
            await RevokeChainAsync(stored, ct);
            return RefreshResult.Fail("reuse-detected");
        }

        if (!stored.IsActive(clock.UtcNow))
        {
            return RefreshResult.Fail("expired-or-revoked");
        }

        var user = await db.Users.FindAsync([stored.UserId], ct)
            ?? throw new InvalidOperationException($"User {stored.UserId} no longer exists.");

        var response = await IssueTokenPairAsync(user, ct, previousToken: stored);
        return RefreshResult.Success(response);
    }

    public async Task LogoutAsync(string rawRefreshToken, CancellationToken ct)
    {
        var hash = tokenService.HashRefreshToken(rawRefreshToken);
        var stored = await refreshTokens.GetByTokenHashAsync(hash, ct);
        if (stored is null) return;

        stored.Revoke(clock.UtcNow);
        await refreshTokens.SaveChangesAsync(ct);
    }

    private async Task<LoginResponse> IssueTokenPairAsync(User user, CancellationToken ct, RefreshToken? previousToken = null)
    {
        var (accessToken, expiresIn) = tokenService.CreateAccessToken(user);
        var rawRefreshToken = tokenService.CreateRefreshToken();
        var refreshHash = tokenService.HashRefreshToken(rawRefreshToken);

        var newToken = RefreshToken.Create(refreshHash, user.Id, clock.UtcNow.Add(RefreshTokenLifetime));
        await refreshTokens.AddAsync(newToken, ct);

        previousToken?.MarkReplacedBy(refreshHash, clock.UtcNow);

        await refreshTokens.SaveChangesAsync(ct);

        return new LoginResponse { AccessToken = accessToken, RefreshToken = rawRefreshToken, ExpiresIn = expiresIn };
    }

    // Walks forward from the point of compromise, revoking every descendant -
    // including the current "live" token at the end of the chain, whoever holds
    // it. Ancestors before this point are already revoked by normal rotation.
    private async Task RevokeChainAsync(RefreshToken compromisedToken, CancellationToken ct)
    {
        var nextHash = compromisedToken.ReplacedByTokenHash;

        while (nextHash is not null)
        {
            var next = await refreshTokens.GetByTokenHashAsync(nextHash, ct);
            if (next is null) break;

            nextHash = next.ReplacedByTokenHash;
            next.Revoke(clock.UtcNow);
        }

        await refreshTokens.SaveChangesAsync(ct);
    }
}
