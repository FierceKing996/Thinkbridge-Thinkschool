using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QuotesApi;
using Xunit;

namespace QuotesApi.Tests;

public class QuotesApiFactory : WebApplicationFactory<Program>
{
    public FakeClock Clock { get; } = new();

    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _connection.Open();

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));

            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(Clock);
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _connection.Dispose();
    }
}

public class CollectionsClockTests : IClassFixture<QuotesApiFactory>
{
    private readonly QuotesApiFactory _factory;

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public CollectionsClockTests(QuotesApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AddItem_StampsAddedAt_WithTheInjectedClocksTime()
    {
        var frozenNow = new DateTimeOffset(2030, 1, 1, 12, 0, 0, TimeSpan.Zero);
        _factory.Clock.UtcNow = frozenNow;

        var client = _factory.CreateClient();

        var collectionResponse = await client.PostAsJsonAsync(
            "/api/collections", new CreateCollectionRequest("Fake Clock Demo", OwnerId: 1));
        var collection = await collectionResponse.Content.ReadFromJsonAsync<CollectionDto>(JsonOptions);

        var itemResponse = await client.PostAsJsonAsync(
            $"/api/collections/{collection!.Id}/items", new AddCollectionItemRequest(QuoteId: 1));
        var updated = await itemResponse.Content.ReadFromJsonAsync<CollectionDto>(JsonOptions);

        var addedAt = Assert.Single(updated!.Items).AddedAt;
        Assert.Equal(frozenNow, addedAt);
    }

    // Minimal shape for deserializing the endpoint's response - avoids depending on
    // the real Collection aggregate's EF-only constructor from the test project.
    private record CollectionDto(int Id, string Name, int OwnerId, List<CollectionItemDto> Items);
    private record CollectionItemDto(int QuoteId, DateTimeOffset AddedAt);
}
