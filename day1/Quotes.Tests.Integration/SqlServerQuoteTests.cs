using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using QuotesApi.Auth;
using QuotesApi.Data;
using QuotesApi.Dtos;
using QuotesApi.Models;
using QuotesApi.Repositories;
using QuotesApi.Services;
using Xunit;

namespace Quotes.Tests.Integration;

// Runs the same pipeline as QuoteEndpointTests, but against a real SQL Server
// 2022 container instead of in-memory SQLite. The DB is shared across every
// test in this class (and every other class in the "SqlServer collection"),
// so each test seeds its own uniquely-identifiable data via Guid rather than
// assuming it's the only thing in the database - unlike the SQLite tests,
// there's no "fresh DB per test" here, deliberately (see SqlServerContainerFixture).
public class SqlServerQuoteTests : SqlServerIntegrationTestBase
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public SqlServerQuoteTests(SqlServerContainerFixture containerFixture) : base(containerFixture) { }

    [Fact]
    public async Task Login_ValidCredentials_ReturnsOkWithTokenPair()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("demo@quotesapi.dev", "correct-horse-battery-staple"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var tokens = await response.Content.ReadFromJsonAsync<LoginResponse>();
        tokens!.AccessToken.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task CreateQuote_ValidRequestWithToken_RoundTripsThroughRealSqlServer()
    {
        var token = await LoginAsync();
        var uniqueAuthor = $"Author-{Guid.NewGuid()}";
        using var createRequest = AuthedRequest(HttpMethod.Post, "/api/quotes", token,
            JsonContent.Create(new CreateQuoteRequest(uniqueAuthor, "Round-tripped through a real SQL Server engine.")));

        var createResponse = await Client.SendAsync(createRequest);
        createResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await createResponse.Content.ReadFromJsonAsync<QuoteDto>(JsonOptions);

        var fetchResponse = await Client.GetAsync($"/api/quotes/{created!.Id}");
        var fetched = await fetchResponse.Content.ReadFromJsonAsync<QuoteDto>(JsonOptions);

        fetched!.Author.Should().Be(uniqueAuthor);
        fetched.Text.Should().Be("Round-tripped through a real SQL Server engine.");
    }

    // SQL Server enforces nvarchar(n) column width at the storage engine - unlike
    // SQLite, which is type-affinity-only and won't reject or truncate an
    // over-length string by default. This proves the app's own validation limit
    // (Quote.MaxAuthorLength) and the DB column width haven't drifted apart:
    // a string of exactly the maximum length must persist intact, not get
    // silently truncated or rejected by the real engine.
    [Fact]
    public async Task CreateQuote_AuthorAtExactMaxLength_PersistsWithoutTruncation()
    {
        var token = await LoginAsync();
        var maxLengthAuthor = $"{Guid.NewGuid():N}".PadRight(Quote.MaxAuthorLength, 'x')[..Quote.MaxAuthorLength];
        using var request = AuthedRequest(HttpMethod.Post, "/api/quotes", token,
            JsonContent.Create(new CreateQuoteRequest(maxLengthAuthor, "Boundary-length author test.")));

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<QuoteDto>(JsonOptions);
        created!.Author.Should().Be(maxLengthAuthor);
        created.Author.Length.Should().Be(Quote.MaxAuthorLength);
    }

    [Fact]
    public async Task CreateQuote_NoToken_ReturnsUnauthorized()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/quotes", new CreateQuoteRequest("Nobody", "No token was sent"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private record QuoteDto(int Id, string Author, string Text, bool IsDeleted, int CreatedByUserId);
}
