# Day 21 — HybridCache + stampede protection

## The cache wiring

`Repositories/AuthorsReportQueries.cs` — the decorator actually wired to
`IAuthorsReportQuery` (see `ServiceCollectionExtensions.AddInfrastructure`):

```csharp
public class CachedAuthorsReportQuery(HybridCache cache, EfAuthorsReportQuery inner) : IAuthorsReportQuery
{
    public const string CacheKey = "reports:authors";

    public Task<List<AuthorSummary>> GetAsync(CancellationToken ct) =>
        cache.GetOrCreateAsync(
            CacheKey,
            factory: async token => await inner.GetAsync(token),
            cancellationToken: ct).AsTask();
}
```

`ServiceCollectionExtensions.cs`:

```csharp
services.AddHybridCache(options =>
{
    options.DefaultEntryOptions = new HybridCacheEntryOptions
    {
        Expiration = TimeSpan.FromSeconds(30),
        LocalCacheExpiration = TimeSpan.FromSeconds(10),
    };
});

var redisConnectionString = config.GetConnectionString("Redis");
if (!string.IsNullOrEmpty(redisConnectionString))
{
    services.AddStackExchangeRedisCache(o => o.Configuration = redisConnectionString);
}
```

**Redis, not Azure Cache for Redis:** `Microsoft.Extensions.Caching.StackExchangeRedis`
talks to any Redis - the free, open-source, self-hosted one in `docker-compose.yml`
included. `HybridCache` is always registered even with no Redis running: with no
`IDistributedCache` behind it, it degrades to a correct, single-node, in-memory-only
cache (L1 only) - which is exactly what makes `dotnet run` and the test suite work
with nothing running. Redis plugs in as the L2/backplane only when
`ConnectionStrings:Redis` is actually configured (same opt-in pattern as
`RabbitMq:HostName` in Day 19):

```bash
docker compose up -d
dotnet user-secrets set "ConnectionStrings:Redis" "localhost:6379" --project QuotesApi
```

## Stampede protection under concurrency

`HybridCache.GetOrCreateAsync` is a *coalescing* cache: concurrent calls for the same
key that all miss don't each run the factory - they share one in-flight call and one
result. Without that, N concurrent requests racing a just-expired key would each run
`EfAuthorsReportQuery.GetAsync` independently (a thundering herd hitting the
database at once); with it, the herd becomes exactly one database query no matter
how many requests are waiting on it.

## Load test: before/after

`loadtest-authors-report.ps1` fires 50 concurrent requests at
`GET /api/reports/authors` in two bursts and reads `EfAuthorsReportQuery`'s own
DB-query counter (exposed via `GET /api/reports/authors/stats`, a diagnostics-only
endpoint that exists purely for this exercise) before and after each burst.
`POST /api/reports/authors/stats/reset` resets both the counter and evicts the cache
entry, so "Burst 1" always starts genuinely cold.

Run against a local `dotnet run`, 3 quotes across 2 authors seeded first:

```
Target: http://localhost:5062  Concurrency: 50

== Burst 1: cold cache (stampede protection) ==
Requests:        50
Failures:        0
p50 latency:     2.0 ms
p99 latency:     37.8 ms
DB queries hit:  1
Cache hit rate:  98.0%

== Burst 2: warm cache ==
Requests:        50
Failures:        0
p50 latency:     2.6 ms
p99 latency:     6.3 ms
DB queries hit:  0
Cache hit rate:  100.0%

Stampede protection confirmed: 50 concurrent cold requests produced only 1 DB query.
```

**Reading this:** 50 concurrent requests racing a cold cache entry produced **1**
database query, not 50 - that's the coalescing behavior directly. The one request
that "loses the race" and actually runs the factory pays a higher p99 (37.8ms vs.
Burst 2's 6.3ms) because it's the one doing real work; every other concurrent
request just waits on that same in-flight call and gets the shared result. Once the
entry is warm (Burst 2), every request is a pure cache hit: 0 additional DB queries
for another 50 requests, and both p50 and p99 drop since nothing touches the
database at all.
