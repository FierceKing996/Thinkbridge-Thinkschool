using Refactor_controller.Models;
using Refactor_controller.Pricing;
using Refactor_controller.Repositories;
using Refactor_controller.Services;

namespace Refactor_controller.Tests.Fakes;

// Only possible to inject a rogue rule like this because discount logic is now
// a pluggable IDiscountRule, not a private method baked into OrderService.
public class OversizedDiscountRule : IDiscountRule
{
    public decimal Calculate(DiscountContext context) => context.Subtotal + 1000m;
}

public class FakeCustomerRepository : ICustomerRepository
{
    private readonly Dictionary<int, Customer> _customers;

    public FakeCustomerRepository(IEnumerable<Customer> customers) =>
        _customers = customers.ToDictionary(c => c.Id);

    public Task<Customer?> GetByIdAsync(int id, CancellationToken cancellationToken) =>
        Task.FromResult(_customers.GetValueOrDefault(id));
}

public class FakeProductRepository : IProductRepository
{
    private readonly Dictionary<int, Product> _products;

    public FakeProductRepository(IEnumerable<Product> products) =>
        _products = products.ToDictionary(p => p.Id);

    public Task<Dictionary<int, Product>> GetByIdsAsync(IEnumerable<int> ids, CancellationToken cancellationToken)
    {
        var idSet = ids.Distinct().ToHashSet();
        return Task.FromResult(_products.Where(p => idSet.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value));
    }
}

public class FakeOrderRepository : IOrderRepository
{
    public List<Order> SavedOrders { get; } = new();
    private int _nextId = 1;

    public Task<Order?> GetByIdAsync(int id, CancellationToken cancellationToken) =>
        Task.FromResult(SavedOrders.FirstOrDefault(o => o.Id == id));

    public Task AddAsync(Order order, CancellationToken cancellationToken)
    {
        order.Id = _nextId++;
        SavedOrders.Add(order);
        return Task.CompletedTask;
    }

    public Task SaveChangesAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

public class FakeEmailSender : IEmailSender
{
    public List<(string Email, int OrderId, decimal Total)> SentEmails { get; } = new();

    public Task SendOrderConfirmationAsync(string email, int orderId, decimal total, CancellationToken cancellationToken)
    {
        SentEmails.Add((email, orderId, total));
        return Task.CompletedTask;
    }
}
