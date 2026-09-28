# Capstone on Azure — how this would actually run with a subscription

Written for: you, as the "what if I had Azure" companion to `PROJECT_NEW.md`
and `DAY22_CAPSTONE.md`. Every substitution made across Days 19–27 was chosen
specifically so it would map cleanly back onto a real Azure service — this
document is that mapping, service by service, plus what would and wouldn't
need to change in the code.

## 1. The one-line version

**Almost none of the application code would change.** Every free substitute was
built behind the same seam a real Azure integration would sit behind —
`IEventPublisher`, `IDistributedCache` (which `HybridCache` already treats as a
pluggable L2), the vendor-neutral OpenTelemetry SDK, `IConfigurationBuilder`.
Swapping the *implementation* behind each seam, and changing config, is most of
the work. The two exceptions worth calling out honestly are Day 19 (RabbitMQ's
manual topology declarations vs. Service Bus's native topic/subscription model
— less code on Azure, not more) and Day 23/24 (Terraform → Bicep is a full
rewrite of the IaC files themselves, not a config swap, since they're
different languages for different control planes).

## 2. Service-by-service mapping

| Concern | What this repo uses now | What Azure gives you | Code impact |
|---|---|---|---|
| Compute | `dotnet run` locally / MonsterASP in prod | **Azure App Service** or **Azure Container Apps** | None — same `Program.cs`, same `ContainerRepository`/`ContainerImageTag` in `QuotesApi.csproj` already produce a container `az containerapp` or App Service's container deploy can consume directly. |
| Database | SQLite (dev), SQL Server (Testcontainers/optional) | **Azure SQL Database** | Zero code — Azure SQL speaks the same TDS wire protocol as SQL Server. `QuotesApi.Migrations.SqlServer` already exists and already targets `Microsoft.EntityFrameworkCore.SqlServer`; only the connection string changes. |
| Messaging | RabbitMQ (`Messaging/RabbitMq*.cs`) | **Azure Service Bus** (topics + subscriptions) | New `AzureServiceBusEventPublisher : IEventPublisher` + Service Bus SDK-based consumers; `IEventPublisher`'s contract doesn't change, so `OutboxRelayService` (the caller) doesn't change at all. |
| Cache L2 | Redis (self-hosted) | **Azure Cache for Redis** | **Zero code.** `AddStackExchangeRedisCache` already talks real Redis wire protocol — Azure Cache for Redis *is* Redis. Only `ConnectionStrings:Redis` changes. |
| Secrets | HashiCorp Vault (AppRole) | **Azure Key Vault** + **Managed Identity** | Delete `Extensions/VaultConfigurationExtensions.cs`'s call in `Program.cs`, restore the four-line `AddAzureKeyVault(new Uri(...), new DefaultAzureCredential())` block it replaced (still in git history). Genuinely *less* code than the Vault version, and no RoleId/SecretId to manage at all — Managed Identity needs zero credential material anywhere. |
| Observability | Jaeger + Prometheus + Grafana | **Application Insights** (Azure Monitor) | Restore `UseAzureMonitor(options => options.ConnectionString = ...)` — one call, additive alongside the existing `AddOtlpExporter()`, exactly as it was before Day 26. KQL replaces PromQL for querying. |
| IaC | Terraform (`infra-terraform/`) | **Bicep** (this repo already has `infra/main.bicep`, `infra/resources.bicep` from before this exercise) | Full rewrite — different language, different provider model — but the *shape* (API + SQL + messaging modules, dev/prod parameter files) carries over directly; see §4. |
| Deployment/drift | `terraform plan/apply/destroy` + workspaces | **Azure Deployment Stacks** + **azd** | Different tool, same three properties (atomic deploy, drift detection, clean teardown) — see §4. |
| Private networking | Docker network with no published ports | **Azure Private Endpoint** + **VNet integration** | Infra-only change (Bicep), zero application code — the app already reads a plain connection string/host name regardless of whether it resolves over the public internet or a private endpoint's private IP. |
| Auth (already in place) | JWT (internal) + optional Entra scheme | **Microsoft Entra ID** | Already implemented — `Auth/AuthenticationExtensions.cs`'s `EntraOptions` path exists today and just needs a real app registration's `TenantId`/`Audience`. Nothing new to build. |

## 3. What the Azure-native architecture looks like end to end

```
                         ┌────────────────────────┐
   Angular UI  ───────▶  │   Azure App Service /   │
  (or served from        │   Container Apps        │  ← Managed Identity (no credential
   the same App Service) │   (QuotesApi container)  │    material anywhere on this box)
                         └───────────┬─────────────┘
                                     │
              ┌──────────────────────┼───────────────────────┐
              ▼                      ▼                        ▼
      ┌───────────────┐     ┌────────────────┐      ┌──────────────────┐
      │ Azure SQL DB   │     │ Azure Service  │      │ Azure Cache for  │
      │ (private       │     │ Bus            │      │ Redis (private   │
      │ endpoint)      │     │ (topic +       │      │ endpoint)        │
      │                │     │ 2 subscriptions)│     │                  │
      └───────────────┘     └────────────────┘      └──────────────────┘
              │                      │
              │                      ▼
              │              ┌────────────────────┐
              │              │ Consumer functions/ │
              │              │ container apps      │
              │              │ (search-index,      │
              │              │  audit-log)          │
              │              └────────────────────┘
              ▼
      ┌────────────────┐        ┌─────────────────┐
      │ Azure Key Vault │◀─────▶│  Managed Identity │  (App Service/Container App's
      │ (secrets)       │        │  (no RoleId/      │   own identity, not a credential
      └────────────────┘        │   SecretId pair)   │   this app has to hold)
                                 └─────────────────┘
      ┌──────────────────────────────────────────┐
      │  Application Insights (traces + metrics + │
      │  logs) — same OpenTelemetry SDK calls,    │
      │  additive UseAzureMonitor() exporter      │
      └──────────────────────────────────────────┘

      All of the above (except the public App Service endpoint) sit behind
      Private Endpoints inside one VNet — no public IP on the SQL DB, Service
      Bus namespace, Redis cache, or Key Vault.
```

## 4. Day by day, what the Azure version actually looks like

### Day 19/20 — Service Bus instead of RabbitMQ

Service Bus's topic/subscription model **is** the thing RabbitMQ's topic
exchange + queues was standing in for — so this direction is less work than
the free substitute, not more:

- `RabbitMqTopology.DeclareAsync`'s manual `ExchangeDeclareAsync` /
  `QueueDeclareAsync` / `QueueBindAsync` calls disappear entirely — a Service
  Bus topic + two subscriptions are declared once in Bicep, not imperatively
  at every publish/consume call.
- Dead-lettering is **built in** — a Service Bus subscription has a
  `maxDeliveryCount` property; a message that fails that many times is
  auto-dead-lettered to the subscription's own `$DeadLetterQueue`, no
  `x-dead-letter-exchange` argument to wire up by hand.
- `RabbitMqEventPublisher.PublishAsync` becomes `ServiceBusSender.SendMessageAsync`
  (Azure.Messaging.ServiceBus SDK) — same `IEventPublisher.PublishAsync(routingKey,
  envelope, ct)` signature, `OutboxRelayService` doesn't know or care which
  implementation is behind it.
- Competing consumers: identical concept — multiple `ServiceBusProcessor`
  instances on the same subscription, Service Bus does the round-robin
  delivery the same way RabbitMQ did.
- The `ProcessedMessage` idempotent-dedupe table stays **exactly as-is** —
  Service Bus is also at-least-once delivery, so the same
  crash-redelivers-but-doesn't-duplicate story from `DAY20_OUTBOX.md` holds
  unchanged.

### Day 21 — Azure Cache for Redis

Nothing to redesign — `services.AddStackExchangeRedisCache(o => o.Configuration
= ...)` already exists in `Extensions/ServiceCollectionExtensions.cs`. Point
`ConnectionStrings:Redis` at `<name>.redis.cache.windows.net:6380,password=...,ssl=True`
instead of `localhost:6379` and it's the same code path, TLS included. The
stampede-protection story (`CachedAuthorsReportQuery`,
`loadtest-authors-report.ps1`) is completely unaffected — `HybridCache`
doesn't know or care what's behind its L2.

### Day 22 — Resilience with Polly

**Unchanged.** The whole point of this pattern is that it protects *this app's
own outbound call* to Entra — that dependency doesn't move whether the app
itself runs locally or on Azure. `Microsoft.Extensions.Http.Resilience` is
already Azure-appropriate; nothing here is a "free substitute" to begin with.

### Day 23/24 — Bicep + Deployment Stacks + azd

This is the one full rewrite, because Bicep and Terraform are genuinely
different languages against different control planes — but the *shape* from
`infra-terraform/` carries over directly:

```bicep
// modules/database.bicep
resource sqlServer 'Microsoft.Sql/servers@2023-08-01-preview' = { ... }
resource sqlDatabase 'Microsoft.Sql/servers/databases@2023-08-01-preview' = { ... }

// modules/messaging.bicep
resource serviceBusNamespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = { ... }
resource topic 'Microsoft.ServiceBus/namespaces/topics@2022-10-01-preview' = { ... }
resource searchIndexSub 'Microsoft.ServiceBus/namespaces/topics/subscriptions@2022-10-01-preview' = {
  properties: { maxDeliveryCount: 5 }   // dead-lettering, declared not coded
}

// modules/api.bicep
resource containerApp 'Microsoft.App/containerApps@2024-03-01' = {
  identity: { type: 'SystemAssigned' }   // Managed Identity - no secret ever generated
  ...
}

// main.bicep
module database 'modules/database.bicep' = { params: { environment: environment } }
module messaging 'modules/messaging.bicep' = { ... }
module api 'modules/api.bicep' = { params: { sqlConnectionString: database.outputs.connectionString } }
```

`environments/dev.bicepparam` and `environments/prod.bicepparam` replace
`dev.tfvars`/`prod.tfvars` — same idea (one file per environment, nothing
environment-specific duplicated across modules).

`deploy.ps1`'s `terraform plan/apply/destroy` + workspaces becomes:

```powershell
azd env new dev
azd up                    # provisions AND deploys in one command - closer to
                           # "one command" than Terraform + a separate app deploy step
azd env new prod
azd up --environment prod

# Deployment Stacks (the actual Day 24 ask) wrap the Bicep deploy itself:
az stack sub create --name quotesapi-dev --location <region> \
  --template-file main.bicep --parameters environments/dev.bicepparam \
  --deny-settings-mode denyDelete   # blocks manual deletion of anything the stack manages

az stack sub show --name quotesapi-dev    # drift: flags resources changed outside the stack
az stack sub delete --name quotesapi-dev --action-on-unmanage deleteAll   # clean teardown
```

`azd` is genuinely a bigger win here than the Terraform wrapper script: it
provisions infra *and* builds/pushes the container *and* deploys the app in
one `azd up`, where the Terraform version still needed a separate
`dotnet publish /t:PublishContainer` step outside the IaC tool entirely.

### Day 25 — Key Vault + Managed Identity

This is the one where Azure is **strictly simpler** than the free substitute,
worth saying plainly rather than glossing over: Vault's AppRole still needed a
RoleId/SecretId pair to exist as an environment variable — a credential,
provisioned once, that could itself leak. Managed Identity needs **no
credential material anywhere, ever** — the Container App/App Service's own
identity, backed by the Azure platform, is what Key Vault trusts. The whole
`VaultConfigurationExtensions.cs` file disappears; what replaces it is the
exact four lines this repo had *before* Day 25 (visible in git history):

```csharp
var keyVaultUri = builder.Configuration["KeyVault:Uri"];
if (!string.IsNullOrEmpty(keyVaultUri))
{
    builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), new DefaultAzureCredential());
}
```

`DefaultAzureCredential` transparently uses the Container App's Managed
Identity in Azure and falls back to your `az login` session locally — so local
dev against a *real* (not emulated) Key Vault works with zero extra
credentials, something the Vault/AppRole version couldn't offer without
standing up a local Vault dev-server.

### Day 26 — Application Insights

Also a near-total reversion to pre-Day-26 code — `UseAzureMonitor()` is
additive on top of the OTLP exporter that's still there for local Jaeger
debugging if you want it:

```csharp
var appInsightsConnectionString = builder.Configuration["ApplicationInsights:ConnectionString"]
    ?? builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
if (!string.IsNullOrEmpty(appInsightsConnectionString))
{
    otel.UseAzureMonitor(options => options.ConnectionString = appInsightsConnectionString);
}
```

The trace-propagation fix from Day 26 (`ExportsController` capturing
`Activity.Current?.Context`, `QuoteCsvExportJob` starting a child `Activity`
from it) **stays exactly as-is** — that's a real OpenTelemetry correctness fix
independent of which exporter is behind it; App Insights' "Application Map"
and "End-to-end transaction" views would show the same API → worker → DB chain
Jaeger does today, driven by the same code.

**PromQL → KQL**, concretely:

```kql
// p50/p99 by endpoint (App Insights' requests table, auto-populated - no
// manual histogram bucket math the way Prometheus needs)
requests
| summarize p50=percentile(duration, 50), p99=percentile(duration, 99) by name
```

```kql
// dependency breakdown (App Insights' dependencies table - HTTP calls AND SQL
// calls both land here automatically; Prometheus only had the HTTP half)
dependencies
| summarize count(), avg(duration) by target, type
```

```kql
// error-rate alert (as an Azure Monitor scheduled query alert rule)
requests
| summarize total=count(), failed=countif(success == false)
| extend errorRate = todouble(failed) / total
| where errorRate > 0.05
```

Note the dependency query: App Insights' `dependencies` table gets **SQL calls
for free** via the same EF Core OpenTelemetry instrumentation already in
`Program.cs` — Prometheus never had that half of the picture at all (see the
honest gap noted in `DAY26_OBSERVABILITY.md`), because SQL calls are traced,
not metriced, and Prometheus only ever saw metrics. On Azure this gap closes
automatically, no new instrumentation code.

### Day 27 — Private Endpoint + Defender

- The Docker-network isolation pattern becomes: Azure SQL, Service Bus, Redis,
  and Key Vault all get a **Private Endpoint** inside the app's VNet, public
  network access disabled on each resource. Same intent (data tier reachable
  *only* from the app), enforced by the platform instead of a Docker network
  boundary — zero application code changes, this is 100% Bicep.
- The OpenAPI hardening (Bearer scheme, versioning, Kestrel body-size limit)
  and the two real bugs fixed (413 status code, exception-message leak) are
  **pure application code, unrelated to Azure vs. not** — they stay exactly as
  committed.
- OWASP ZAP baseline can run as a step in an Azure DevOps/GitHub Actions
  pipeline against the deployed dev Container App URL, same invocation as
  documented in `DAY27_SECURITY.md`, now with a real reachable target instead
  of "not executed in this environment." **Microsoft Defender for Cloud**
  additionally gives continuous scanning (container image vulnerabilities, SQL
  vulnerability assessment, Key Vault access anomalies) with zero extra code —
  a capability with no free-tier local equivalent at all.

## 5. What you'd actually spend money on

Rough shape, not a quote — sizes matter more than exact numbers:

- **App Service Plan / Container Apps environment** — smallest tier that keeps
  the app warm (Container Apps can scale to zero between demo sessions,
  App Service Basic/Standard cannot).
- **Azure SQL Database** — Basic or a serverless compute tier; this app's
  actual load doesn't need more.
- **Service Bus** — Basic tier lacks topics (needs **Standard** at minimum for
  the topic/subscription model this exercise relies on).
- **Azure Cache for Redis** — Basic C0 is enough for demonstrating stampede
  protection; production HA would want Standard.
- **Key Vault** — pay-per-operation, effectively free at this app's volume.
- **Application Insights** — free tier's daily data cap comfortably covers a
  demo app; real cost risk only shows up at production traffic volumes.

The free, self-hosted version this repo actually runs costs nothing and
teaches the same integration patterns — which is exactly why every
substitution in Days 19–27 was chosen to sit behind the same abstraction a
real Azure service would.
