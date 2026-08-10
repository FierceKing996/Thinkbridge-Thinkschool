using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Refactor_controller.Data;
using Refactor_controller.Dtos;
using Xunit;

namespace Refactor_controller.Tests;

public class OrdersApiFactory : WebApplicationFactory<Program>
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        _connection.Open();

        builder.ConfigureServices(services =>
        {
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.AddDbContext<AppDbContext>(options => options.UseSqlite(_connection));
        });
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        _connection.Dispose();
    }
}

public class OrdersApiIntegrationTests : IClassFixture<OrdersApiFactory>
{
    private readonly OrdersApiFactory _factory;

    public OrdersApiIntegrationTests(OrdersApiFactory factory)
    {
        _factory = factory;
    }

    // End-to-end regression test: Bob (seeded customer #2) has no address on file,
    // and ordering two items exercises the original off-by-one loop bound.
    // Against the original controller this request threw an unhandled exception (HTTP 500).
    [Fact]
    public async Task PostOrder_CustomerWithoutAddress_MultipleItems_ReturnsTypedSuccessResponse()
    {
        var client = _factory.CreateClient();

        var request = new CreateOrderRequest
        {
            CustomerId = 2, // Bob - seeded with no address
            Items =
            [
                new OrderItemRequest { ProductId = 1, Quantity = 1 }, // Widget - has a category
                new OrderItemRequest { ProductId = 2, Quantity = 1 }  // Gadget - seeded with no category
            ]
        };

        var response = await client.PostAsJsonAsync("/api/orders", request);

        response.EnsureSuccessStatusCode();
        var order = await response.Content.ReadFromJsonAsync<OrderResponse>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(order);
        Assert.Equal(2, order!.Items.Count);
        Assert.True(order.Total > 0);
    }
}
