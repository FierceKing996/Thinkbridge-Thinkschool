using Microsoft.EntityFrameworkCore;
using Refactor_controller.Data;
using Refactor_controller.Models;

namespace Refactor_controller.Repositories;

public class EfProductRepository : IProductRepository
{
    private readonly AppDbContext _context;

    public EfProductRepository(AppDbContext context)
    {
        _context = context;
    }

    public async Task<Dictionary<int, Product>> GetByIdsAsync(IEnumerable<int> ids, CancellationToken cancellationToken)
    {
        var idList = ids.Distinct().ToList();

        var products = await _context.Products
            .Include(p => p.Category)
            .Where(p => idList.Contains(p.Id))
            .ToListAsync(cancellationToken);

        return products.ToDictionary(p => p.Id);
    }
}
