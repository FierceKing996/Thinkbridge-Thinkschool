using Microsoft.Extensions.Logging.Abstractions;
using Refactor_controller.Dtos;
using Refactor_controller.Exceptions;
using Refactor_controller.Models;
using Refactor_controller.Pricing;
using Refactor_controller.Services;
using Refactor_controller.Tests.Fakes;
using Xunit;

namespace Refactor_controller.Tests;

public class OrderServiceTests
{
    private static (OrderService Service, FakeOrderRepository Orders, FakeEmailSender Emails) BuildService(
        IEnumerable<Customer> customers, IEnumerable<Product> products)
    {
        var orderRepo = new FakeOrderRepository();
        var emailSender = new FakeEmailSender();
        var discountCalculator = new DiscountCalculator([new CouponDiscountRule(), new LoyaltyBonusRule()]);
        var service = new OrderService(
            new FakeCustomerRepository(customers),
            new FakeProductRepository(products),
            orderRepo,
            emailSender,
            discountCalculator,
            NullLogger<OrderService>.Instance);

        return (service, orderRepo, emailSender);
    }

    // Regression test for the original off-by-one loop bound (`i <= Items.Count`),
    // which threw ArgumentOutOfRangeException whenever an order had any items.
    // Also exercises a product with no Category, the second planted null-deref bug.
    [Fact]
    public async Task CreateOrderAsync_WithMultipleItems_ProcessesAllOfThem()
    {
        var customer = new Customer { Id = 1, Name = "Alice", Email = "alice@example.com", Address = new Address { City = "Springfield" } };
        var productWithCategory = new Product { Id = 1, Name = "Widget", Price = 10m, StockQuantity = 100, Category = new Category { Name = "Tools" } };
        var productWithoutCategory = new Product { Id = 2, Name = "Gadget", Price = 20m, StockQuantity = 100, Category = null };

        var (service, _, _) = BuildService([customer], [productWithCategory, productWithoutCategory]);

        var request = new CreateOrderRequest
        {
            CustomerId = 1,
            Items =
            [
                new OrderItemRequest { ProductId = 1, Quantity = 1 },
                new OrderItemRequest { ProductId = 2, Quantity = 1 }
            ]
        };

        var result = await service.CreateOrderAsync(request, CancellationToken.None);

        Assert.Equal(2, result.Items.Count);
        Assert.Equal(30m, result.Subtotal);
    }

    // Regression test for the original `customer.Address.City` null dereference —
    // a customer with no address on file must not crash order creation.
    [Fact]
    public async Task CreateOrderAsync_CustomerWithoutAddress_DoesNotThrow()
    {
        var customerWithoutAddress = new Customer { Id = 2, Name = "Bob", Email = "bob@example.com", Address = null };
        var product = new Product { Id = 1, Name = "Widget", Price = 10m, StockQuantity = 100 };

        var (service, _, _) = BuildService([customerWithoutAddress], [product]);

        var request = new CreateOrderRequest
        {
            CustomerId = 2,
            Items = [new OrderItemRequest { ProductId = 1, Quantity = 1 }]
        };

        var result = await service.CreateOrderAsync(request, CancellationToken.None);

        Assert.Single(result.Items);
    }

    [Fact]
    public async Task CreateOrderAsync_AppliesCouponDiscountAndTax_Correctly()
    {
        var customer = new Customer { Id = 1, Name = "Alice", Email = "alice@example.com", Address = new Address { City = "Springfield" } };
        var product = new Product { Id = 1, Name = "Widget", Price = 10m, StockQuantity = 100 };

        var (service, _, _) = BuildService([customer], [product]);

        var request = new CreateOrderRequest
        {
            CustomerId = 1,
            CouponCode = "SAVE10",
            Items = [new OrderItemRequest { ProductId = 1, Quantity = 10 }]
        };

        var result = await service.CreateOrderAsync(request, CancellationToken.None);

        Assert.Equal(100m, result.Subtotal);
        Assert.Equal(10m, result.Discount);
        Assert.Equal(7.2m, result.Tax);
        Assert.Equal(97.2m, result.Total);
    }

    // Test: order fails when customer does not exist
    [Fact]
    public async Task CreateOrderAsync_UnknownCustomer_ThrowsCustomerNotFoundException()
    {
        var product = new Product { Id = 1, Name = "Widget", Price = 10m, StockQuantity = 100 };
        var (service, _, _) = BuildService([], [product]);

        var request = new CreateOrderRequest
        {
            CustomerId = 999,
            Items = [new OrderItemRequest { ProductId = 1, Quantity = 1 }]
        };

        await Assert.ThrowsAsync<CustomerNotFoundException>(
            () => service.CreateOrderAsync(request, CancellationToken.None));
    }

    // Test: discount never makes total negative
    [Fact]
    public async Task CreateOrderAsync_DiscountExceedsSubtotal_TotalIsClampedToZero()
    {
        var customer = new Customer { Id = 1, Name = "Alice", Email = "alice@example.com" };
        var product = new Product { Id = 1, Name = "Widget", Price = 10m, StockQuantity = 100 };

        var orderRepo = new FakeOrderRepository();
        var emailSender = new FakeEmailSender();
        var discountCalculator = new DiscountCalculator([new OversizedDiscountRule()]);
        var service = new OrderService(
            new FakeCustomerRepository([customer]),
            new FakeProductRepository([product]),
            orderRepo,
            emailSender,
            discountCalculator,
            NullLogger<OrderService>.Instance);

        var request = new CreateOrderRequest
        {
            CustomerId = 1,
            Items = [new OrderItemRequest { ProductId = 1, Quantity = 1 }]
        };

        var result = await service.CreateOrderAsync(request, CancellationToken.None);

        Assert.Equal(0m, result.Total);
    }
}
