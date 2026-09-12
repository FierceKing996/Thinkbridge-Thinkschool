using QuotesApi.Dtos;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace QuotesApi.Tests;

public class AuthTests : IClassFixture<QuotesApiFactory>
{
    private readonly QuotesApiFactory _factory;

    public AuthTests(QuotesApiFactory factory)
    {
        _factory = factory;
        _factory.Clock.UtcNow = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
    }

    [Fact]
    public async Task RefreshTokenReuse_RevokesTheEntireChain()
    {
        var client = _factory.CreateClient();

        var loginResponse = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("demo@quotesapi.dev", "correct-horse-battery-staple"));
        loginResponse.EnsureSuccessStatusCode();
        var pair1 = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>();

        // Legitimate rotation: pair1's refresh token gets used exactly once, as intended.
        var refreshResponse = await client.PostAsJsonAsync(
            "/api/auth/refresh", new RefreshRequest(pair1!.RefreshToken));
        refreshResponse.EnsureSuccessStatusCode();
        var pair2 = await refreshResponse.Content.ReadFromJsonAsync<LoginResponse>();

        // An attacker who stole pair1's refresh token before rotation replays it now.
        var reuseAttempt = await client.PostAsJsonAsync(
            "/api/auth/refresh", new RefreshRequest(pair1.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, reuseAttempt.StatusCode);

        // pair2 was never itself reused, never expired, never logged out - it's the
        // legitimate user's own current token. Reuse detection must still have
        // killed it too, purely because it descends from the compromised chain.
        var legitimateAttemptAfterReuse = await client.PostAsJsonAsync(
            "/api/auth/refresh", new RefreshRequest(pair2!.RefreshToken));
        Assert.Equal(HttpStatusCode.Unauthorized, legitimateAttemptAfterReuse.StatusCode);
    }
}
