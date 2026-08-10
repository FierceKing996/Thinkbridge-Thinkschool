using Refactor_controller.Models;

namespace Refactor_controller.Repositories;

public interface IProductRepository
{
    Task<Dictionary<int, Product>> GetByIdsAsync(IEnumerable<int> ids, CancellationToken cancellationToken);
}
