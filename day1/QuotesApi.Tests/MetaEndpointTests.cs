using System.Net;
using System.Net.Http.Json;
using Xunit;

namespace QuotesApi.Tests;

// GET /api/meta backs app.html's environment badge (quotes-ui/src/app/app.ts)
// so the same frontend build can be pointed at either the "dev" or "prod"
// Render deployment (render.yaml) and still show which one it's talking to.
public class MetaEndpointTests : IClassFixture<QuotesApiFactory>
{
    private readonly QuotesApiFactory _factory;

    public MetaEndpointTests(QuotesApiFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Meta_Anonymous_Returns200WithAnEnvironmentString()
    {
        var client = _factory.CreateClient();

        var response = await client.GetAsync("/api/meta");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<Dictionary<string, string>>();
        Assert.True(body!.ContainsKey("environment"));
        Assert.False(string.IsNullOrWhiteSpace(body["environment"]));
    }
}
