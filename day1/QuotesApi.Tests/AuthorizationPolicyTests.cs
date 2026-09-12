using QuotesApi.Dtos;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace QuotesApi.Tests;

public class AuthorizationPolicyTests : IClassFixture<QuotesApiFactory>
{
    private readonly QuotesApiFactory _factory;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public AuthorizationPolicyTests(QuotesApiFactory factory)
    {
        _factory = factory;
    }

    private async Task<string> LoginAsync(HttpClient client, string email)
    {
        var response = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest(email, "correct-horse-battery-staple"));
        response.EnsureSuccessStatusCode();
        var tokens = await response.Content.ReadFromJsonAsync<LoginResponse>();
        return tokens!.AccessToken;
    }

    // Claim-based policy: readonly@quotesapi.dev is seeded with no scopes at all,
    // so RequireClaim("scope", "quotes.write") fails even though the user is
    // genuinely authenticated - this is "who they are" succeeding but "can they"
    // failing, which is exactly the 401/403 distinction the policy exists to draw.
    [Fact]
    public async Task CreateQuote_WithoutWriteScope_Returns403()
    {
        var client = _factory.CreateClient();
        var token = await LoginAsync(client, "readonly@quotesapi.dev");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/quotes")
        {
            Headers = { { "Authorization", $"Bearer {token}" } },
            Content = JsonContent.Create(new CreateQuoteRequest("Someone", "Should never be created"))
        };

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // Custom resource-based requirement: readonly@quotesapi.dev is authenticated
    // and allowed to hit the delete endpoint at all (that only requires being
    // logged in), but SameOwnerAuthorizationHandler compares the token's "sub"
    // claim against the specific Quote's CreatedByUserId and fails - a rule no
    // static claim on the token alone could express.
    [Fact]
    public async Task DeleteQuote_NotTheOwner_Returns403()
    {
        var client = _factory.CreateClient();

        var ownerToken = await LoginAsync(client, "demo@quotesapi.dev");
        using var createRequest = new HttpRequestMessage(HttpMethod.Post, "/api/quotes")
        {
            Headers = { { "Authorization", $"Bearer {ownerToken}" } },
            Content = JsonContent.Create(new CreateQuoteRequest("Owner", "This quote belongs to demo@quotesapi.dev"))
        };
        var createResponse = await client.SendAsync(createRequest);
        createResponse.EnsureSuccessStatusCode();
        var quoteId = (await createResponse.Content.ReadFromJsonAsync<JsonElement>(JsonOptions)).GetProperty("id").GetInt32();

        var otherUserToken = await LoginAsync(client, "readonly@quotesapi.dev");
        using var deleteRequest = new HttpRequestMessage(HttpMethod.Delete, $"/api/quotes/{quoteId}")
        {
            Headers = { { "Authorization", $"Bearer {otherUserToken}" } }
        };

        var deleteResponse = await client.SendAsync(deleteRequest);

        Assert.Equal(HttpStatusCode.Forbidden, deleteResponse.StatusCode);
    }
}
