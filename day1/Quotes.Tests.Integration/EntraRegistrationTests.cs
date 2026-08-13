using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using QuotesApi;
using Xunit;

namespace Quotes.Tests.Integration;

// Covers Extension.cs's `if (entraOptions is not null) { authBuilder.AddJwtBearer(...) }`
// block - previously only reachable with real Entra credentials this project
// doesn't have, so it never ran in any test. Deliberately does NOT send a
// token that actually gets routed to and validated against the Entra scheme:
// that would require a real network call to Microsoft's OIDC metadata
// endpoint for a fake tenant, which is exactly the kind of external
// dependency a test shouldn't have. What's being proven here is narrower and
// fully within reach: the DI registration itself doesn't throw, and the
// Internal scheme keeps working unaffected once Entra is also registered.
public class EntraRegistrationTests : IAsyncLifetime
{
    private EntraEnabledQuotesApiFactory _factory = null!;
    private HttpClient _client = null!;

    public Task InitializeAsync()
    {
        _factory = new EntraEnabledQuotesApiFactory();
        _client = _factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task AppStartsSuccessfully_WithEntraSchemeAlsoRegistered()
    {
        var response = await _client.GetAsync("/api/quotes");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task InternalScheme_StillWorksNormally_WithEntraAlsoRegistered()
    {
        var login = await _client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("demo@quotesapi.dev", "correct-horse-battery-staple"));

        login.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ProtectedEndpoint_NoToken_StillReturnsUnauthorized_WithEntraAlsoRegistered()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/quotes", new CreateQuoteRequest("Nobody", "No token sent"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
