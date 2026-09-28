using QuotesApi.Models;

namespace QuotesApi.Repositories;

public interface IUserRepository
{
    Task<User?> GetByIdAsync(int id, CancellationToken ct);
    Task<User?> GetByEmailAsync(string email, CancellationToken ct);
    Task AddAsync(User user, CancellationToken ct);
    Task SaveChangesAsync(CancellationToken ct);
}
