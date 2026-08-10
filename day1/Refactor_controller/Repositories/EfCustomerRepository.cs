using Microsoft.EntityFrameworkCore;
using Refactor_controller.Data;
using Refactor_controller.Models;

namespace Refactor_controller.Repositories;

public class EfCustomerRepository : ICustomerRepository
{
    private readonly AppDbContext _context;

    public EfCustomerRepository(AppDbContext context)
    {
        _context = context;
    }

    public Task<Customer?> GetByIdAsync(int id, CancellationToken cancellationToken) =>
        _context.Customers
            .Include(c => c.Address)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
}
