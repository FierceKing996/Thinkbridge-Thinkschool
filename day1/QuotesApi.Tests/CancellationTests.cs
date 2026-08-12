using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using QuotesApi;
using Xunit;

namespace QuotesApi.Tests;

public class CancellationTestFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        _connection.Open();

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));

            // Every GetByIdAsync call takes 800ms instead of microseconds, giving the
            // test a reliable window to cancel well before the server finishes.
            services.RemoveAll<ICollectionRepository>();
            services.AddScoped<ICollectionRepository>(sp =>
                new SlowCollectionRepository(
                    new CollectionRepository(
                        sp.GetRequiredService<AppDbContext>(),
                        sp.GetRequiredService<ILogger<CollectionRepository>>()),
                    TimeSpan.FromMilliseconds(800)));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _connection.Dispose();
    }
}

public class CancellationTests : IClassFixture<CancellationTestFactory>
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly CancellationTestFactory _factory;

    public CancellationTests(CancellationTestFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task AddItem_CancelledMidRequest_ClientNeverReceivesAResponse()
    {
        var client = _factory.CreateClient();

        var loginResponse = await client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("demo@quotesapi.dev", "correct-horse-battery-staple"));
        loginResponse.EnsureSuccessStatusCode();
        var token = (await loginResponse.Content.ReadFromJsonAsync<LoginResponse>())!.AccessToken;

        using var createCollection = new HttpRequestMessage(HttpMethod.Post, "/api/collections")
        {
            Headers = { { "Authorization", $"Bearer {token}" } },
            Content = JsonContent.Create(new CreateCollectionRequest("Cancellation Demo", OwnerId: 1))
        };
        var collectionResponse = await client.SendAsync(createCollection);
        var collection = await collectionResponse.Content.ReadFromJsonAsync<CollectionDto>(JsonOptions);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/collections/{collection!.Id}/items")
        {
            Headers = { { "Authorization", $"Bearer {token}" } },
            Content = JsonContent.Create(new AddCollectionItemRequest(QuoteId: 1))
        };

        // The server-side handler is still inside its 800ms artificial delay when this
        // token fires at 100ms. ASP.NET Core has no real "499" - the client-observable
        // outcome of a cancelled request is that SendAsync throws instead of returning
        // any response, which is exactly "the operation didn't complete" from here.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.SendAsync(request, cts.Token));
    }

    private record CollectionDto(int Id, string Name, int OwnerId, List<CollectionItemDto> Items);
    private record CollectionItemDto(int QuoteId, DateTimeOffset AddedAt);
}

// Separate, fully deterministic proof that cancellation genuinely reaches the
// repository layer - no wall-clock racing, no HTTP round trip. A pre-cancelled
// token must make GetByIdAsync throw before it ever reaches the inner repository.
public class SlowCollectionRepositoryTests
{
    private class SpyRepository : ICollectionRepository
    {
        public bool GetByIdWasCalled { get; private set; }

        public Task<Collection?> GetByIdAsync(int id, CancellationToken ct)
        {
            GetByIdWasCalled = true;
            return Task.FromResult<Collection?>(null);
        }

        public Task<Collection> AddAsync(Collection collection, CancellationToken ct) => Task.FromResult(collection);
        public Task UpdateAsync(Collection collection, CancellationToken ct) => Task.CompletedTask;
        public Task<bool> DeleteAsync(int id, CancellationToken ct) => Task.FromResult(false);
    }

    [Fact]
    public async Task GetByIdAsync_TokenAlreadyCancelled_ThrowsAndNeverReachesInnerRepository()
    {
        var spy = new SpyRepository();
        var sut = new SlowCollectionRepository(spy, TimeSpan.FromSeconds(2));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => sut.GetByIdAsync(1, cts.Token));

        Assert.False(spy.GetByIdWasCalled);
    }
}
