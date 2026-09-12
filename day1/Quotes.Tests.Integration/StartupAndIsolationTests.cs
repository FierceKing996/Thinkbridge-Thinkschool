using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using QuotesApi.Auth;
using QuotesApi.Data;
using QuotesApi.Dtos;
using QuotesApi.Models;
using QuotesApi.Repositories;
using QuotesApi.Services;
using Xunit;

namespace Quotes.Tests.Integration;

public class StartupAndIsolationTests : IntegrationTestBase
{
    // If EF migrations hadn't run against this test's fresh SQLite connection,
    // the Users table wouldn't exist and this would fail with a raw
    // "no such table" SQLite error, not a clean 200/401 from the app.
    [Fact]
    public async Task Startup_AppliesMigrationsAndSeedsUsers_LoginSucceedsImmediately()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("demo@quotesapi.dev", "correct-horse-battery-staple"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // Proof of isolation, not a tautology: QuoteEndpointTests creates quotes in
    // several tests in this same run. If databases were shared instead of fresh
    // per test, this would be flaky depending on execution order - it isn't,
    // because each test's factory owns its own SqliteConnection.
    [Fact]
    public async Task GetQuotes_NoOtherTestsCreatedAnythingHere_StartsEmpty()
    {
        var response = await Client.GetAsync("/api/quotes");

        var quotes = await response.Content.ReadFromJsonAsync<List<object>>();
        quotes.Should().BeEmpty();
    }
}
