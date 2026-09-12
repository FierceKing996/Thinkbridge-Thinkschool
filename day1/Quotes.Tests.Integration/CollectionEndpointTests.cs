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

public class CollectionEndpointTests : IntegrationTestBase
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    [Fact]
    public async Task GetCollection_NonExistentId_ReturnsNotFound()
    {
        var response = await Client.GetAsync("/api/collections/999");

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task CreateCollection_ValidRequestWithToken_ReturnsCreated()
    {
        var token = await LoginAsync();
        using var request = AuthedRequest(HttpMethod.Post, "/api/collections", token,
            JsonContent.Create(new CreateCollectionRequest("Favorites", OwnerId: 1)));

        var response = await Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateCollection_NoToken_ReturnsUnauthorized()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/collections", new CreateCollectionRequest("Favorites", OwnerId: 1));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task AddItemToCollection_ValidRequest_ReturnsOkWithUpdatedCollection()
    {
        var token = await LoginAsync();
        using var createRequest = AuthedRequest(HttpMethod.Post, "/api/collections", token,
            JsonContent.Create(new CreateCollectionRequest("Favorites", OwnerId: 1)));
        var collection = await (await Client.SendAsync(createRequest)).Content.ReadFromJsonAsync<CollectionDto>(JsonOptions);

        using var addRequest = AuthedRequest(HttpMethod.Post, $"/api/collections/{collection!.Id}/items", token,
            JsonContent.Create(new AddCollectionItemRequest(QuoteId: 1)));
        var response = await Client.SendAsync(addRequest);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await response.Content.ReadFromJsonAsync<CollectionDto>(JsonOptions);
        updated!.Items.Should().ContainSingle(i => i.QuoteId == 1);
    }

    [Fact]
    public async Task RemoveItemFromCollection_ExistingItem_ReturnsOkWithEmptyItems()
    {
        var token = await LoginAsync();
        using var createRequest = AuthedRequest(HttpMethod.Post, "/api/collections", token,
            JsonContent.Create(new CreateCollectionRequest("Favorites", OwnerId: 1)));
        var collection = await (await Client.SendAsync(createRequest)).Content.ReadFromJsonAsync<CollectionDto>(JsonOptions);

        using var addRequest = AuthedRequest(HttpMethod.Post, $"/api/collections/{collection!.Id}/items", token,
            JsonContent.Create(new AddCollectionItemRequest(QuoteId: 1)));
        await Client.SendAsync(addRequest);

        using var removeRequest = AuthedRequest(HttpMethod.Delete, $"/api/collections/{collection.Id}/items/1", token);
        var response = await Client.SendAsync(removeRequest);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await response.Content.ReadFromJsonAsync<CollectionDto>(JsonOptions);
        updated!.Items.Should().BeEmpty();
    }

    private record CollectionDto(int Id, string Name, int OwnerId, List<CollectionItemDto> Items);
    private record CollectionItemDto(int QuoteId, DateTimeOffset AddedAt);
}
