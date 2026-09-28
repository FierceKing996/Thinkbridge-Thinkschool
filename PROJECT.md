# QuotesApi + quotes-ui

A small "quotes" domain (create/list/collect quotations) used as a vehicle for practicing production-grade backend and frontend engineering: rich domain modeling, JWT auth with refresh-token rotation, CQRS, EF Core vs. Dapper, observability, resilience, load testing/perf tuning, and a signals-first Angular frontend built by directing (and reviewing) AI coding agents.

Two halves:

- **`day1/QuotesApi/`** — an ASP.NET Core 10 minimal-API backend.
- **`quotes-ui/`** — an Angular 21.2 (standalone, zoneless) frontend against it.

Everything below is grounded directly in the current source, not aspirational — every endpoint, shape, and behavior listed here is what the code actually does.

---

## 1. What it does

A user can:

- Log in (`POST /api/auth/login`) and get a short-lived access token + a rotating refresh token.
- List quotes, paginated, and view one (`GET /api/quotes`, `GET /api/quotes/{id}`).
- Create a quote (`POST /api/quotes`, requires the `quotes.write` scope).
- Delete a quote they own (`DELETE /api/quotes/{id}`).
- Group quotes into named **collections** (a separate aggregate, up to 50 items each, no duplicate quotes) and view a denormalized collection-detail screen.
- See an authors report (quote count + most recent quote per author) — the endpoint this project's N+1 and Dapper-vs-EF work centers on.

The frontend ships a create-quote form (with full client-side validation mirroring the server's exact invariants) and a list+detail view with pagination and author filtering, all built against the real API above.

---

## 2. Architecture at a glance

```
                        ┌─────────────────────────────┐
                        │        quotes-ui             │
                        │  Angular 21.2 (standalone,   │
                        │  zoneless, signals-first)    │
                        │                               │
                        │  HttpClient                   │
                        │    └─ functional interceptors:│
                        │       errorMapping → auth →   │
                        │       retry (see §4.3)        │
                        └───────────────┬───────────────┘
                                        │ JSON over HTTP
                                        ▼
                        ┌─────────────────────────────┐
                        │        QuotesApi              │
                        │  ASP.NET Core 10 minimal API  │
                        │                                │
                        │  Auth (dual-scheme JWT)        │
                        │  Quotes / Collections / Reports│
                        │  endpoints                     │
                        │                                │
                        │  EF Core repositories (+ one    │
                        │  Dapper read path)              │
                        └───────────────┬───────────────┘
                                        │
                                        ▼
                        ┌─────────────────────────────┐
                        │  SQLite (dev) via EF Core      │
                        │  migrations; a parallel         │
                        │  SQL Server migrations project  │
                        │  exists for prod parity tests   │
                        └─────────────────────────────┘
```

The backend also exports OpenTelemetry traces (OTLP + optional Azure Monitor), structured logs via Serilog (trace-ID-correlated), and has Bicep IaC under `day1/QuotesApi/infra/` for Azure Container Apps.

---

## 3. Backend — `day1/QuotesApi/`

### 3.1 Tech stack

.NET 10 / ASP.NET Core minimal APIs, EF Core 10 (SQLite in dev, a separate SQL Server migrations project for prod-parity testing), Dapper (one hand-optimized read path), JWT bearer auth (`Microsoft.AspNetCore.Authentication.JwtBearer`) with an optional Microsoft Entra ID second scheme, BCrypt for password hashing, Serilog, OpenTelemetry + Azure Monitor, `Microsoft.Extensions.Http.Resilience` (Polly) for the Entra metadata backchannel, Azure Key Vault (optional) for secrets.

### 3.2 Domain model

Rich entities, not anemic DTOs — invariants live on the aggregate, not scattered across endpoint handlers:

- **`Quote`** (`Quote.cs`) — private setters, constructed only via `Quote.Create(author, text, createdByUserId)`, which returns a `QuoteCreationResult` (success/fail, never throws for a normal validation failure). Invariants: author 1–200 chars, text 1–1000 chars (both non-whitespace). No `Rename`/`UpdateText` — the only post-construction state change is `Delete()` (soft delete via `IsDeleted`).
- **`Collection`** (`Collection.cs`) — a second aggregate, not an event-sourced one. `Collection.Create(name, ownerId)`, `Rename`, `AddItem`/`RemoveItem` all throw a `DomainException` on invariant violation (name 3–80 chars, max 50 items, no duplicate quote in a collection). `Items` is `IReadOnlyList` backed by a private field — EF is configured (`UsePropertyAccessMode(PropertyAccessMode.Field)`) to write through the field directly, since there's no public setter to use.
- **`User`** / **`RefreshToken`** — persistence-oriented, back the auth subsystem (§3.4).

### 3.3 API surface

| Method | Route | Auth | Notes |
|---|---|---|---|
| GET | `/api/quotes?page=&size=` | none | `page` defaults to 1, `size` to 10 |
| GET | `/api/quotes/{id}` | none | 404 (empty body) if missing/soft-deleted |
| POST | `/api/quotes` | `can-edit-quotes` policy (`quotes.write` scope) | 201 + the created `Quote`, or 400 `ValidationProblemDetails` |
| DELETE | `/api/quotes/{id}` | authenticated + `can-delete-own-quote` (resource-based) | 204, 404, or 403 |
| GET | `/api/reports/authors` | none | grouped author summary — see §3.6 |
| POST | `/api/auth/register` | none | 200 + token pair (auto-login on signup), 409 if the email is taken, 400 on a bad email/weak password |
| POST | `/api/auth/login` | none | 200 + token pair, or 401 |
| POST | `/api/auth/refresh` | none (bearer refresh token in body) | rotates the refresh token; reuse of an already-rotated token revokes the whole chain (§3.4) |
| POST | `/api/auth/logout` | none | revokes the given refresh token |
| GET | `/api/collections/{id}` | none | denormalized detail read model (§3.5) |
| POST | `/api/collections` | authenticated | |
| POST | `/api/collections/{id}/items` | authenticated | write model — see §3.5 |
| DELETE | `/api/collections/{id}/items/{quoteId}` | authenticated | |
| GET | `/health` | none | ASP.NET Core health checks |
| GET | `/api/meta` | none | `{ environment }` — which live deployment this is (see §11); backs the frontend's dev/prod badge |

**Error shapes are not uniform, on purpose and by observation** — this matters a lot for any client:
- `Results.ValidationProblem(...)` (used on quote/collection creation failures) produces a real `ValidationProblemDetails` JSON body: `{type, title, status, errors: {<key>: [messages]}, traceId}`. For quotes, `errors` always has exactly one key, the literal string `"error"` — never per-field, even though `CreateQuoteRequest` has two fields (see `Extension.cs`: `Results.ValidationProblem(new Dictionary<string,string[]> { ["error"] = [result.Error!] })`).
- `Results.NotFound()` / `Results.Unauthorized()` / `Results.Forbid()` are **genuinely empty-bodied** (`Content-Length: 0`). `AddProblemDetails()` is registered in DI, but there's no `UseStatusCodePages()`/status-code-page middleware wired up, so these plain status-code results are never auto-wrapped into a ProblemDetails JSON body. Verified empirically via curl, not assumed.
- Unhandled exceptions are caught by an explicit `UseExceptionHandler` middleware (`Program.cs`) that writes a minimal `ProblemDetails` (title + status + exception message) as a 500.

### 3.4 Auth

Two JWT schemes can be active simultaneously, selected per-request by a policy scheme (`Extensions.SelectScheme`) that peeks at the token's `iss` claim (unvalidated, just for routing) before either handler actually validates anything:

- **Internal** (`HS256`, symmetric key from config) — always registered. This is what the seeded demo users authenticate against.
- **Entra** (Microsoft Entra ID, OIDC discovery) — only registered if an `Entra` config section is present; otherwise every token is validated against Internal only. Entra's metadata/JWKS fetch goes through a *named* `HttpClient` wrapped in a Polly resilience pipeline (3 retries w/ exponential backoff+jitter, a circuit breaker at 50% failure/30s window, a 10s total timeout) — because a transient blip talking to Entra shouldn't fail every in-flight token validation.

Authorization is **policy-based, not role-based** (`can-edit-quotes` → `RequireClaim("scope", "quotes.write")`; `can-delete-own-quote` → a resource-based `IAuthorizationHandler` since "is this the owner" can only be checked after the specific `Quote` is loaded, not declaratively on the route).

**Refresh-token lifecycle** (`AuthService.cs`) is the most security-sensitive part of the codebase:
- Refresh tokens are opaque 256-bit random values (not JWTs), stored **hashed** (unsalted SHA-256 — deliberate: they're already high-entropy, and an unsalted hash is what makes "look up by exact value" possible).
- Every refresh **rotates**: the old token is marked `ReplacedByTokenHash`, a new one issued.
- **Reuse detection**: if a token that's already been rotated is presented again, that's a signal it leaked — the entire token chain descending from it is revoked immediately, forcing a real login. The specific failure reason (`invalid` / `expired-or-revoked` / `reuse-detected`) is logged server-side only; the client always just gets a uniform 401, so a probing attacker can't distinguish the cases.

Password verification uses BCrypt and is wrapped in its own OpenTelemetry span (`verify-password`) — it's pure CPU work invisible to the automatic ASP.NET Core/EF instrumentation, so without a manual span it wouldn't show up in a trace at all.

**Self-registration** (`AuthService.RegisterAsync`, `AuthController.Register`) is the newest piece of this: `POST /api/auth/register` takes an email/password, rejects an obviously-malformed email or a password under 8 characters (400), rejects a duplicate email (409, with a real `detail` message — the one endpoint in this API that doesn't stay deliberately vague, since there's no reuse-detection-style signal to protect here), and otherwise hashes the password with BCrypt, grants the same `quotes.write` scope the seeded demo user has (there's no invite/approval step — "registered" and "can write quotes" are the same thing in this exercise), and immediately issues a token pair via the same `IssueTokenPairAsync` login uses. `User` stays exactly as anemic as before (§10.1) — no new invariants were added to the entity, just a new caller of the existing token-issuing path.

Two users are seeded at startup: `demo@quotesapi.dev` (has `quotes.write`) and `readonly@quotesapi.dev` (no scopes — exists specifically to exercise the 403 path).

### 3.5 CQRS split (Collections)

"Add an item to a collection" (write) and "view a collection's detail" (read) are deliberately separate paths, not one repository method serving both:

- **Write**: `AddCollectionItemCommand` → `AddCollectionItemCommandHandler` → loads the `Collection` aggregate, calls `AddItem` (invariant-checked), saves. Normalized, validated, goes through the domain model.
- **Read**: `ICollectionQueries`/`CollectionQueries` — bypasses the aggregate entirely, runs one denormalized projection query joining `CollectionItems` to `Quotes` directly, shaped exactly for the detail screen (`CollectionDetailResponse`). No event sourcing — this is the simplest form of CQRS: two paths, one database, no event store.

This split is also what fixed a real N+1: the old single-repository approach fetched a collection's items one quote-lookup at a time; the read model now does one join.

### 3.6 EF Core vs. Dapper (`GET /api/reports/authors`)

This endpoint is the project's dedicated performance-work target:

- **Started** as a genuine N+1 (one query per author) — measured under real k6 load: **p50 ≈ 5.9s, p99 ≈ 6.1s** against a 10,002-row/2,001-author seeded dataset.
- **Fixed** by collapsing it into a single grouped LINQ query (`EfAuthorsReportQuery`) plus a real `IX_Quotes_Author` index (added via EF Core migration, mirrored into the separate SQL Server migrations project so integration tests against SQL Server stay green too). Re-measured: **p50 ≈ 43.2ms (≈137× faster), p99 ≈ 112.6ms (≈54× faster)**.
- **Dapper variant** (`DapperAuthorsReportQuery`) exists alongside it, hand-timed against the same dataset, running the exact SQL EF generates (captured via `LogTo`) directly — same index, same query plan, skipping only EF's materialization pipeline (no expression tree, no entity-shaper delegate, no change tracker). `IAuthorsReportQuery` resolves to the EF version by default in DI; the Dapper version is there as a documented comparison, not because 43ms needed it.

### 3.7 Persistence

EF Core against SQLite in dev (`day1/QuotesApi/quotes.db`, migrations under `Migrations/`, applied automatically at startup via `MigrateAsync()`). A **separate project**, `QuotesApi.Migrations.SqlServer`, holds a parallel migration history for SQL Server — it exists so integration tests that spin up a real SQL Server (via Testcontainers, see §3.8) run against the same schema shape production would, and so a schema change made for SQLite doesn't silently drift from what SQL Server would need.

### 3.8 Testing

Four separate test projects, each with a distinct purpose:

- **`QuotesApi.Tests.Domain`** — pure domain invariant tests (`Collection`, no infrastructure).
- **`Quotes.Tests.Unit`** — unit tests for auth (`AuthServiceTests`, `TokenServiceTests`), the Entra scheme-selection logic (`SelectSchemeTests`), the Entra backchannel's resilience config, and multi-provider `DbContext` switching.
- **`QuotesApi.Tests`** — endpoint-level tests: auth flows end-to-end, authorization policies, cancellation behavior, a deliberately slow fake repository (`SlowCollectionRepository`) for timeout/cancellation scenarios.
- **`Quotes.Tests.Integration`** — full-stack integration tests including a **real SQL Server via Testcontainers** (`SqlServerContainerFixture`) specifically to catch isolation-level and locking bugs that SQLite can't reproduce (this project's transaction-isolation and deadlock exercises were verified here — a real deadlock was forced and its deadlock graph captured via SQL Server's `system_health` XE session).

### 3.9 Observability & resilience

- **Serilog** replaces default logging entirely, config-driven. A custom middleware pushes `TraceId` (the same ID ASP.NET Core's own request `Activity` uses) onto Serilog's `LogContext`, so every log line for a request — including EF Core's own SQL logging — correlates with the same trace ID shown in a tracing backend.
- **OpenTelemetry**: ASP.NET Core, EF Core, and `HttpClient` instrumentation, plus a custom `ActivitySource` (`Telemetry.cs`) for hand-written spans like `verify-password`. Exports via OTLP always, and additionally to Azure Monitor if a connection string is configured (checked under both this app's own config key and the literal env var Azure Container Apps sets).
- **Resilience**: the Entra backchannel `HttpClient` is the only outbound dependency wrapped in Polly (retry + circuit breaker + timeout) — internal DB calls aren't retried at this layer.
- **Secrets**: Azure Key Vault is wired in only if a vault URI is configured, using `DefaultAzureCredential` (Managed Identity in Azure, falls back to local dev credentials) — no secret ever needs to be typed into config directly.

### 3.10 Infra

`day1/QuotesApi/infra/` holds Bicep (`main.bicep`, `resources.bicep`) for deploying to Azure Container Apps; the `.csproj` is configured to build a container image directly (`dotnet publish` container support, Alpine base image).

---

## 4. Frontend — `quotes-ui/`

### 4.1 Tech stack

Angular **21.2**, standalone components (no `NgModule`s anywhere), **zoneless** change detection (`provideZonelessChangeDetection()` — no zone.js in the bundle at all), **signals** as the primary state-management primitive, Vitest (not Karma/Jasmine) as the test runner via `@angular/build`'s Vitest builder.

### 4.2 Components

- **`QuoteList`** — the list+pagination+filter screen. State is `page`/`pageSize`/`authorFilter` signals; a derived `status` computed (`'loading' | 'error' | 'empty' | 'loaded'`) drives the template's `@switch`. Fetches via `toObservable(page/pageSize).pipe(switchMap(...))` — **not** a plain `.subscribe()` in an `effect()` — specifically so a page change cancels whatever fetch was still in flight (see §4.3 for why this mattered more than it looks).
- **`QuoteDetail`** — driven by an `id = input<number|null>()` from the parent. Same `switchMap`-over-`toObservable(id)` pattern, for the same reason: selecting a new quote must cancel the previous detail fetch, or a slow response for quote A can land after a fast response for quote B and clobber it.
- **`CreateQuote`** — built against Angular's **Signal Forms preview API** (`@angular/forms/signals`: `form()`, `schema()`, `required()`/`minLength()`/`maxLength()`/`validate()`, the `FormRoot`/`FormField` directives). Validators mirror the server's exact constraints (author 1–200, text 1–1000) plus a custom trimmed-blank check, because the server trims before validating and the framework's built-in emptiness check doesn't. Full a11y: associated labels, `aria-invalid`/`aria-describedby` gated on touched+invalid, `role="alert"`/`role="status"`, focus moved to the first invalid field on submit (`FieldState.focusBoundControl()`), submit button disabled + relabeled while in flight.
- **`CreateQuoteReactive`** — a deliberate second implementation of the *same* form using traditional `ReactiveFormsModule`/`FormBuilder`/`Validators`, built as a comparison baseline (not wired into the shipping app — see §4.4). Same validators, same server-error mapping, same a11y bar, but everything Signal Forms gives for free (submitting state, focus-on-invalid, mark-touched-on-submit, and — a real gap the first draft missed — the native `maxlength` attribute) had to be hand-rolled.
- **`Login`** / **`Register`** — real forms (plain signals + native `(input)`/`(submit)` handlers, no forms module pulled in for two/three fields), not a one-click "log in as demo user" button. `Register` client-side mirrors `AuthService.RegisterAsync`'s own rules (password ≥ 8 chars, confirm-password match) before ever making a request; both share a `returnUrl` convention with `authGuard` (§ below) so a bounce through either page and back lands the user where they originally meant to go, and both narrow a 401/409 `ApiError` into a specific on-screen message rather than the generic one `api-error.ts` defaults to.

`App` (the root shell, `app.ts`) reads `Auth.isAuthenticated` directly to swap the nav between "Sign in"/"Create account" and a "Log out" button (`Auth.logout()` — clears both tokens locally and best-effort revokes the refresh token via `POST /api/auth/logout`), and separately fetches `GET /api/meta` once at startup to render the dev/prod badge next to the wordmark (see §11).

### 4.3 HTTP layer & interceptor chain

`Quote` (`quote.ts`) is a thin typed wrapper over `HttpClient` — `getQuotes`, `getQuoteById`, `createQuote`. All cross-cutting HTTP concerns live in three **functional interceptors**, composed in a specific, load-bearing order in `app.config.ts`:

```ts
withInterceptors([errorMappingInterceptor, authInterceptor, retryInterceptor])
```

Angular's interceptor array is outer→inner on the request path and **inner→outer on the response path** — the first entry sees the response *last*. That ordering is deliberate, not incidental:

1. **`retryInterceptor`** (innermost — sits directly against the backend) — retries **GET requests only**, and only on a genuinely transient failure (network error / status 0, or a 5xx). Never retries a 4xx (a 404 won't start existing because you asked again, and blindly retrying a 401 risks a confusing repeated-auth loop) and never retries a non-GET (a retried POST could double-create a quote). Exponential backoff (250ms, then 500ms).
2. **`authInterceptor`** (middle) — attaches a bearer token to non-GET, non-`/api/auth/*` requests, reading it synchronously off `Auth.getToken()`. Excludes GET (those endpoints are unauthenticated) and `/api/auth/*` itself (excluding only GET here caused a real self-intercepting deadlock — see §7). **Also does the refresh-on-401 dance now**: if a request that *did* carry a token comes back 401, it calls `Auth.refreshAccessToken()` (coalesced across concurrent callers via a shared, `shareReplay(1)`'d Observable — a burst of 401s must trigger at most one real `/api/auth/refresh` call, since a second concurrent refresh would look like reuse of an already-rotated token to the server's own reuse-detection and revoke the whole chain), retries the original request once with the new token on success, and on failure calls `Auth.logout()` and rethrows the *original* 401 — the caller's `ApiError`-kind `'auth'` handling is unaffected by the silent refresh attempt underneath it.
3. **`errorMappingInterceptor`** (outermost) — sees the *final* result only after retry (and now the interceptor-level refresh-and-retry) has exhausted its attempts against the real status code. Catches whatever the backend produced and rethrows a typed `ApiError` (`api-error.ts`): a `kind` (`'validation' | 'notFound' | 'auth' | 'conflict' | 'network' | 'server'` — `'conflict'` added for `POST /api/auth/register`'s 409), the original status if any, and a friendly `message`. This is what lets `retryInterceptor` make retry/no-retry decisions against the real HTTP status — if the order were reversed, it would be deciding based on an already-mapped `ApiError` that's lost the original status code.

Every component that talks to the backend (`CreateQuote`, `QuoteDetail`, `QuoteList`) consumes the typed `ApiError.kind` — none of them hand-roll their own `HttpErrorResponse`/status-code checks any more; that logic used to be duplicated three times and now lives once, at the interceptor boundary.

### 4.4 Why two create-quote forms exist

`create-quote/` (Signal Forms) is the one actually mounted in `app.html` and shipped. `create-quote-reactive/` is a same-behavior comparison build, kept as a sibling, unmounted, purely so the two approaches could be evaluated side by side on real code rather than in the abstract. See §7 for what that comparison surfaced.

### 4.5 Testing

Vitest + `HttpClientTestingModule`. Two kinds of coverage worth distinguishing:

- **Behavioral specs** (`*.spec.ts` next to each component) — the usual pristine/dirty/touched/submit/error-state coverage, plus real regression tests for the two stale-response races (§7).
- **`quote.contract.spec.ts`** — a *characterization test* (Michael Feathers' sense: pins what the real system actually does) built from response bytes captured via live `curl` against the running backend, not invented fixtures — the real 200 array shape, the real empty-bodied 404, the real `ValidationProblemDetails` 400. It's meant to fail loudly if the backend's actual wire contract ever drifts, independent of whatever the interceptor layer does with the result.

---

## 5. How the two halves fit together

The frontend has zero knowledge of the backend beyond its HTTP contract — no shared types package, no codegen from an OpenAPI spec. Every field name, status code, and error shape used on the Angular side was verified directly against `Extension.cs`/`Quote.cs`/`Auth.cs` (or live `curl` output) rather than assumed, which is also why the error-handling work in §4.3 exists: the two real 4xx shapes this API produces (empty-bodied vs. real `ValidationProblemDetails`) aren't uniform, and a client that assumed otherwise would break on the empty ones.

Local dev: backend on `http://localhost:5062` (`dotnet run` from `day1/QuotesApi/`), frontend on `http://localhost:4200` (`ng serve` from `quotes-ui/`) — no proxy config; the frontend's relative `/api/...` calls assume they're being run against a backend reachable at the same origin or proxied appropriately in whatever's serving them.

---

## 6. Request tracing: every real call, file-to-file

Grep confirms the shipping frontend only ever calls **four** real endpoints (everything else in §3.3 — collections, reports, delete, refresh, logout — has no caller anywhere in `quotes-ui/`, `CreateQuoteReactive` included but unmounted). What follows is the literal hop-by-hop path for each one, file and line, request side then response side. All three data calls pass through the same interceptor chain from `app.config.ts`:

```ts
withInterceptors([errorMappingInterceptor, authInterceptor, retryInterceptor])
```

— outermost to innermost, so **on the way out** a request hits `errorMappingInterceptor` first (it does nothing to requests) → `authInterceptor` → `retryInterceptor` → the real network call; **on the way back**, the response/error unwinds in the reverse order: `retryInterceptor` sees it first (raw), then `authInterceptor` (passthrough), then `errorMappingInterceptor` last (maps the final error, if any).

### 6.1 `GET /api/quotes?page=&size=` — the list screen loading

**Frontend, request side:**
1. `quote-list/quote-list.ts:68` — constructor pipes `toObservable(computed(() => ({page, pageSize})))` into `switchMap`.
2. `quote-list/quote-list.ts:74` — inside the `switchMap` callback: `this.quoteService.getQuotes(page, pageSize)`.
3. `quote.ts:33` — `Quote.getQuotes()`: `this.http.get<QuoteDto[]>('/api/quotes?page=${page}&size=${size}')`.
4. `error-mapping-interceptor.ts:18` — passthrough (`next(req)`) on the request side, its work is all on the response side.
5. `auth-interceptor.ts:19` — `req.method === 'GET'` → true → `return next(req)` immediately, no token fetch, no `/api/auth/login` round trip.
6. `retry-interceptor.ts:35-55` — `req.method === 'GET'` → wraps `next(req)` in RxJS `retry({count: 2, delay: ...})`, armed to retry only on a transient failure.
7. Angular's `HttpBackend` opens the real XHR/fetch to `http://localhost:5062/api/quotes?page=1&size=10`.

**Backend:**
8. Kestrel accepts the connection → `Program.cs:91-97` pushes `TraceId` onto Serilog's `LogContext` → `Program.cs:99` `UseSerilogRequestLogging()` → `Program.cs:102-116` exception-handler middleware wraps everything downstream → `Program.cs:146-147` `UseAuthentication()`/`UseAuthorization()` run but this route has no `RequireAuthorization()`, so they're a no-op gate here → routing dispatches into the endpoint.
9. `Extension.cs:223-228` — `group.MapGet("/", ...)` inside the `/api/quotes` group (`Extension.cs:220`). Binds `page`/`size` from the query string, defaults `p=1`/`s=10` if absent or ≤0.
10. `Extension.cs:227` — calls `IQuoteRepository.GetPagedAsync(p, s, ct)`, DI-resolved to `QuoteRepository` (`Infrastructure.cs:76`).
11. `Infrastructure.cs:78-86` — `QuoteRepository.GetPagedAsync`: logs, then `db.Quotes.Where(!IsDeleted).Skip((page-1)*size).Take(size).ToListAsync(ct)` — EF Core translates this to a real `SELECT ... LIMIT ... OFFSET ...` against `quotes.db` (SQLite).
12. `Extension.cs:227` — wraps the resulting `List<Quote>` in `Results.Ok(...)`; System.Text.Json serializes it camelCase (`id`, `author`, `text`, `isDeleted`, `createdByUserId`).

**Response side, unwinding back through the interceptors:**
13. `retry-interceptor.ts:39` — the `next(req)` observable emits a success, `retry()` does nothing.
14. `auth-interceptor.ts` — GET path never entered the `switchMap` branch, nothing to unwind.
15. `error-mapping-interceptor.ts:18` — `catchError` never fires on a success, passes the response through untouched.
16. `quote-list/quote-list.ts:97-104` — the outer `.subscribe()` receives the `FetchResult`, calls `this.quotes.set(result.quotes)`, `this.loading.set(false)` → the `status` computed flips to `'loaded'` → the template re-renders.

### 6.2 `GET /api/quotes/{id}` — clicking a quote to see its detail

**Frontend:**
1. `quote-list/quote-list.html` — a click on a list item calls `selectQuote(id)` → `quote-list.ts:133` sets `this.selectedId`.
2. `quote-detail/quote-detail.ts` receives it via its `id = input<number|null>()`, bound from the parent template.
3. `quote-detail/quote-detail.ts:37-39` — `toObservable(this.id).pipe(switchMap((id) => {...`.
4. `quote-detail/quote-detail.ts:49` — `this.quoteService.getQuoteById(id)`.
5. `quote.ts:40` — `Quote.getQuoteById()`: `this.http.get<QuoteDto>('/api/quotes/${id}')`.
6. Same interceptor pass-through as §6.1 steps 4-6 (GET, no auth needed, retry armed).

**Backend:**
7. `Extension.cs:231-235` — `group.MapGet("/{id:int}", ...)`.
8. `Extension.cs:233` — `IQuoteRepository.GetByIdAsync(id, ct)` → `Infrastructure.cs:88-93`: `db.Quotes.FindAsync([id], ct)`, returns the entity only if `IsDeleted` is false.
9. `Extension.cs:234` — found → `Results.Ok(quote)` (200); not found or soft-deleted → `Results.NotFound()` — **a genuinely empty 404 body**, per §3.3.

**Response side:**
- **Success**: unwinds untouched through all three interceptors, same as §6.1 step 13-15 → `quote-detail.ts:49-65` `map()` wraps it `{kind:'success', quote}` → the final `.subscribe()` at `quote-detail.ts:70-79` sets `this.data`.
- **404**: `retry-interceptor.ts:19` `isTransientFailure()` returns `false` for a 4xx → `retry-interceptor.ts:49` rethrows immediately, no retry attempted → `error-mapping-interceptor.ts:18` catches it, `api-error.ts:82-83` maps status 404 → `new ApiError('notFound', ...)` → back at `quote-detail.ts:56-65`, the `catchError` checks `err.kind === 'notFound'` and sets a friendly `"Quote {id} was not found."` message instead of ever calling `.next()` with quote data.
- Note: `switchMap` at step 3 means clicking a *different* quote before this resolves cancels this entire chain outright (§4.2, §7) — nothing above ever reaches the subscriber for an abandoned selection.

### 6.3 `POST /api/quotes` — submitting the create-quote form

This is the only flow that also triggers a **second, nested** HTTP call (the login), because it's the first non-GET request the app makes.

**Frontend, request side:**
1. `create-quote/create-quote.ts:77-101` — Signal Forms' `form(...)`'s `submission.action`, invoked by `<form [formRoot]>`'s native submit (`create-quote.html:4`) once client-side validation (the `schema` at `create-quote.ts:29-47`) passes.
2. `create-quote.ts:85` — `await firstValueFrom(this.quoteService.createQuote(author, text))`.
3. `quote.ts:53` — `Quote.createQuote()`: `this.http.post<QuoteDto>('/api/quotes', {author, text})`.
4. `error-mapping-interceptor.ts:18` — passthrough on the way out.
5. `auth-interceptor.ts:19` — `req.method === 'GET'` is false and the URL isn't `/api/auth/*`, so **this branch is taken**: `auth-interceptor.ts:23-26` — `inject(Auth).getToken().pipe(switchMap((token) => next(req.clone({setHeaders:{Authorization: \`Bearer ${token}\`}}))))`.
6. `auth.ts:23-24` — first call ever: `this.token$` is `null`, so it's populated: `this.http.post<LoginResponseDto>('/api/auth/login', {email:'demo@quotesapi.dev', password:'correct-horse-battery-staple'})`. **This is a brand-new HTTP request that re-enters the exact same interceptor chain from the top.**
   - `error-mapping-interceptor.ts:18` — passthrough.
   - `auth-interceptor.ts:19` — `req.url.includes('/api/auth/')` is true this time → passthrough, **not** re-entered recursively (this exact guard is what fixes the self-deadlock described in §7).
   - `retry-interceptor.ts:35` — method is `POST`, not `GET` → passthrough, no retry.
   - Real request: `POST http://localhost:5062/api/auth/login`.
7. **Backend, for the login call**: `Extension.cs:298-302` — `group.MapPost("/login", ...)` inside `/api/auth` (`Extension.cs:295`) → `IAuthService.LoginAsync` → `Auth.cs:148-178` `AuthService.LoginAsync`: `Infrastructure.cs:327-328` `UserRepository.GetByEmailAsync` → `Auth.cs:166-169` BCrypt password verification (wrapped in the `verify-password` OTel span, `Auth.cs:166`) → on match, `Auth.cs:225-239` `IssueTokenPairAsync`: `Auth.cs:227` `TokenService.CreateAccessToken` (signs a JWT, `Auth.cs:74-101`) + `Auth.cs:228` `CreateRefreshToken` (`Auth.cs:105`, random bytes) → `Infrastructure.cs:340-341` `RefreshTokenRepository.AddAsync` persists the new refresh token's hash → `Extension.cs:301` `Results.Ok(response)` → 200 + `{access_token, refresh_token, expires_in}`.
8. Response unwinds back through the nested interceptor pass (nothing to map, it's a success) → `auth.ts:29-32` `.pipe(map(res => res.access_token), shareReplay(1))` → the token string is emitted and cached in `this.token$` for every future call this session.
9. Back in `auth-interceptor.ts:24-26`'s outer `switchMap`, the token now in hand: `next(req.clone(...))` forwards the **original** create-quote request, now carrying `Authorization: Bearer <token>`, to `retry-interceptor.ts`.
10. `retry-interceptor.ts:35` — method is `POST` → passthrough, no retry logic (a non-idempotent write is never retried here — see §3.3/§4.3).
11. Real request: `POST http://localhost:5062/api/quotes` with the JWT attached.

**Backend, for the create-quote call:**
12. `Program.cs:146-147` — `UseAuthentication()` validates the bearer JWT against whichever scheme `Extensions.SelectScheme` (`Extension.cs:159-185`) routes it to (Internal, here — no Entra section configured) → `UseAuthorization()` evaluates the `can-edit-quotes` policy (`Extension.cs:116`: `RequireClaim("scope","quotes.write")`) against the token's claims.
13. `Extension.cs:238-251` — `group.MapPost("/", ...)`, gated `.RequireAuthorization("can-edit-quotes")` (`Extension.cs:251`).
14. `Extension.cs:240` — pulls the caller's user id from the JWT's `sub` claim.
15. `Extension.cs:243` — `TextNormalizer.cs:13` trims both fields, then `Quote.cs:35-50` `Quote.Create(author, text, userId)` checks the length invariants and returns a `QuoteCreationResult`.
16. **Invalid** → `Extension.cs:246` `Results.ValidationProblem(new Dictionary<string,string[]>{["error"]=[result.Error!]})` — 400, real `ValidationProblemDetails`.
    **Valid** → `Extension.cs:249` `IQuoteRepository.CreateAsync` → `Infrastructure.cs:106-112`: `db.Quotes.Add(quote); await db.SaveChangesAsync(ct);` — a real `INSERT` against SQLite, EF populates the generated `Id` back onto the entity → `Extension.cs:250` `Results.Created($"/api/quotes/{created.Id}", created)` — 201 + `Location` header + the created `Quote` as JSON.

**Response side:**
- `retry-interceptor.ts:35` — POST, passthrough regardless of outcome.
- `auth-interceptor.ts` — nothing on the response side; the `switchMap` at step 5 only shaped the request.
- **Success (201)**: `error-mapping-interceptor.ts:18` passthrough → `create-quote.ts:85-92`: `firstValueFrom` resolves, `field().reset({...EMPTY_MODEL})` clears the form, `this.submitSuccess.set(true)`.
- **400**: `error-mapping-interceptor.ts:18` catches it → `api-error.ts:90-91` maps status 400 → `extractValidationMessage` (`api-error.ts:56-60`) pulls `body.errors.error[0]` → `new ApiError('validation', <that message>, 400)` → `create-quote.ts:93-94` `catch (err) { this.submitError.set(this.mapSubmitError(err)); }` → `create-quote.ts:130-149` narrows `ApiError.kind` down to the form's `SubmitError` shape → `create-quote.html:37-39` renders it in the `role="alert"` banner.

### 6.4 What never gets called

`DELETE /api/quotes/{id}` and `GET /api/reports/authors` are real, tested backend endpoints (§3.3, §3.6) with **no frontend caller at all** — there's no delete button and no reports screen. `POST /api/auth/register`/`login`/`refresh`/`logout` and the collections read/write routes, by contrast, are now all reachable from the UI (`Register`/`Login`, `authInterceptor`'s refresh-on-401, `App.logout()`, and `CollectionDetail`/`CollectionStore` respectively) — this whole subsection used to list all of those as dead code; it doesn't any more.

## 7. Real bugs found and fixed along the way

Worth keeping because each one is a genuine lesson, not a hypothetical:

- **Auth-interceptor self-deadlock**: the interceptor originally excluded only `GET` requests from token attachment. `Auth.getToken()` itself calls `POST /api/auth/login` through the same `HttpClient`, so the interceptor was recursively intercepting its own token-fetch request — a real circular subscription deadlock (the create-quote form got stuck on "Creating…" forever with zero network requests ever firing). Fixed by also excluding `/api/auth/*`.
- **Reactive Forms' missing native `maxlength`**: Signal Forms' `FormField` directive sets the native HTML `maxlength` attribute as a side effect of the `maxLength()` validator, so a real browser physically truncates typing past the limit. The hand-built Reactive Forms comparison version didn't do this — `Validators.maxLength()` is JS-only — so typing 250 characters into its author field left all 250 in the DOM, only flagged invalid on blur. Confirmed live (Signal Forms: DOM value clamped to 200; Reactive Forms: stayed at 250) before fixing it with an explicit `[attr.maxlength]` binding.
- **Stale-response race in `QuoteList`** (found *because of* the retry feature, not despite it): the list's fetch effect subscribed directly with no cancellation of a superseded request — the same class of bug `QuoteDetail` had already fixed with `switchMap`, never backported. Once `retryInterceptor` could keep a transiently-failing GET in flight for 750ms+, this became trivially reproducible: request page 1 (stuck retrying), click to page 2 before it resolves, and the stale page-1 response lands after page-2's and silently overwrites it — pager says "Page 2," content shows page 1. Fixed with the same `switchMap` pattern `QuoteDetail` already used; verified live (via Playwright route interception against the real running app, not just a mocked unit test) that only the expected number of requests fire and the stale one never reaches the DOM.

---

## 8. Running it locally

```bash
# Backend
cd day1/QuotesApi
dotnet run --urls http://localhost:5062

# Frontend (separate terminal)
cd quotes-ui
npm install
npx ng serve --port 4200
```

Then open `http://localhost:4200`. Seeded demo credentials: `demo@quotesapi.dev` / `correct-horse-battery-staple` (has `quotes.write`; the other seeded user, `readonly@quotesapi.dev`, deliberately has no scopes to exercise the 403 path) — or hit `/register` and create your own.

Backend tests: `dotnet test` from `day1/` (the integration suite spins up a real SQL Server container — Docker must be running). Frontend tests: `npx ng test` from `quotes-ui/`.

**Single-container run** (what §11's live deployments actually run — closer to production than the two-terminal setup above): `docker build -t quotesapi . && docker run -p 8080:8080 -e Jwt__SigningKey="$(openssl rand -base64 32)" quotesapi`, then open `http://localhost:8080` — the Angular build is baked into `wwwroot/` inside the image (see the root `Dockerfile`), so there's one process and one origin, no `ng serve`/proxy involved.

---

## 9. Notable things this project deliberately does *not* do

- No event sourcing for Collections' CQRS split — two query paths over one database, nothing more.
- No shared frontend/backend type generation — the contract is enforced by hand-verification and the characterization test, not tooling.
- No retry on non-idempotent requests, ever — a POST failing transiently surfaces as a real failure rather than risking a duplicate write.
- No email verification or password-reset flow — `POST /api/auth/register` signs a new account in immediately; there's no "confirm your email" step and no "forgot password" path.
- No persistent storage on the live deployments (§11) — the free host's filesystem is ephemeral, so SQLite (registered users, quotes, everything) resets on redeploy/idle spin-down. Fine for demoing the auth flow, not a real user store.

---

## 10. Entities, functional requirements, non-functional requirements

### 10.1 Entities

Four persisted entities, all EF Core-mapped in `Infrastructure.cs`'s `AppDbContext.OnModelCreating` (§3.7):

| Entity | File | Key fields | Invariants / notes |
|---|---|---|---|
| **`Quote`** | `Quote.cs` | `Id`, `Author` (≤200 chars), `Text` (≤1000 chars), `IsDeleted`, `CreatedByUserId` | Private setters; only constructible via `Quote.Create()`, which enforces both length invariants and returns a result type rather than throwing. No update method — the only post-creation mutation is `Delete()` (soft delete). `HasIndex(q => q.Author)` for the reports query. |
| **`Collection`** | `Collection.cs` | `Id`, `Name` (3–80 chars), `OwnerId`, `Items` (`IReadOnlyList<CollectionItem>`, backed by a private `List<CollectionItem>` field) | A second, independent aggregate — not a child of `Quote`. `AddItem`/`RemoveItem`/`Rename` all throw `DomainException` on violation: max 50 items, no duplicate quote per collection, name length. `CollectionItem` (`QuoteId`, `AddedAt`) is an EF *owned* type stored in its own `CollectionItems` table (`OwnsMany`), not a separate aggregate — it has no identity of its own outside its parent `Collection`. |
| **`User`** | `User.cs` | `Id`, `Email` (unique), `PasswordHash` (BCrypt), `Scopes` (space-separated, e.g. `"quotes.write"`) | Deliberately anemic — plain public setters, no factory, no invariants. The file's own comment says why: this exercise was about wiring auth, not about modeling `User` as a rich aggregate the way `Quote`/`Collection` are. |
| **`RefreshToken`** | `RefreshToken.cs` | `Id`, `TokenHash` (unique, SHA-256 of the raw token — the raw value is never persisted), `UserId`, `ExpiresAt`, `RevokedAt`, `ReplacedByTokenHash` | `IsActive(now)` = not revoked and not expired. `MarkReplacedBy()` revokes the current token at the same instant it records its successor's hash — this pairing is what makes reuse detection possible (§3.4). |

**Relationships**: `Quote.CreatedByUserId` and `RefreshToken.UserId` are real EF-enforced foreign keys to `User`. `Collection.OwnerId` is a plain `int`, not an EF-configured foreign key (no `HasOne<User>()` for it in `OnModelCreating`) — ownership is checked in application code (`can-delete-own-quote`'s pattern), not the database schema. `CollectionItem.QuoteId` is likewise a plain `int`, not a foreign key to `Quotes` — nothing in the schema stops a `CollectionItem` from pointing at a quote id that doesn't exist; that's an implicit trust boundary worth knowing about if this ever grew a delete-quote-while-collected scenario.

On the frontend, there is no separate "entity" layer — `QuoteDto` (`quote.ts`) is a plain wire-shape interface mirroring `Quote`'s JSON serialization exactly (including `isDeleted`/`createdByUserId`, which today's UI never reads but still types), and `ApiError` (`api-error.ts`) is the one client-side type that could be called a domain concept of its own, since it doesn't mirror anything the backend sends directly — it's synthesized entirely at the interceptor boundary (§4.3).

### 10.2 Functional requirements

Grouped by area; every line corresponds to a real, working endpoint or component cited elsewhere in this document — nothing here is aspirational.

**Authentication & authorization**
- A new user can self-register with email + password and is signed in immediately, same token pair as login (`POST /api/auth/register`, §3.4), consumed by `Register` (§4.2). A taken email or a too-weak password is rejected (409 / 400 respectively) with a message the form shows verbatim.
- A user can log in with email + password and receive a short-lived access token and a longer-lived refresh token (`POST /api/auth/login`, §3.4), consumed by `Login` (§4.2).
- A refresh token can be exchanged for a new token pair; presenting one that's already been used revokes its entire lineage (`POST /api/auth/refresh`, §3.4) — now called automatically by `authInterceptor` on a 401 from an authenticated request (§4.3), not just by hand.
- A refresh token (and by extension a logged-in session) can be explicitly revoked (`POST /api/auth/logout`) — called by `Auth.logout()` (§4.2) whenever the "Log out" nav button is used.
- Write operations are gated by scope (`quotes.write` for creating a quote) or plain authentication (collections), enforced via ASP.NET Core policies, not ad hoc checks in each handler.
- A user can delete only a quote they themselves created — a resource-based check, not a route-level one (`DELETE /api/quotes/{id}`, §3.4) — not currently called by the frontend.

**Quotes**
- List quotes, paginated (`GET /api/quotes?page=&size=`), consumed by `QuoteList` (§4.2).
- View a single quote by id (`GET /api/quotes/{id}`), consumed by `QuoteDetail`.
- Create a quote, subject to the same author/text length invariants on both client (`create-quote.ts`'s schema) and server (`Quote.Create()`), with server-side trimming applied before validation on both.
- Filter the currently-loaded page of quotes by author, client-side only (`QuoteList.filteredQuotes`) — does not issue a new request per keystroke.
- Soft-delete a quote (`DELETE /api/quotes/{id}`) — implemented backend-side, no frontend UI for it yet.

**Collections**
- Create a named collection owned by a user (`POST /api/collections`).
- Add a quote to a collection, subject to the aggregate's own invariants (max 50 items, no duplicates) (`POST /api/collections/{id}/items`, §3.5's write model).
- Remove a quote from a collection (`DELETE /api/collections/{id}/items/{quoteId}`).
- View a collection's detail — id, name, owner, and every item's quote text/author/added-at — as one denormalized read (`GET /api/collections/{id}`, §3.5's read model). No frontend UI consumes this yet.

**Reporting**
- View, per author, how many quotes they have and their most recent quote's text (`GET /api/reports/authors`, §3.6). No frontend UI consumes this yet; it exists specifically as the target of this project's N+1/EF-vs-Dapper performance work.

**Frontend-specific UX requirements**
- Every server-side 4xx must surface as a specific, human-readable message, not a generic failure — distinguishing "your input was invalid" from "you're not allowed to do that" from "we couldn't reach the server" (§4.3's `ApiError.kind`).
- The create-quote form must be fully keyboard-operable and screen-reader-accessible: associated labels, `aria-invalid`/`aria-describedby` on errors, focus moved to the first invalid field on a failed submit (§4.2) — verified with a real axe-core audit (zero violations) and manual keyboard-only passes, not just written to spec.
- Selecting a different quote, or changing page, while a previous fetch is still in flight must never let the stale response win (§4.2, §7's stale-response-race bugs).

### 10.3 Non-functional requirements

Each of these is backed by a specific mechanism actually in the code, not a general aspiration:

- **Performance** — the authors-report endpoint went from a real, load-tested p50 of ~5.9s / p99 ~6.1s (N+1, no index) to p50 ~43.2ms / p99 ~112.6ms (single grouped query + `IX_Quotes_Author`), measured with k6 against a 10,002-row seeded dataset (§3.6). A Dapper variant of the same query exists as a documented comparison point for when EF's materialization overhead would matter, even though it doesn't here.
- **Security** — passwords hashed with BCrypt (adaptive work factor); refresh tokens stored only as SHA-256 hashes, never raw; refresh-token rotation with reuse detection that revokes an entire compromised chain (§3.4); policy-based (not role-based) authorization so "who can write quotes" is a single named rule, not hardcoded per endpoint; secrets sourced from Azure Key Vault via Managed Identity when configured, never committed to config (§3.9).
- **Resilience** — the one real outbound network dependency (the Entra JWKS/metadata backchannel) is wrapped in a Polly pipeline: 3 retries with exponential backoff + jitter, a circuit breaker at 50% failure over a 30s window, a 10s hard timeout (§3.4/§3.9). On the frontend, idempotent GETs get the equivalent treatment — 3 attempts with exponential backoff, but only on genuinely transient failures (network error or 5xx), never on a 4xx or a non-idempotent write (§4.3).
- **Observability** — every request's logs (including EF Core's own SQL logging) and its OpenTelemetry trace share the same `TraceId`, correlated via Serilog's `LogContext` (§3.9); a hand-written span (`verify-password`) makes otherwise-invisible CPU-bound work (BCrypt verification) visible in a trace; traces export via OTLP always and additionally to Azure Monitor when configured.
- **Accessibility** — the create-quote form meets WCAG-style expectations concretely, not just in principle: labeled inputs, `aria-invalid`/`aria-describedby` wired only when actually relevant, `role="alert"`/`role="status"` on dynamic messages, focus management on failed submit, a zero-violation axe-core audit, and a full manual keyboard-only pass (§4.2).
- **Testability** — the backend is split into four test projects by concern (pure domain, unit, endpoint-level, full-stack integration against a real containerized SQL Server) specifically so a schema or locking bug that SQLite can't reproduce still gets caught (§3.8). The frontend has a dedicated characterization-test file (`quote.contract.spec.ts`) whose fixtures are real bytes captured from the live backend, not invented ones (§4.5) — it exists to fail loudly if the real API contract ever silently drifts.
- **Maintainability** — invariants live once, on the aggregate (`Quote`, `Collection`), not copy-pasted across every handler that touches them; Collections' write and read paths are fully separated (§3.5) so the read side can be reshaped for a screen without touching the invariant-checked write side; error-mapping logic that used to be duplicated in three frontend components now lives once, at the interceptor boundary (§4.3).
- **Deployability** — the backend builds as a container image directly from the `.csproj` (Alpine base), with Bicep IaC (`infra/main.bicep`, `infra/resources.bicep`) targeting Azure Container Apps (§3.10) and Terraform (`infra-terraform/`) targeting local Docker as a free stand-in for the same shape; Key Vault and Azure Monitor wiring are both purely additive/optional, so the same code runs unmodified with zero Azure resources provisioned for local dev. A third path — a plain multi-stage `Dockerfile` at the repo root plus a Render Blueprint (`render.yaml`) — is what the two actually-live URLs in §11 run on, chosen specifically because it needs no cloud subscription at all.

---

## 11. Live deployments (dev & prod)

Two Render web services, both built from the same root `Dockerfile` and the same `main` branch, defined declaratively in `render.yaml` (Render's Blueprint format — the free-tier analog to the Bicep/Terraform IaC in §3.10, for a host that doesn't need the Azure subscription this project otherwise targets):

- **`quotesapi-dev`** and **`quotesapi-prod`** — identical images, distinguished only by config: each gets its own `Jwt__SigningKey` (set as a Render secret, never committed — same rule appsettings.Production.json's own comment already states for this key) and its own `Environment__Label` (`dev`/`prod`), which `GET /api/meta` (`Program.cs`) echoes back and `App` (`app.ts`) renders as a small badge next to the wordmark — so the same frontend build visibly identifies which live deployment it's talking to.

**The Dockerfile is a 3-stage build**: an `node:22-alpine` stage runs `ng build --configuration production` for `quotes-ui/`; a `mcr.microsoft.com/dotnet/sdk:10.0-alpine` stage restores and publishes `QuotesApi.csproj`; the final `mcr.microsoft.com/dotnet/aspnet:10.0-alpine` stage copies the publish output plus the Angular build's `browser/` output straight into `wwwroot/` — one image, one process, same same-origin design `Program.cs`'s `UseStaticFiles()`/`MapFallbackToFile("index.html")` was already built around (§4.3's "no CORS config anywhere" observation is why this was the natural shape, not two separate services). A small `entrypoint.sh` resolves the actual bind port from `$PORT` at container start (`http://0.0.0.0:${PORT:-8080}`), since a host-injected port can't be baked in at build time.

**Known limitation, stated plainly**: Render's free plan filesystem is ephemeral, so the SQLite file under `App_Data/` (§3.7) does not survive a redeploy or an idle spin-down — registered users and quotes on both live URLs are wiped periodically. That's an accepted tradeoff for a $0/month deploy, not an oversight; moving either environment onto a persistent disk or a managed Postgres add-on would fix it, at the cost of no longer being free (§9's last bullet).
