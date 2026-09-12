using QuotesApi.Dtos;
using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace QuotesApi.Tests;

// The full ladder: anonymous, authenticated-but-wrong-policy (covered separately
// in AuthorizationPolicyTests.cs), authenticated-with-right-policy, and
// expired-token. Revoked-refresh-chain is covered in AuthTests.cs.
public class EndToEndAuthTests : IClassFixture<QuotesApiFactory>
{
    private readonly QuotesApiFactory _factory;

    public EndToEndAuthTests(QuotesApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task CreateQuote_Anonymous_Returns401()
    {
        var client = _factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            "/api/quotes", new CreateQuoteRequest("Nobody", "No token was sent at all"));

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task CreateQuote_AuthenticatedWithRightPolicy_Returns201()
    {
        var client = _factory.CreateClient();

        var loginResponse = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("demo@quotesapi.dev", "correct-horse-battery-staple"));
        loginResponse.EnsureSuccessStatusCode();
        var token = (await loginResponse.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/quotes")
        {
            Headers = { { "Authorization", $"Bearer {token}" } },
            Content = JsonContent.Create(new CreateQuoteRequest("Right Policy", "demo has the quotes.write scope"))
        };

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    [Fact]
    public async Task CreateQuote_WithExpiredToken_Returns401()
    {
        // Mint while the clock is set far in the past, so the embedded "exp" claim
        // is already behind real wall-clock time. Validation runs through
        // Microsoft.IdentityModel's default lifetime check, which uses genuine
        // system time - it has no idea this app-level IClock exists - so setting
        // the mint-time clock to the past is what makes this deterministic without
        // any real waiting.
        _factory.Clock.UtcNow = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var client = _factory.CreateClient();

        var loginResponse = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("demo@quotesapi.dev", "correct-horse-battery-staple"));
        loginResponse.EnsureSuccessStatusCode();
        var token = (await loginResponse.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/quotes")
        {
            Headers = { { "Authorization", $"Bearer {token}" } },
            Content = JsonContent.Create(new CreateQuoteRequest("Too Late", "Minted in the year 2000"))
        };

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }
}
