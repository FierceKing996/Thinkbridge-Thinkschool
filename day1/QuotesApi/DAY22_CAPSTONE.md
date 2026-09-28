# Day 22 — Capstone kickoff: design + scaffold

**The product slice:** QuotesApi itself — the app this whole 27-day sequence has
been building, not a new one. It already has a real domain (quotes, collections,
auth, reporting), a real async story (outbox → messaging, background jobs,
caching), and a real deployment target (see `publish-monsterasp.ps1`), so it's the
capstone rather than a new toy example. This doc is the one-pager; the repo itself
(no public URL — local project, see "Scaffold" below for the layout in place of a
link) is the scaffold.

## Modular monolith, not microservices

One deployable (`QuotesApi.csproj` → one process, one `Program.cs`), internally
partitioned by bounded context instead of by service boundary. The reason isn't
"microservices are hard" in the abstract — it's that these contexts share one
transaction boundary constantly (Day 20's outbox row commits in the *same*
transaction as the quote it describes; a network call across service boundaries
can't do that), and they're developed by the same small surface area at once. A
modular monolith gets the organizational benefit of clear boundaries without
paying for distributed transactions this app doesn't need yet. If a context's load
or team ever outgrows this (e.g. the search-index consumer needing independent
scaling), the RabbitMQ boundary already drawn around it (Day 19) is exactly where
it would peel off into its own service — the seam is already there, just not cut.

## Bounded contexts (folders = contexts)

| Context | Folder(s) | Owns |
|---|---|---|
| **Auth** | `Auth/`, `Services/{AuthService,TokenService}.cs` | Login, JWT/Entra validation, authorization policies |
| **Quotes** | `Controllers/QuotesController.cs`, `Models/Quote.cs`, `Repositories/QuoteRepository.cs` | The core aggregate — create/read/delete |
| **Collections** | `Models/{Collection,CollectionItem}.cs`, `Repositories/Collection*.cs`, `Services/AddCollectionItemCommandHandler.cs` | Grouping quotes; its own aggregate + invariants |
| **Reporting** | `Controllers/ReportsController.cs`, `Repositories/AuthorsReportQueries.cs` | Read-only projections (authors report), cached |
| **Messaging** | `Messaging/`, `Models/{OutboxMessage,ProcessedMessage,AuditLogEntry}.cs`, `Services/OutboxRelayService.cs` | Cross-context integration events (`QuoteCreated`) |
| **Background jobs** | `Services/BackgroundJobs/`, `Controllers/ExportsController.cs` | Work moved off the request thread |

Each context's own controller/model/repository files only reference its own
models directly; cross-context communication happens either through a repository
interface (in-process) or through the Messaging context's events (`quote.created`)
— never by one context reaching into another's DbSet directly. `AppDbContext` is
the one place all contexts' entities are registered, which is the modular-monolith
tradeoff made explicit: one database, many contexts, discipline instead of network
boundaries enforcing the separation.

## The core aggregate

**`Quote`** (`Models/Quote.cs`) — the aggregate this whole system exists to serve:

```csharp
public class Quote
{
    public int Id { get; private set; }
    public string Author { get; private set; } = string.Empty;
    public string Text { get; private set; } = string.Empty;
    public bool IsDeleted { get; private set; }
    public int CreatedByUserId { get; private set; }

    private Quote() { } // EF materialization only

    public static QuoteCreationResult Create(string author, string text, int createdByUserId) { /* invariants */ }
    public void Delete() => IsDeleted = true;
}
```

Rich, not anemic: `Create` is the only way to construct a valid one (invariants
centralized, not copy-pasted per call site), and there's no `Text`/`Author` setter
— once created, those fields are immutable by construction, not by convention.
`Collection` is a second aggregate root with the same shape (private setters,
factory/behavior methods, `CollectionItem` as an owned child collection) — see
`Models/Collection.cs`.

## Async flows

1. **Quote created → search-index + audit-log, at-least-once, exactly-once processed.**
   `QuoteRepository.CreateAsync` writes the `Quote` and an `OutboxMessage` in one EF
   transaction → `OutboxRelayService` polls unprocessed rows and publishes to the
   `quotes.events` RabbitMQ topic exchange → two independent subscriptions
   (`quotes.search-index`, competing consumers; `quotes.audit-log`, single
   consumer) each dedupe by `MessageId` before acting. A crash between "publish
   succeeded" and "row marked processed" redelivers safely — see `DAY20_OUTBOX.md`.

2. **CSV export → background drain, not the request thread.**
   `POST /api/exports/quotes` enqueues a work item on an in-memory
   `BackgroundTaskQueue`; `QueuedHostedService` drains it and writes the file;
   `GET /api/exports/quotes/{id}` polls status. See `DAY18_BACKGROUND_JOBS.md`.

3. **Authors report → coalesced cache, not a thundering herd.**
   `GET /api/reports/authors` goes through `CachedAuthorsReportQuery`
   (`HybridCache.GetOrCreateAsync`), which collapses concurrent cache misses for
   the same key into one database query instead of one per concurrent request.
   See `DAY21_HYBRIDCACHE.md`.

## Scaffold

The solution structure *is* the scaffold — no separate step was needed because
each bounded context's folder was created as its exercise landed:

```
day1/
  QuotesApi/                     # the modular monolith
    Auth/                        # Auth context
    Controllers/                 # one per context: Auth, Quotes, Collections, Reports, Exports
    Models/                      # aggregates + entities, one file per type
    Repositories/                # persistence + query interfaces, one per aggregate
    Services/                    # cross-cutting: clock, tokens, background jobs
      BackgroundJobs/            # Day 18
    Messaging/                   # Day 19 - RabbitMQ pub/sub, topology, events
    Data/AppDbContext.cs         # the one shared persistence boundary
    Migrations/                  # SQLite migrations
  QuotesApi.Migrations.SqlServer/  # SQL Server migrations (same model, second provider)
  Quotes.Tests.Unit/              # fast, no I/O
  Quotes.Tests.Integration/       # real HTTP pipeline, SQLite + SQL Server (Testcontainers)
  QuotesApi.Tests.Domain/         # aggregate invariants in isolation
```
