using System.Net.Http.Headers;
using System.Net.Http.Json;
using QuotesApi;
using Xunit;

namespace Quotes.Tests.Integration;

[Collection("SqlServer collection")]
public abstract class SqlServerIntegrationTestBase : IAsyncLifetime
{
    private readonly SqlServerContainerFixture _containerFixture;
    protected SqlServerQuotesApiFactory Factory { get; private set; } = null!;
    protected HttpClient Client { get; private set; } = null!;

    protected SqlServerIntegrationTestBase(SqlServerContainerFixture containerFixture)
    {
        _containerFixture = containerFixture;
    }

    public Task InitializeAsync()
    {
        Factory = new SqlServerQuotesApiFactory(_containerFixture.ConnectionString);
        Client = Factory.CreateClient();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        Client.Dispose();
        await Factory.DisposeAsync();
    }

    protected async Task<string> LoginAsync(
        string email = "demo@quotesapi.dev", string password = "correct-horse-battery-staple")
    {
        var response = await Client.PostAsJsonAsync("/api/auth/login", new LoginRequest(email, password));
        response.EnsureSuccessStatusCode();
        var tokens = await response.Content.ReadFromJsonAsync<LoginResponse>();
        return tokens!.AccessToken;
    }

    protected HttpRequestMessage AuthedRequest(HttpMethod method, string url, string token, HttpContent? content = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return request;
    }
}
