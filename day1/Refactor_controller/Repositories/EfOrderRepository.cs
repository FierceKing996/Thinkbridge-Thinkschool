using Microsoft.EntityFrameworkCore;
using Refactor_controller.Data;
using Refactor_controller.Models;

namespace Refactor_controller.Repositories;

public class EfOrderRepository : IOrderRepository
{
    private readonly AppDbContext _context;
    private readonly ILogger<EfOrderRepository> _logger;

    public EfOrderRepository(AppDbContext context, ILogger<EfOrderRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    public Task<Order?> GetByIdAsync(int id, CancellationToken cancellationToken) =>
        _context.Orders
            .Include(o => o.Customer)
            .Include(o => o.Items).ThenInclude(i => i.Product)
            .FirstOrDefaultAsync(o => o.Id == id, cancellationToken);

    public async Task AddAsync(Order order, CancellationToken cancellationToken)
    {
        await _context.Orders.AddAsync(order, cancellationToken);
    }

    public async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            _logger.LogError(ex, "Failed to persist order changes");
            throw;
        }
    }
}
