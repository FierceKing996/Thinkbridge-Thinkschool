using System.Net.Http.Headers;
using System.Net.Http.Json;
using QuotesApi.Auth;
using QuotesApi.Data;
using QuotesApi.Dtos;
using QuotesApi.Models;
using QuotesApi.Repositories;
using QuotesApi.Services;
using Xunit;

namespace Quotes.Tests.Integration;

// No IClassFixture anywhere in this project: xUnit constructs a fresh instance
// of the test class - and therefore calls InitializeAsync() - for every single
// [Fact]/[Theory] case. That's what gives each test its own factory, its own
// in-memory SQLite connection, and its own HttpClient with zero coordination.
public abstract class IntegrationTestBase : IAsyncLifetime
{
    protected QuotesApiFactory Factory { get; private set; } = null!;
    protected HttpClient Client { get; private set; } = null!;

    public Task InitializeAsync()
    {
        Factory = new QuotesApiFactory();
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
