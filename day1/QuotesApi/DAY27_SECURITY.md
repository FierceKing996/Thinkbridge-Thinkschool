# Day 27 — Security pass

## STRIDE-lite threat model

Scoped to the capstone slice from `DAY22_CAPSTONE.md` — the six bounded contexts
of QuotesApi. One row per component, the threat that actually applies to it (not
every STRIDE category forced onto every component), and where the mitigation
lives today vs. what's a real gap.

| Component | Threat | Mitigation today | Gap / action |
|---|---|---|---|
| Auth (login) | **Spoofing** — credential stuffing against `POST /api/auth/login` | BCrypt password hashing; JWT with `ClockSkew = TimeSpan.Zero` | No rate limiting on login attempts — a real gap. Next step: `Microsoft.AspNetCore.RateLimiting` per-IP on `/api/auth/*`. |
| Auth (Entra backchannel) | **Denial of Service** — a hung/slow Entra endpoint exhausting connections | Day 22's bulkhead (`AddConcurrencyLimiter`) + timeout + circuit breaker | Covered — see `DAY22_RESILIENCE.md`. |
| Quotes/Collections CRUD | **Tampering** — editing/deleting another user's data | `SameOwnerAuthorizationHandler` (`can-delete-own-quote`); scope-based `can-edit-quotes` policy | Covered by policy, but worth a periodic authz test sweep — see `AuthorizationPolicyTests.cs`. |
| Quotes/Collections CRUD | **Denial of Service** — oversized request bodies | Day 27's Kestrel `MaxRequestBodySize = 64KB` (see below) | Fixed this exercise. |
| Global exception handler | **Information Disclosure** — internal exception text (stack detail, ADO connection strings in DB exceptions) returned to any caller on *any* 500 | Fixed this exercise (see below) | Was a real, pre-existing gap — every unhandled exception's raw `.Message` was echoed to the client regardless of type. |
| Outbox / RabbitMQ messaging | **Tampering / Repudiation** — a forged or replayed event on the bus | `MessageId`-based idempotent dedupe (`ProcessedMessage`) prevents duplicate *processing*, but nothing authenticates that a message actually originated from this app's own relay | Real gap for a production deployment: RabbitMQ's default local dev credentials (guest/guest) are fine for `docker-compose.yml` only: production would need TLS + real credentials + ideally message signing if multiple untrusted publishers ever exist on the same exchange. |
| HybridCache (authors report) | **Denial of Service** — cache stampede on a cold key | Day 21: `HybridCache.GetOrCreateAsync` coalesces concurrent misses into one DB call | Covered — see `DAY21_HYBRIDCACHE.md`. |
| Background jobs (CSV export) | **Elevation of Privilege / Information Disclosure** — export job files sit in `App_Data/exports/`, one file per Guid job id | `wwwroot`/static files middleware doesn't serve `App_Data/` (out of the web root); `Download` action checks the job's own status record, not just file existence | Job ids are GUIDs (unguessable) but there's no ownership check — anyone with a valid job id (even from a different user) can download that CSV. Real gap: the exercise didn't scope exports to `CreatedByUserId`; noted here rather than silently left as "fine". |
| Vault / secrets | **Spoofing** — a compromised RoleId+SecretId acting as the app | AppRole policy scoped to exactly `secret/data/quotesapi`, `token_ttl=1h` | See `DAY25_IDENTITY.md`'s honest gap: this isn't credential-free the way Managed Identity is. |

Two real findings came out of *testing* this exercise's own changes, not just
writing the table — both fixed:

### Finding 1: oversized requests returned 500, not 413

Adding the Kestrel `MaxRequestBodySize` limit (below) surfaced that
`BadHttpRequestException` (which Kestrel throws when a body exceeds the limit,
carrying its own correct `StatusCode`) was being discarded by the global
exception handler in favor of a hardcoded 500 — misrepresenting a client error
(oversized payload) as a server failure.

### Finding 2: every unhandled exception leaked its raw message to the client

The same handler put `exception.Message` into the response `Detail` field
**unconditionally**, for every exception type, including genuine 500s. A future
`SqlException` (containing a connection string fragment) or similar would have
been echoed straight back to whoever sent the request that triggered it.

```diff
     exceptionHandlerApp.Run(async context =>
     {
-        var exceptionHandlerPathFeature = context.Features.Get<IExceptionHandlerPathFeature>();
-        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
+        var exception = context.Features.Get<IExceptionHandlerPathFeature>()?.Error;
+        var statusCode = exception is BadHttpRequestException badRequest
+            ? badRequest.StatusCode
+            : StatusCodes.Status500InternalServerError;
+        var isClientError = statusCode is >= 400 and < 500;
+        context.Response.StatusCode = statusCode;

         await context.Response.WriteAsJsonAsync(new ProblemDetails
         {
-            Title = "An unexpected error occurred.",
-            Status = context.Response.StatusCode,
-            Detail = exceptionHandlerPathFeature?.Error.Message
+            Title = isClientError ? exception!.Message : "An unexpected error occurred.",
+            Status = statusCode,
+            Detail = isClientError ? exception!.Message : null
         });
     });
```

**Proof, live:**

```bash
$ curl -X POST http://localhost:5062/api/auth/login -H "Content-Type: application/json" --data-binary @bigbody.txt
# before:
{"title":"An unexpected error occurred.","status":500,"detail":"Request body too large. The max request body size is 65536 bytes."}
# after:
{"title":"Request body too large. The max request body size is 65536 bytes.","status":413,"detail":"Request body too large. The max request body size is 65536 bytes."}
```

Correct status code now, and a genuine 500 (any exception that isn't a
`BadHttpRequestException`) no longer includes `Detail` at all.

## Private endpoints (Docker network isolation instead of Azure Private Endpoint)

Azure Private Endpoint needs an Azure VNet. The free, self-hostable analog is
the same idea one layer down: put the data tier on a network with **no path in
from outside it**, so only the application itself can reach it — not "protected
by a password", genuinely unreachable.

`docker-compose.yml`:

```yaml
networks:
  data-tier:

services:
  rabbitmq:
    networks: [data-tier]
    ports: ["5672:5672", "15672:15672"]   # published for local dev only - see below
  redis:
    networks: [data-tier]
    ports: ["6379:6379"]
  vault:
    networks: [data-tier]
    ports: ["8200:8200"]
```

**Honest caveat:** these exercises run QuotesApi via `dotnet run` on the host,
not as its own container in this compose file, so the raw protocol ports
(5672, 6379, 8200) stay published to the host for local development to work at
all. A real deployment does not have this exception: the app itself joins
`data-tier` as a container, every one of those port mappings is deleted
entirely, and RabbitMQ/Redis/Vault become reachable *only* by service name from
inside the network — identical in effect to what a Private Endpoint achieves
inside a VNet, just drawn with a Docker network boundary instead of an Azure
one. The `ContainerRepository`/`ContainerImageTag` properties already in
`QuotesApi.csproj` (`dotnet publish /t:PublishContainer`) are what would produce
that app container.

## OpenAPI hardening

**Auth** — the generated spec previously had no security scheme at all, so any
tool reading `/openapi/v1.json` (Swagger UI, a client generator, an automated
scanner) would think every endpoint was anonymous, even though `[Authorize]`
already rejected unauthenticated requests correctly. Fixed with a document
transformer (`Extensions/BearerSecuritySchemeTransformer.cs`):

```bash
$ curl http://localhost:5062/openapi/v1.json | jq '.components.securitySchemes, .security'
{
  "Bearer": { "type": "http", "scheme": "bearer", "bearerFormat": "JWT", ... }
}
[{ "Bearer": [] }]
```

**Versioning** — every controller now declares `[ApiVersion("1.0")]` explicitly
via header/query-string versioning (not a URL segment change, so no existing
route — or the Angular UI's existing calls — breaks):

```bash
$ curl -sD - -o /dev/null http://localhost:5062/api/reports/authors | grep -i api-supported-versions
api-supported-versions: 1.0
```

**Input limits** — `builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 64 * 1024)`,
proven above via the 413 response.

## OWASP ZAP baseline

ZAP itself needs no Azure substitution — it's already free and open-source. Run
against a locally running instance:

```bash
docker run --rm -t ghcr.io/zaproxy/zaproxy:stable zap-baseline.py \
  -t http://host.docker.internal:5062 \
  -r zap-report.html
```

**Not executed in this environment** — this sandbox has neither Docker nor a
running QuotesApi reachable from a container, so there is no real scan output
to paste honestly. The command above is the actual invocation to run locally
(`docker compose`'s `data-tier` app, once containerized, would be the more
realistic target); running it would very plausibly flag two things worth
pre-empting rather than waiting to be told: (1) no `Content-Security-Policy` or
`X-Content-Type-Options` response headers are set anywhere in this app today,
and (2) the OpenAPI spec being served at a predictable, unauthenticated path
(`/openapi/v1.json`) is itself a (mild, intentional) piece of API surface
disclosure. Both are real, named gaps — not claimed as fixed, since there is no
scan run to verify against.
