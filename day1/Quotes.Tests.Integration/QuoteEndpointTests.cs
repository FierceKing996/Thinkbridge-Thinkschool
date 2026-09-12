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

public class QuoteEndpointTests : IntegrationTestBase
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task GetQuotes_Anonymous_ReturnsOk()
    {
        var response = await Client.GetAsync("/api/quotes");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetQuoteById_NonExistentId_ReturnsNotFound()
    {
        var response = await Client.GetAsync("/api/quotes/999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // Happy path: authenticated, holds the required "can-edit-quotes" policy,
    // sends a valid body - the whole pipeline (auth -> policy -> validation ->
    // persistence) succeeds end to end.
    [Fact]
    public async Task CreateQuote_ValidRequestWithToken_ReturnsCreatedWithQuote()
    {
        var token = await LoginAsync();
        using var request = AuthedRequest(HttpMethod.Post, "/api/quotes", token,
            JsonContent.Create(new CreateQuoteRequest("Grace Hopper", "The most dangerous phrase is: we have always done it this way.")));

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var created = await response.Content.ReadFromJsonAsync<QuoteDto>(JsonOptions);
        created!.Author.Should().Be("Grace Hopper");
        created.IsDeleted.Should().BeFalse();
    }

    // Error path: no Authorization header at all. The whole point of gating this
    // endpoint - the request must be rejected before it ever reaches Quote.Create.
    [Fact]
    public async Task CreateQuote_NoToken_ReturnsUnauthorized()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/quotes", new CreateQuoteRequest("Nobody", "No token was sent"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateQuote_BlankText_ReturnsValidationProblemDetails()
    {
        var token = await LoginAsync();
        using var request = AuthedRequest(HttpMethod.Post, "/api/quotes", token,
            JsonContent.Create(new CreateQuoteRequest("Someone", "")));

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
    }

    [Fact]
    public async Task DeleteQuote_OwnerWithToken_ReturnsNoContent()
    {
        var token = await LoginAsync();
        using var createRequest = AuthedRequest(HttpMethod.Post, "/api/quotes", token,
            JsonContent.Create(new CreateQuoteRequest("Author", "To be deleted")));
        var created = await (await Client.SendAsync(createRequest)).Content.ReadFromJsonAsync<QuoteDto>(JsonOptions);

        using var deleteRequest = AuthedRequest(HttpMethod.Delete, $"/api/quotes/{created!.Id}", token);
        var response = await Client.SendAsync(deleteRequest);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task DeleteQuote_NoToken_ReturnsUnauthorized()
    {
        var response = await Client.DeleteAsync("/api/quotes/1");

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private record QuoteDto(int Id, string Author, string Text, bool IsDeleted, int CreatedByUserId);
}
