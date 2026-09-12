using QuotesApi.Dtos;
using QuotesApi.Models;
using QuotesApi.Repositories;

namespace QuotesApi.Services;

public class AuthService(
    IUserRepository users,
    IRefreshTokenRepository refreshTokens,
    ITokenService tokenService,
    IClock clock,
    ILogger<AuthService> logger) : IAuthService
{
    private static readonly TimeSpan RefreshTokenLifetime = TimeSpan.FromDays(7);

    public async Task<LoginResponse?> LoginAsync(string email, string password, CancellationToken ct)
    {
        var user = await users.GetByEmailAsync(email, ct);
        if (user is null)
        {
            return null;
        }

        // BCrypt verification is deliberately slow (adaptive work factor) and
        // is pure CPU work - invisible to both the EF and ASP.NET Core auto
        // instrumentation, which only see I/O. A custom span is the only way
        // to see how much of a login request's time this step costs.
        //
        // Scoped as a block, not `using var` - a using declaration wouldn't
        // dispose (end) the span until LoginAsync returns, which would wrongly
        // nest IssueTokenPairAsync's own DB work inside "verify-password" too.
        // Caught this by actually inspecting exported spans, not by inspection.
        bool passwordMatches;
        using (var activity = Telemetry.Source.StartActivity("verify-password"))
        {
            activity?.SetTag("user.id", user.Id);
            passwordMatches = BCrypt.Net.BCrypt.Verify(password, user.PasswordHash);
        }

        if (!passwordMatches)
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

        var user = await users.GetByIdAsync(stored.UserId, ct)
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
