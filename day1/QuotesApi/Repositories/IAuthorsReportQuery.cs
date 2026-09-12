using QuotesApi.Dtos;

namespace QuotesApi.Repositories;

public interface IAuthorsReportQuery
{
    Task<List<AuthorSummary>> GetAsync(CancellationToken ct);
}
