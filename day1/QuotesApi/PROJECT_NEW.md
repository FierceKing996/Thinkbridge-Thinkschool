# QuotesApi — Project Overview (post Day 18–27)

Written for: you, as a map of what this project is now and where everything
lives — not a tutorial, a reference to come back to.

## 1. What this is

QuotesApi is a small "quotes" API (create/list/delete quotes, group them into
named collections) built incrementally over a multi-week exercise series. It
started as a plain CRUD API and has grown, day by day, into a small **modular
monolith**: one deployable process, internally organized into clear bounded
contexts, with real production-shaped concerns bolted on — background jobs,
event-driven messaging with a transactional outbox, caching with stampede
protection, resilience patterns, secrets management, observability, and a
security pass. `DAY22_CAPSTONE.md` is the one-page design doc for this framing
(bounded contexts, the core aggregate, the async flows) — this document goes
one level more concrete: which file does what, and how a request actually
travels from the Angular UI down to SQLite and back.

The frontend is `quotes-ui/` (Angular, zoneless, standalone components,
signals). The backend is `day1/QuotesApi/` (ASP.NET Core 10, EF Core, SQLite by
default). They're two separate projects; the backend serves the built Angular
app as static files in production (`app.UseStaticFiles()` in `Program.cs`) but
in local dev they run as two separate processes talking over HTTP.

## 2. Architecture in one picture

```
Angular UI (quotes-ui)
   │  HTTP (interceptor pipeline: error-mapping → auth → retry)
   ▼
ASP.NET Core Controllers  (Controllers/*.cs)
   │
   ▼
Services / Command Handlers  (Services/*.cs)
   │
   ▼
Repositories  (Repositories/*.cs)
   │
   ▼
AppDbContext (Data/AppDbContext.cs) ── EF Core ── SQLite (dev) / SQL Server (prod-capable)
```

Cutting across that vertical stack, three things run independently of any
single HTTP request:

- **`QueuedHostedService`** drains an in-memory background job queue (Day 18).
- **`OutboxRelayService`** polls for unpublished domain events and publishes
  them to RabbitMQ (Day 20, feeding Day 19's consumers).
- **RabbitMQ consumers** (`QuoteSearchIndexConsumerService`,
  `QuoteAuditLogConsumerService`) listen for those events independently of the
  request that created them (Day 19).

## 3. Folder map (bounded contexts)

| Folder | Context | Contains |
|---|---|---|
| `Auth/` | Auth | JWT/Entra scheme selection, resilience pipeline for the Entra backchannel |
| `Controllers/` | (entry points, one per context) | `AuthController`, `QuotesController`, `CollectionsController`, `ReportsController`, `ExportsController` |
| `Models/` | Domain | Rich aggregates: `Quote`, `Collection`, `User`, `RefreshToken`, `OutboxMessage`, `ProcessedMessage`, `AuditLogEntry` |
| `Repositories/` | Persistence + queries | One repository/query class per aggregate |
| `Services/` | Cross-cutting + app services | `AuthService`, `TokenService`, `IClock`, `Services/BackgroundJobs/` (Day 18), `OutboxRelayService` (Day 20) |
| `Messaging/` | Integration events | Everything RabbitMQ-related (Day 19/20) |
| `Data/` | Persistence boundary | `AppDbContext` — the one place every context's entities are registered |
| `Extensions/` | DI wiring | `ServiceCollectionExtensions.AddInfrastructure` — almost everything below is registered here |
| `Migrations/` | EF migrations (SQLite) | — |

## 4. What's new (Days 18–27), explained plainly, and exactly where it lives

Each of these started as a real problem that shows up in almost every
production system, not an arbitrary feature to bolt on. The plain-English
version of the problem comes first; the file locations are where to go read
the actual solution.

### Day 18 — Background jobs

**The problem, in plain terms:** some work is too slow to make a user wait for
it inside a single request — think of a waiter at a restaurant. If the waiter
had to stand at your table until the kitchen finished cooking your food before
serving anyone else, the restaurant would grind to a halt. Instead, the waiter
takes the order, hands it to the kitchen, and immediately moves on to the next
table. The kitchen works through orders in the background at its own pace.

**The solution here:** a request drops a "job" into an in-memory queue and
gets an immediate reply (a job id to check later); a separate background
process drains that queue one item at a time, whenever it gets to it.

- **What**: a bounded, in-process queue + a `BackgroundService` that drains it, used to move a CSV export off the request thread.
- **Where**:
  - Queue: `Services/BackgroundJobs/IBackgroundTaskQueue.cs`, `BackgroundTaskQueue.cs`
  - Drain loop: `Services/BackgroundJobs/QueuedHostedService.cs`
  - The concrete job: `Services/BackgroundJobs/QuoteCsvExportJob.cs`
  - HTTP surface: `Controllers/ExportsController.cs` (`POST /api/exports/quotes`, `GET /api/exports/quotes/{id}`, `GET /api/exports/quotes/{id}/download`)
  - DI registration: `Extensions/ServiceCollectionExtensions.cs` (`AddSingleton<IBackgroundTaskQueue>`, `AddHostedService<QueuedHostedService>`)
- **Reachable from the Angular UI?** No — this is backend-only today; nothing in `quotes-ui/` calls `/api/exports/*`. See §5.6 for the exact call trace.

### Day 19 — Messaging (RabbitMQ instead of Azure Service Bus)

**The problem, in plain terms:** when something happens ("a quote was
created"), several *other* parts of the system might care — a search index
needs updating, an audit log needs a row, maybe a notification needs sending.
You could call all of them directly from the code that created the quote, but
then that one piece of code has to know about every future thing that might
ever care, and if any one of those calls is slow or fails, it drags down the
original request too.

**The solution here:** publish one message ("a quote was created") to a
message broker, like posting a notice on a public bulletin board instead of
calling every interested person individually. Anyone who cares can subscribe
and read it whenever they're ready, completely independently of each other and
of whoever posted the notice.

- **What**: a topic exchange (`quotes.events`) with two independent subscriptions and a competing-consumer worker pool, idempotent dedupe, dead-letter handling.
- **In plain terms**: "topic exchange" = the bulletin board itself; "subscription" = one reader who's decided to check that board regularly; "competing consumers" = several readers checking the same board so the workload splits between them instead of piling on one; "idempotent dedupe" = if the same notice accidentally gets read twice, only act on it once; "dead-letter" = if a notice is garbled and nobody can act on it, set it aside in a separate pile instead of getting stuck re-reading it forever.
- **Where**: everything under `Messaging/` — `RabbitMqOptions.cs`, `RabbitMqConnection.cs`, `RabbitMqTopology.cs`, `RabbitMqEventPublisher.cs`, `QuoteSearchIndexConsumerService.cs`, `QuoteAuditLogConsumerService.cs`, plus `Models/ProcessedMessage.cs` and `Models/AuditLogEntry.cs` for dedupe/audit storage.
- **Trigger**: not called by any controller directly — it's driven by Day 20's outbox relay, below.
- **Opt-in**: only active when `RabbitMq:HostName` is configured (`Extensions/ServiceCollectionExtensions.cs`); otherwise `NoOpEventPublisher` is used and no consumers start. See §5.7 for the full call trace.

### Day 20 — Transactional outbox

**The problem, in plain terms:** say you want to "save a quote to the
database" AND "tell everyone a quote was created" to always happen together.
What if the database save succeeds but the notification never goes out because
the network hiccupped right after? Now your systems disagree about reality —
the quote exists, but nobody else ever finds out. Doing them as two separate
steps means one can succeed while the other fails, silently.

**The solution here (the "outbox pattern"):** instead of trying to do the
database save AND the network call in one risky step, write down "a
notification needs to go out" as a row in the *same database transaction* as
the save itself — like writing a reminder note in your notebook in the exact
same breath as doing the actual task, so the note and the task can never
become inconsistent with each other. A separate process checks the notebook
regularly and does the actual notifying, crossing the note off only once it
genuinely succeeds.

- **What**: every `Quote` creation also writes an `OutboxMessage` row in the *same* EF transaction; a separate relay publishes it later.
- **Where**:
  - The outbox write: `Repositories/QuoteRepository.cs`, method `CreateAsync` — this is the one place a Day 20 change touches existing Day-1 CRUD code.
  - The entity: `Models/OutboxMessage.cs`
  - The relay: `Services/OutboxRelayService.cs` (a `BackgroundService`, polls every 2s)
- **So**: creating a quote via the UI (`POST /api/quotes`) *does* write an outbox row today, even though the UI never sees it — the relay picks it up in the background and (if RabbitMQ is configured) publishes it. See §5.7 for the full trace, including what happens if the relay crashes mid-way.

### Day 21 — HybridCache + stampede protection

**The problem, in plain terms:** a cache is just a sticky note with an answer
you already worked out, so the next time someone asks the same question you
can hand them the sticky note instead of redoing the work. The tricky part:
what if 50 people ask the exact same question at the exact same instant,
right when the sticky note has gone stale? Without care, all 50 people trigger
the expensive work simultaneously — a "cache stampede" that hits the database
50 times at once for one question, which can be worse than having no cache at
all.

**The solution here:** the cache notices that 50 identical requests are
already in flight for the same answer, lets exactly *one* of them go do the
real work, and makes the other 49 simply wait for that one answer and share
it — no stampede, no matter how many people ask at once.

- **What**: the authors report is wrapped in `HybridCache`, which coalesces concurrent cache misses into a single database call.
- **Where**:
  - The cache wrapper: `Repositories/AuthorsReportQueries.cs`, class **`CachedAuthorsReportQuery`** — this is the class actually registered for `IAuthorsReportQuery` (see `ServiceCollectionExtensions.cs`); `EfAuthorsReportQuery` (same file) is the real query it wraps and instruments (see its `QueryCount` static counter).
  - Registration: `Extensions/ServiceCollectionExtensions.cs` — `services.AddHybridCache(...)`, then `services.AddScoped<IAuthorsReportQuery>(sp => new CachedAuthorsReportQuery(...))`.
  - HTTP surface: `Controllers/ReportsController.cs` — `GET /api/reports/authors` (the cached endpoint), plus `GET /api/reports/authors/stats` and `POST /api/reports/authors/stats/reset` (load-test instrumentation only).
  - Optional Redis L2 backplane: `ConnectionStrings:Redis`, wired in the same `AddInfrastructure` method via `AddStackExchangeRedisCache`.
- **Reachable from the Angular UI?** No — `quotes-ui/` has no "authors report" screen; this is a backend-only endpoint today, exercised via `loadtest-authors-report.ps1` or curl/Swagger. See §5.8 for the exact trace of a cache hit vs. a cache miss vs. a stampede.

### Day 22 — Resilience with Polly

**The problem, in plain terms:** any time your app calls another service over
a network, that call can fail in ways that have nothing to do with your own
code — a blip, a slow response, the other service being genuinely down. Doing
nothing about it means one flaky dependency can freeze or crash your whole
app.

**The solution here, using a phone-call analogy for each of the four layers:**
- **Retry** — if the call drops, redial a couple of times before giving up.
- **Circuit breaker** — if you've redialed the same number and it's failed
  most of the time recently, stop dialing it for a while and fail instantly
  instead — calling a number you already know is dead just wastes your time
  and theirs. After a cooldown, try one call to see if they're back; if that
  works, go back to normal.
- **Bulkhead** — (named after the watertight compartments in a ship's hull
  that stop one flooded section from sinking the whole ship) don't let calls
  to one flaky dependency use up *every* phone line you have — cap how many
  can be in flight at once so the rest of the app still has lines free.
- **Timeout** — never let a single call ring forever; hang up after a fixed
  time no matter what.

- **What**: a 4-stage resilience pipeline (bulkhead → retry → circuit breaker → timeout) around the one real outbound HTTP dependency this app has: the Entra OIDC backchannel.
- **Where**: `Auth/AuthenticationExtensions.cs`, method `ConfigureResilience` — this wraps the `HttpClient` registered under the name `"entra-backchannel"`, used only when Entra auth is configured (`EntraOptions`).
- **Not related to the Angular UI's own HTTP calls** — the UI's requests go through Angular's own interceptor pipeline (`core/http/`), which is a completely separate, client-side concern (see §5). See §5.9 for the backend trace.

### Day 23/24 — IaC + deployment (Terraform)

**The problem, in plain terms:** setting up servers/databases/message brokers
by clicking through a dashboard or typing commands by hand works once, but
nobody can tell later exactly what was done, and doing it again identically
(on a second machine, or after accidentally deleting something) means
remembering every click perfectly.

**The solution here (Infrastructure as Code):** write down *what should
exist* in a text file, and use a tool that reads the file and makes reality
match it — creating what's missing, leaving alone what already matches, and
flagging anything that's drifted out of sync. `terraform plan` is "show me
what would change without changing anything yet"; `terraform apply` actually
does it; `terraform destroy` cleanly removes everything the file describes.

- **Where**: `infra-terraform/` — `main.tf`, `variables.tf`, `modules/{api,database,messaging}/`, `environments/{dev,prod}.tfvars`, `deploy.ps1`.
- Purely infrastructure-as-code; no application code changed for this day (though running it for real did surface and fix three real application bugs — see `DAY23_24_IAC.md`). See §5.10 for the trace of what `terraform apply` actually does, step by step.

### Day 25 — Identity (HashiCorp Vault instead of Azure Key Vault)

**The problem, in plain terms:** a password typed directly into a code file
is a password anyone who can read that file now knows — including, if the
file ever gets committed to source control, anyone who ever gets a copy of
that repository, forever, even after you "fix" it later.

**The solution here:** never let the secret sit in a file at all. Fetch it at
the last possible moment, at startup, from a system built specifically to
guard secrets (Vault) and hand them out only to callers it recognizes and
trusts — or, more simply, read it from an environment variable that's set
once, on the machine that's actually running the app, and never written down
anywhere else.

- **What**: secrets (the JWT signing key) come from Vault (AppRole auth) or a plain environment variable — never a committed file.
- **Where**:
  - `Extensions/VaultConfigurationExtensions.cs` — `AddVaultSecrets`, called from `Program.cs` only when `Vault:Address` is configured.
  - The fix: `appsettings.Production.json` no longer contains a plaintext `Jwt:SigningKey`.
- **Not visible to the frontend at all** — this is purely how the backend process itself boots. See §5.11 for the exact startup-time trace.

### Day 26 — Observability (Jaeger + Prometheus + Grafana instead of App Insights)

**The problem, in plain terms:** a log line tells you *that* something
happened ("request finished"), but not always *how long each step took* or
*how one request's work threads through several different processes*.
Without that, "the app feels slow" is nearly impossible to actually diagnose —
you're guessing.

**The solution here:** give every request a unique id (a "trace") the moment
it starts, and have every step it touches — the web request, a database
query, a background job it kicked off — stamp that same id on its own work.
Line them all up afterward and you get a timeline of exactly where the time
went, across process boundaries, not just within one. **Metrics** are the
complementary piece: instead of one detailed story per request, they're
running counters and timers ("how many requests hit this endpoint in the last
minute, and how long did the slowest 1% take") that are cheap to keep forever
and great for spotting trends and firing alerts.

- **What**: OpenTelemetry traces (already existed) now export to Jaeger; a new metrics pipeline exports to Prometheus; the Day-18 export job explicitly propagates trace context across the background-job boundary.
- **Where**:
  - `Program.cs` — the `.WithMetrics(...)` block and `app.MapPrometheusScrapingEndpoint()` (serves `GET /metrics`).
  - Trace propagation: `Controllers/ExportsController.cs` (captures `Activity.Current?.Context` before enqueueing) and `Services/BackgroundJobs/QuoteCsvExportJob.cs` (starts a child `Activity` from that captured context, and pushes the same `TraceId` onto Serilog's `LogContext`).
  - Local stack: `docker-compose.yml` (`jaeger`, `prometheus`, `grafana` services) + `observability/prometheus.yml`.
- See §5.12 for the trace of how one trace id survives crossing from an HTTP request into a background job.

### Day 27 — Security pass

**The problem, in plain terms:** most security holes aren't exotic — they're
ordinary oversights nobody happened to test: a response that leaks more detail
than intended, a request size nobody thought to cap, an API description that
doesn't mention it actually requires a login. A security pass is deliberately
going looking for exactly these kinds of gaps in your own system before
someone else finds them for you.

**The solution here:** a lightweight threat model (STRIDE-lite — a checklist
of six ways things typically go wrong: someone pretending to be someone else,
data being tampered with, actions being deniable, information leaking out,
the system being knocked over, or someone gaining permissions they shouldn't
have), a rule that the data tier is reachable only from the app itself and
nothing else, a request-size limit, and an API description that accurately
says what actually requires a login.

- **What**: a STRIDE-lite threat model, a Docker-network isolation pattern for the data tier, OpenAPI hardening, and two real bugs found and fixed.
- **Where**:
  - OpenAPI security scheme: `Extensions/BearerSecuritySchemeTransformer.cs`.
  - API versioning: `[Asp.Versioning.ApiVersion("1.0")]` on every controller class; wiring in `Program.cs` (`AddApiVersioning`).
  - Request size limit: `Program.cs` — `builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 64 * 1024)`.
  - **Real fix #1**: `Program.cs`'s global exception handler now returns the correct status code (e.g. 413) for `BadHttpRequestException` instead of always 500.
  - **Real fix #2**: the same handler no longer echoes raw exception messages back to the client on a genuine 500 (information-disclosure fix).
- See §5.13 for the trace of exactly where in the pipeline each of these checks happens.

## 5. Tracing a request: frontend → backend, function by function

The Angular app only exercises a subset of the API — login, quotes CRUD, and
collections. None of the Day 18–27 additions above are called from the UI
yet; they're proven via curl/PowerShell scripts (`loadtest-authors-report.ps1`),
a terminal, and the test suites instead. This section (§5.0–§5.5) traces the
UI-driven flows; §5b right after it traces every Day 18–27 feature the exact
same way, just triggered by something other than a browser click.

### 5.0 Every request first passes through Angular's own interceptor chain

Registered in `quotes-ui/src/app/app.config.ts`:

```
errorMappingInterceptor  →  authInterceptor  →  retryInterceptor  →  (network)
```

- `core/auth/auth-interceptor.ts` — attaches `Authorization: Bearer <token>` to
  every **non-GET** request (except `/api/auth/*`). Token comes from
  `core/auth/auth.ts`'s in-memory signal (`_token`), itself seeded from
  `sessionStorage` on load.
- `core/http/retry-interceptor.ts` — retries **GET** requests up to twice, with
  backoff, but only on a real network failure (`status === 0`) or a 5xx —
  never a 4xx.
- `core/http/error-mapping-interceptor.ts` — catches whatever comes back and
  rethrows a typed `ApiError` (`core/http/api-error.ts`) instead of a raw
  `HttpErrorResponse`, so every component downstream deals with one shape.

### 5.1 Viewing the quote list

```
quote-list.ts (QuoteList component, constructor)
  → this.quoteService.getQuotes(page, pageSize)          [core/models/quote.ts]
  → HttpClient.get('/api/quotes?page=&size=')
  → (through the interceptor chain above)
  → Controllers/QuotesController.cs :: GetQuotes(page, size, ct)
  → Repositories/IQuoteRepository.cs :: GetPagedAsync(page, size, ct)
  → Repositories/QuoteRepository.cs :: GetPagedAsync
      → AppDbContext.Quotes.Where(!IsDeleted).Skip(...).Take(...).ToListAsync()
  → SQLite
  ← List<Quote> (serialized as QuoteDto[] by System.Text.Json's default camelCase)
  ← QuoteList.quotes signal set; filteredQuotes()/status() recompute
```

No auth header on this call (GET) — `QuotesController.GetQuotes` has no
`[Authorize]`.

### 5.2 Viewing one quote

```
quote-detail.ts (QuoteDetail, constructor, driven by the `id` route param)
  → this.quoteService.getQuoteById(id)                   [core/models/quote.ts]
  → HttpClient.get('/api/quotes/{id}')
  → Controllers/QuotesController.cs :: GetQuote(id, ct)
  → Repositories/QuoteRepository.cs :: GetByIdAsync(id, ct)
      → AppDbContext.Quotes.FindAsync([id])
  → SQLite
  ← 200 + Quote, or 404 (empty body) if not found / soft-deleted
  ← QuoteDetail.data signal set, or .error set via ApiError.kind === 'notFound'
```

### 5.3 Creating a quote (the one write path with real Day 20 side effects)

```
create-quote.ts (CreateQuote component, form submit action)
  → this.quoteService.createQuote(author, text)          [core/models/quote.ts]
  → HttpClient.post('/api/quotes', { author, text })
  → auth-interceptor.ts attaches Authorization: Bearer <token>
      (this route IS guarded client-side by authGuard on 'quotes/new')
  → Controllers/QuotesController.cs :: CreateQuote(req, ct)
      → reads userId from the JWT "sub" claim
      → ITextNormalizer.Trim(author), Trim(text)          [Services/TextNormalizer.cs]
      → Models/Quote.cs :: Quote.Create(author, text, userId)   -- domain invariants
      → Repositories/QuoteRepository.cs :: CreateAsync(quote, ct)
          → BEGIN TRANSACTION
          → db.Quotes.Add(quote); SaveChangesAsync()        -- assigns quote.Id
          → db.OutboxMessages.Add(OutboxMessage.For("quote.created", payload, now))
          → SaveChangesAsync()
          → COMMIT
  ← 201 Created + the new Quote
  ← CreateQuote resets the form, shows success

  [independently, seconds later, NOT part of the HTTP response:]
  Services/OutboxRelayService.cs (background poll loop)
    → finds the new OutboxMessage row (ProcessedAt IS NULL)
    → Messaging/IEventPublisher.cs :: PublishAsync(...)
        → if RabbitMq:HostName configured: Messaging/RabbitMqEventPublisher.cs
            → publishes to the "quotes.events" topic exchange
            → Messaging/QuoteSearchIndexConsumerService.cs and
              QuoteAuditLogConsumerService.cs each independently consume it
        → if not configured: Messaging/NoOpEventPublisher.cs (logs, doesn't mark processed)
    → marks the OutboxMessage processed (only on successful publish)
```

The Angular UI never sees any of the "independently, seconds later" part — it
gets its `201 Created` response as soon as the transaction commits, which is
exactly the point of the outbox pattern (the slow/unreliable part — publishing
to a broker — is decoupled from the request).

### 5.4 Signing in

```
login.ts (Login component, logIn())
  → this.auth.login()                                    [core/auth/auth.ts]
  → HttpClient.post('/api/auth/login', DEMO_CREDENTIALS)
      (auth-interceptor.ts explicitly SKIPS /api/auth/* - no token to attach yet)
  → Controllers/AuthController.cs :: Login(req, ct)
  → Services/AuthService.cs :: LoginAsync(email, password, ct)
      → Repositories/IUserRepository.cs :: GetByEmailAsync
      → BCrypt.Net.BCrypt.Verify(password, user.PasswordHash)   [inside a custom
                                                                  OpenTelemetry span,
                                                                  "verify-password"]
      → IssueTokenPairAsync(user, ct)
          → Services/TokenService.cs :: CreateAccessToken(user)   -- mints the JWT
          → Services/TokenService.cs :: CreateRefreshToken()
          → Repositories/IRefreshTokenRepository.cs :: AddAsync(...)
  ← 200 + { access_token, refresh_token, expires_in }, or 401 if creds don't match
  ← auth.ts stores access_token in a signal + sessionStorage
  ← Login navigates to returnUrl (or /quotes)
```

### 5.5 Viewing a collection, adding/removing an item

```
collection-detail.ts (constructor effect, driven by the `id` route param)
  → this.store.load(id)                                  [collection-store.ts]
  → this.collectionService.getCollection(id)              [core/models/collection.ts]
  → HttpClient.get('/api/collections/{id}')
  → Controllers/CollectionsController.cs :: GetCollection(id, ct)
  → Repositories/ICollectionQueries.cs :: GetDetailAsync(id, ct)
  → Repositories/CollectionQueries.cs :: GetDetailAsync
      → one query for the collection header (Id, Name, OwnerId)
      → one join query: Collections.Items ⋈ Quotes on QuoteId  (enriches each
        item with author/text - the "read model")
  ← CollectionDetailResponse -> CollectionStore._items signal set
```

Adding an item (optimistic UI update, then reconciled with the server):

```
collection-detail.ts :: add()
  → this.store.addItem(quoteId)                          [collection-store.ts]
      → optimistically appends a placeholder row to _items, marks quoteId pending
      → this.collectionService.addItem(id, quoteId)
      → HttpClient.post('/api/collections/{id}/items', { quoteId })
          (auth-interceptor.ts attaches the bearer token - this route requires [Authorize])
      → Controllers/CollectionsController.cs :: AddItem(id, req, ct)
      → Services/AddCollectionItemCommandHandler.cs :: HandleAsync(command, ct)
          → Repositories/ICollectionRepository.cs :: GetByIdAsync(id, ct)
          → Models/Collection.cs :: AddItem(quoteId, now)     -- domain invariants
             (rejects duplicates, caps at 50 items - DomainException on violation)
          → Repositories/ICollectionRepository.cs :: UpdateAsync(collection, ct)
      ← 200 + the collection with BARE items (write model: no author/text)
      → CollectionStore.reconcile(response) - MERGES by quoteId, keeping the
        enriched author/text it already had, taking membership/order from the
        server response
```

Removing an item follows the same shape through `DELETE
/api/collections/{id}/items/{quoteId}` → `CollectionsController.RemoveItem` →
`Collection.RemoveItem(quoteId)` → `ICollectionRepository.UpdateAsync`.

## 5b. Tracing the Day 18–27 features, the same way

None of what follows is triggered by clicking something in `quotes-ui/` — each
one is triggered by a different kind of actor: a direct API call (curl,
Postman, a script), a timer running forever in the background, or a person
typing a command in a terminal. Same trace style as above: who calls what, in
which file, in what order.

### 5.6 Exporting quotes to CSV (Day 18)

```
(you, via curl/Postman - not the UI) POST /api/exports/quotes
  → Controllers/ExportsController.cs :: StartExport(ct)
      → creates a jobId (a Guid)
      → ExportJobStatusStore.Set(jobId, Queued)
      → captures the current trace context (Day 26's doing - see §5.12)
      → Services/BackgroundJobs/IBackgroundTaskQueue.cs :: QueueBackgroundWorkItemAsync(job)
  ← 202 Accepted + { jobId }   -- returns immediately; nothing has run yet

  [completely separately, on its own forever-loop:]
  Services/BackgroundJobs/QueuedHostedService.cs :: ExecuteAsync
    → dequeues the job the moment it's free to
    → runs Services/BackgroundJobs/QuoteCsvExportJob.cs
        → ExportJobStatusStore.Set(jobId, Running)
        → Repositories/IQuoteRepository.cs :: GetPagedAsync (pages through every quote)
        → writes App_Data/exports/{jobId}.csv
        → ExportJobStatusStore.Set(jobId, Completed)

(you, moments or minutes later) GET /api/exports/quotes/{jobId}
  → Controllers/ExportsController.cs :: GetStatus(jobId) -- just reads the in-memory status store
  ← { state: "Completed", fileName: "....csv" }

(you) GET /api/exports/quotes/{jobId}/download
  → Controllers/ExportsController.cs :: Download(jobId) -- streams the file back
```

The whole point: the first call returns in milliseconds regardless of how many
quotes exist, because the actual paging-and-writing work happens on a
completely different thread, on its own schedule.

### 5.7 The outbox → RabbitMQ → consumers pipeline (Days 19 & 20)

§5.3 already showed the *write* half of this (creating a quote writes an
outbox row). Here's the rest of the chain, which runs independently of any
request at all:

```
Services/OutboxRelayService.cs :: ExecuteAsync (loops forever, every 2 seconds)
  → RelayBatchAsync(ct)
      → SELECT * FROM OutboxMessages WHERE ProcessedAt IS NULL ORDER BY OccurredAt LIMIT 50
      → for each unsent row, one at a time:
          → Messaging/IEventPublisher.cs :: PublishAsync(routingKey, envelope, ct)
              → (if RabbitMq:HostName is configured) Messaging/RabbitMqEventPublisher.cs
                  → Messaging/RabbitMqConnection.cs :: CreateChannelAsync
                  → Messaging/RabbitMqTopology.cs :: DeclareAsync
                      -- makes sure the exchange/queues/dead-letter-queue exist;
                         safe to call every time, does nothing if they already do
                  → channel.BasicPublishAsync(exchange: "quotes.events", routingKey, body: <JSON envelope>)
              → (if not configured) Messaging/NoOpEventPublisher.cs -- logs, then throws,
                so the row below is correctly left unprocessed rather than
                pretending delivery happened
          → publish succeeded → OutboxMessage.MarkProcessed(now); SaveChangesAsync()
          → publish failed → do nothing; the row is untouched and gets tried
            again on the very next loop, 2 seconds later

-- meanwhile, completely independently, two more forever-loops are listening --

Messaging/QuoteSearchIndexConsumerService.cs :: ExecuteAsync
  (3 separate channels, all reading the SAME queue - "competing consumers":
   whichever one is free next gets the next message, splitting the workload)
  → channel.BasicConsumeAsync("quotes.search-index", ...)
  → for each delivered message:
      → deserialize the envelope
      → if the payload contains the literal text "POISON" → throw on purpose
        (this project's stand-in for "a message that can never succeed")
      → check the Models/ProcessedMessage.cs table for this exact MessageId
          → already there? → skip the work, but still acknowledge the message
            (this is the "idempotent dedupe" - the same message can arrive
            twice, e.g. after a relay crash, without being acted on twice)
          → not there? → do the work (logged as "indexing"), record a
            ProcessedMessage row, then acknowledge
      → any other unexpected exception → reject without requeueing, which
        (because the queue was set up with a dead-letter-exchange) makes
        RabbitMQ move it to quotes.events.dlq instead of retrying forever

Messaging/QuoteAuditLogConsumerService.cs :: ExecuteAsync
  (1 consumer, same shape as above, but writes a Models/AuditLogEntry.cs row
   instead of "indexing" - the second, independent subscriber to the exact
   same event)
```

### 5.8 Reading the cached authors report — hit, miss, and stampede (Day 21)

```
(you, via curl or loadtest-authors-report.ps1 - no UI screen for this yet)
GET /api/reports/authors
  → Controllers/ReportsController.cs :: GetAuthors(ct)
  → Repositories/AuthorsReportQueries.cs :: CachedAuthorsReportQuery.GetAsync(ct)
      → HybridCache.GetOrCreateAsync("reports:authors", factory, ct)

          CACHE HIT (someone already asked recently, entry hasn't expired):
          ← the cached answer comes back immediately; the database is never touched

          CACHE MISS (first ever request, or the entry expired):
          → runs the factory: EfAuthorsReportQuery.GetAsync(ct)
              → increments a counter used only by the load-test script
              → AppDbContext.Quotes.GroupBy(Author)....ToListAsync() - the real query
          → HybridCache stores the result under the key "reports:authors"

          50 CONCURRENT MISSES ARRIVING AT ONCE (the "stampede" scenario):
          → HybridCache lets exactly ONE of those 50 actually run the factory
            above; the other 49 simply wait and are handed that same one
            result once it's ready - the database sees ONE query, not 50
  ← 200 + [{ author, quoteCount, mostRecentQuoteText }, ...]
```

### 5.9 A resilient outbound call: the Entra backchannel (Day 22)

```
(a request arrives carrying a Bearer token issued by Microsoft Entra, not by
this app's own /api/auth/login)
  → ASP.NET Core's authentication layer needs Entra's public signing keys to
    check the token is genuine, so it calls out to Entra itself
  → that outbound call is made through the "entra-backchannel" HttpClient,
    wrapped by Auth/AuthenticationExtensions.cs :: ConfigureResilience -
    four layers, outermost first:
      1. Bulkhead        - reject immediately if too many of these calls are
                            already in flight (protects the rest of the app)
      2. Retry           - up to 3 attempts, waiting a bit longer each time
      3. Circuit breaker - if recent calls have mostly failed, stop trying
                            for a while and fail instantly instead
      4. Timeout         - give up on any single attempt after 10 seconds
  ← the real response, or a fast, deliberate failure if the circuit is open
```

### 5.10 Standing up real infrastructure with Terraform (Days 23/24)

```
(you, in a terminal, not the app itself) terraform apply
  → reads infra-terraform/main.tf + variables.tf + environments/dev.tfvars
  → talks to the "docker" provider plugin - a completely separate tool
    talking directly to Docker, nothing to do with the C# code at all
  → for each thing described in the .tf files:
      modules/database/main.tf   → creates the SQL Server container
      modules/messaging/main.tf  → creates the RabbitMQ container
      modules/api/main.tf        → creates the QuotesApi container itself,
                                    setting its environment variables
                                    (Jwt__SigningKey, RabbitMq__HostName, ...)
  → records exactly what it created in a state file

(later) terraform plan
  → re-checks what's REALLY running against both the .tf files and the state
  → prints "No changes" if everything still matches, or exactly what changed
    if someone/something altered things outside of Terraform ("drift")
```

### 5.11 Where the JWT signing key actually comes from at startup (Day 25)

```
the app process starts (dotnet run, or a container starting up)
  → Program.cs runs, near the very top, before almost anything else
      → reads config key "Vault:Address"
          → set? → Extensions/VaultConfigurationExtensions.cs :: AddVaultSecrets
              → authenticates to Vault using a RoleId/SecretId pair read from
                environment variables (never typed into any file)
              → asks Vault for the secret at path "secret/quotesapi"
              → merges what Vault returns into this app's own configuration,
                as if it had been in appsettings.json all along
          → not set? → do nothing here; the app instead expects
            Jwt__SigningKey to already be a real environment variable, or
            (locally) in user-secrets
  → later, Auth/AuthenticationExtensions.cs :: AddJwtAuth reads
    config["Jwt:SigningKey"] - by this point it doesn't know or care whether
    that value came from Vault, an environment variable, or user-secrets
```

### 5.12 A trace that survives a background job (Day 26)

```
POST /api/exports/quotes arrives
  → ASP.NET Core's own OpenTelemetry instrumentation automatically starts an
    "Activity" (a span) for this request - this is where the TraceId that
    shows up in every log line for this request comes from
  → Controllers/ExportsController.cs :: StartExport
      → reads Activity.Current?.Context BEFORE the request finishes
      → hands that captured context into the queued job
  ← 202 Accepted - the request's own Activity ends right here

  [seconds later, on a background thread with no ambient Activity of its own:]
  Services/BackgroundJobs/QuoteCsvExportJob.cs
      → Telemetry.Source.StartActivity("QuoteCsvExportJob.Process",
          parentContext: <the context captured above>)
          -- this one line is what makes the new span a CHILD of the
             original request's span, even though it's running on a
             completely different call stack, seconds later
      → LogContext.PushProperty("TraceId", activity.TraceId)
          -- so every log line this job writes carries the SAME TraceId as
             the original HTTP request's log line, provably (see
             DAY26_OBSERVABILITY.md for the actual matching log lines)
      → every EF Core query this job makes automatically becomes a child
        span too, with zero extra code
  → if a collector is configured (OTEL_EXPORTER_OTLP_ENDPOINT), all of this
    ships to Jaeger as one single trace: request → background job → each
    database query, in order, with real durations
```

### 5.13 Hardening a request before it even reaches your code (Day 27)

```
any incoming request
  → Kestrel (the web server itself) checks the request's size against
    MaxRequestBodySize (64KB)
      → too big? → throws BadHttpRequestException immediately;
        your controller code never even runs
  → Program.cs's global exception handler catches whatever went wrong:
      → it's that BadHttpRequestException → returns ITS real status code
        (413), not a generic 500
      → it's anything else → returns 500 with NO exception detail included
        in the response (this used to leak the raw exception message to
        every caller - a real bug, fixed this pass)
  → Asp.Versioning middleware reads the request's version (a header or query
    string; defaults to 1.0 if the caller doesn't specify one) and routes to
    whichever controller declares that [ApiVersion(...)]
  → (separately, whenever someone fetches GET /openapi/v1.json)
      → Extensions/BearerSecuritySchemeTransformer.cs adds a note to the
        generated API documentation saying "most of this needs a Bearer
        token" - previously the documentation didn't mention that at all
```

## 6. What to remember about this codebase's current shape

- The **backend has grown well ahead of the frontend** — Days 18/19/20/21/26
  added real, tested functionality (background jobs, messaging, outbox,
  caching, tracing) that has no UI yet. That's fine for an exercise sequence,
  but worth knowing before assuming "the app does X" means "you can click a
  button and see X."
- Every optional integration (RabbitMQ, Redis, Vault, Jaeger/Prometheus)
  follows the exact same pattern: **absent config = feature quietly disabled**,
  never a crash. Check `Extensions/ServiceCollectionExtensions.cs` and
  `Program.cs` for every `if (!string.IsNullOrEmpty(...))` gate — that's the
  full list of what's currently switched on vs. off in your environment.
- `DAY18_BACKGROUND_JOBS.md` through `DAY27_SECURITY.md` are the detailed,
  code-pasted write-ups for each individual day; this document is the map
  across all of them plus the frontend trace none of those individual docs
  cover.
