using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Hybrid;
using QuotesApi.Repositories;

namespace QuotesApi.Controllers;

[ApiController]
[Route("api/reports")]
public class ReportsController : ControllerBase
{
    private readonly IAuthorsReportQuery _authorsReport;
    private readonly HybridCache _cache;

    public ReportsController(IAuthorsReportQuery authorsReport, HybridCache cache)
    {
        _authorsReport = authorsReport;
        _cache = cache;
    }

    // GET /api/reports/authors - EF by default (IAuthorsReportQuery resolves to
    // EfAuthorsReportQuery, see ServiceCollectionExtensions.AddInfrastructure).
    // DapperAuthorsReportQuery is the same query, same index, hand-timed against
    // it - see the Day-9 Dapper-vs-EF exercise - but isn't wired in here: this
    // endpoint's numbers (43ms p50 under load) don't need it.
    [HttpGet("authors")]
    public async Task<IActionResult> GetAuthors(CancellationToken ct)
    {
        var summaries = await _authorsReport.GetAsync(ct);
        return Ok(summaries);
    }

    // Day 21 load-test instrumentation only - see loadtest-authors-report.ps1 and
    // DAY21_HYBRIDCACHE.md. Not something a production report endpoint would
    // expose; it exists purely to make the stampede-protection numbers visible.
    [HttpGet("authors/stats")]
    public IActionResult GetAuthorsStats() => Ok(new { dbQueryCount = EfAuthorsReportQuery.QueryCount });

    // Evicts the cache entry too, not just the counter - otherwise a load-test run
    // that starts with a still-warm entry (e.g. one left over from manual testing)
    // would report a false "0 DB queries" for what's supposed to be the cold-cache
    // burst, rather than actually exercising the coalescing path.
    [HttpPost("authors/stats/reset")]
    public async Task<IActionResult> ResetAuthorsStats(CancellationToken ct)
    {
        EfAuthorsReportQuery.ResetQueryCount();
        await _cache.RemoveAsync(CachedAuthorsReportQuery.CacheKey, ct);
        return NoContent();
    }
}
