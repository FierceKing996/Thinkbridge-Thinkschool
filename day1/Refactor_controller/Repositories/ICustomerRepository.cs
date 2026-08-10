using Refactor_controller.Models;

namespace Refactor_controller.Repositories;

public interface ICustomerRepository
{
    Task<Customer?> GetByIdAsync(int id, CancellationToken cancellationToken);
}
