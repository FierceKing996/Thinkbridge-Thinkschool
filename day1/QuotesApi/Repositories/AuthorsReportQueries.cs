using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using QuotesApi.Data;
using QuotesApi.Dtos;

namespace QuotesApi.Repositories;

// EF Core version - what's live behind GET /api/reports/authors. Same query
// that replaced the N+1 loop: one GROUP BY, backed by IX_Quotes_Author.
public class EfAuthorsReportQuery(AppDbContext db) : IAuthorsReportQuery
{
    // Day 21's load-test instrumentation: counts how many times this query actually
    // hit the database, so a before/after run against CachedAuthorsReportQuery can
    // show the number collapsing from "one per request" to "one per cache miss" -
    // see GET /api/reports/authors/stats and loadtest-authors-report.ps1. Static
    // (not a metric/counter service) purely to keep the exercise's instrumentation
    // out of the DI graph the actual caching code depends on.
    private static long _queryCount;
    public static long QueryCount => Interlocked.Read(ref _queryCount);
    public static void ResetQueryCount() => Interlocked.Exchange(ref _queryCount, 0);

    public Task<List<AuthorSummary>> GetAsync(CancellationToken ct)
    {
        Interlocked.Increment(ref _queryCount);
        return db.Quotes
            .Where(q => !q.IsDeleted)
            .GroupBy(q => q.Author)
            .Select(g => new AuthorSummary(
                g.Key,
                g.Count(),
                g.OrderByDescending(q => q.Id).Select(q => q.Text).FirstOrDefault()))
            .ToListAsync(ct);
    }
}

// Dapper version - the exact SQL EF generates for the query above (captured via
// LogTo during the profiling exercise this endpoint came out of), executed
// directly. Same index, same query plan; the only thing this skips is EF's
// materialization pipeline - no expression tree, no entity-shaper delegate, no
// change tracker even in the untracked case. Reuses AppDbContext's own
// connection rather than opening a second one, so it still shares the
// request's transaction/scope.
public class DapperAuthorsReportQuery(AppDbContext db) : IAuthorsReportQuery
{
    private const string Sql = """
        SELECT "Author", COUNT(*) AS "QuoteCount", (
            SELECT "Text" FROM "Quotes" AS "q0"
            WHERE NOT ("q0"."IsDeleted") AND "Quotes"."Author" = "q0"."Author"
            ORDER BY "q0"."Id" DESC
            LIMIT 1) AS "MostRecentQuoteText"
        FROM "Quotes"
        WHERE NOT ("IsDeleted")
        GROUP BY "Author";
        """;

    // SQLite's COUNT(*) always comes back as INTEGER storage class, which
    // Microsoft.Data.Sqlite maps to Int64 - no CAST changes that, SQLite has no
    // narrower integer type. AuthorSummary.QuoteCount is int, so Dapper's
    // constructor-matching rejects the (long, not int) mismatch outright rather
    // than narrowing it; this row type exists only to receive what SQLite
    // actually returns before converting.
    private sealed record Row(string Author, long QuoteCount, string? MostRecentQuoteText);

    public async Task<List<AuthorSummary>> GetAsync(CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        var rows = await connection.QueryAsync<Row>(new CommandDefinition(Sql, cancellationToken: ct));
        return rows.Select(r => new AuthorSummary(r.Author, (int)r.QuoteCount, r.MostRecentQuoteText)).ToList();
    }
}

// Day 21: the query actually wired to IAuthorsReportQuery (see
// ServiceCollectionExtensions). HybridCache.GetOrCreateAsync is where the
// stampede protection comes from - it's a *coalescing* cache: concurrent calls
// for the same key that all miss don't each run the factory, they share one
// in-flight call and one result. Without that, N concurrent requests hitting a
// just-expired key would fan out into N identical GetAsync() calls hitting the
// database at once (a "thundering herd") - see DAY21_HYBRIDCACHE.md for the
// load-test numbers with and without this wrapper in front of EfAuthorsReportQuery.
public class CachedAuthorsReportQuery(HybridCache cache, EfAuthorsReportQuery inner) : IAuthorsReportQuery
{
    public const string CacheKey = "reports:authors";

    public Task<List<AuthorSummary>> GetAsync(CancellationToken ct) =>
        cache.GetOrCreateAsync(
            CacheKey,
            factory: async token => await inner.GetAsync(token),
            cancellationToken: ct).AsTask();
}
