# Day 26 — Observability (Jaeger + Prometheus + Grafana instead of App Insights)

Application Insights needs an Azure subscription. **Jaeger** (traces),
**Prometheus** (metrics) and **Grafana** (dashboards/alerts) — all free,
open-source, self-hosted via `docker-compose.yml` — cover the same three things
App Insights would: a trace view, a metrics store, and PromQL in place of KQL.
The app already spoke OpenTelemetry before this exercise (tracing was wired up
several days ago); Day 26 replaces the one Azure-specific piece
(`UseAzureMonitor`) and adds the metrics pipeline that was missing entirely.

## Wiring

`Program.cs`:

```csharp
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(Telemetry.ServiceName))
    .WithTracing(tracing =>
    {
        tracing
            .AddSource(Telemetry.ServiceName)
            .AddAspNetCoreInstrumentation()
            .AddEntityFrameworkCoreInstrumentation()
            .AddHttpClientInstrumentation();

        if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            tracing.AddOtlpExporter(); // -> Jaeger, which speaks OTLP natively
        }
    })
    .WithMetrics(metrics =>
    {
        metrics
            .AddMeter("Microsoft.AspNetCore.Hosting")  // http.server.request.duration
            .AddMeter("System.Net.Http")               // http.client.request.duration
            .AddRuntimeInstrumentation()
            .AddPrometheusExporter();                  // always on - Prometheus pulls, nothing to point at a collector for
    });
```

```csharp
app.MapPrometheusScrapingEndpoint(); // GET /metrics
```

Traces needed **zero new exporter code** — `AddOtlpExporter()` already existed
here; Jaeger's all-in-one image just needs to be the thing listening at
`OTEL_EXPORTER_OTLP_ENDPOINT`. Only `UseAzureMonitor()` (and the
`Azure.Monitor.OpenTelemetry.AspNetCore` package) is what actually got removed.

## Proof: real metrics, scraped live

```bash
$ curl http://localhost:5062/metrics
# TYPE dotnet_gc_collections_total counter
dotnet_gc_collections_total{otel_scope_name="System.Runtime",gc_heap_generation="gen1"} 1
...
# TYPE http_server_request_duration_seconds histogram
http_server_request_duration_seconds_bucket{http_request_method="GET",http_response_status_code="200",http_route="api/reports/authors",le="0.005"} 4
http_server_request_duration_seconds_bucket{http_request_method="GET",http_response_status_code="200",http_route="api/reports/authors",le="0.25"} 5
...
http_server_request_duration_seconds_sum{http_request_method="GET",http_response_status_code="200",http_route="api/reports/authors"} 0.1677974
http_server_request_duration_seconds_count{http_request_method="GET",http_response_status_code="200",http_route="api/reports/authors"} 5
```

Real Prometheus exposition format, captured from a running `dotnet run` with
no Prometheus server even started yet — the endpoint exists independent of
anything scraping it, which is the whole point of a pull model.

## PromQL (in place of KQL)

**p50/p99 latency by endpoint:**

```promql
histogram_quantile(0.50, sum(rate(http_server_request_duration_seconds_bucket[5m])) by (le, http_route))
histogram_quantile(0.99, sum(rate(http_server_request_duration_seconds_bucket[5m])) by (le, http_route))
```

**Dependency call breakdown (outbound HTTP, e.g. the Entra backchannel from Day 22):**

```promql
sum(rate(http_client_request_duration_seconds_count[5m])) by (server_address, http_response_status_code)
```

The database side of the dependency breakdown isn't a Prometheus metric here —
EF Core's OpenTelemetry instrumentation emits *traces*, not a metrics meter, so
that half lives in Jaeger's per-trace span list instead (see below), the same
way App Insights' own "Dependencies" view is trace-derived too, not a separate
KQL table for everything.

**Alert on error rate** (Grafana alert rule, or `alerting.rules.yml` for
Prometheus's own Alertmanager):

```promql
sum(rate(http_server_request_duration_seconds_count{http_response_status_code=~"5.."}[5m]))
/
sum(rate(http_server_request_duration_seconds_count[5m])) > 0.05
```

Fires when more than 5% of requests over the last 5 minutes returned a 5xx.

## Proof: distributed tracing stitches API → worker → DB

This is the one place App Insights' automatic HTTP-context propagation doesn't
carry over for free: `Activity.Current` does **not** flow across Day 18's
in-memory queue on its own — by the time `QueuedHostedService` dequeues a job,
the HTTP request that enqueued it has already finished and its `Activity` has
ended. Fixed by capturing the request's trace context at enqueue time and
starting the job's own `Activity` as its child:

`Controllers/ExportsController.cs`:

```csharp
var triggeringContext = Activity.Current?.Context ?? default;
await taskQueue.QueueBackgroundWorkItemAsync(
    QuoteCsvExportJob.Create(jobId, ExportDirectory, triggeringContext), ct);
```

`Services/BackgroundJobs/QuoteCsvExportJob.cs`:

```csharp
using var activity = Telemetry.Source.StartActivity(
    "QuoteCsvExportJob.Process", ActivityKind.Internal, triggeringContext);

using var _ = LogContext.PushProperty("TraceId", activity?.TraceId.ToString() ?? "no-trace");
```

EF Core's own instrumentation then nests every query the job makes
(`GetPagedAsync`) under this `Activity` automatically — no extra code needed at
the DB layer for that link.

**Live proof** (no Jaeger even running — the same TraceId visible directly in
Serilog output, which is exactly what `LogContext.PushProperty` above is for):

```
[15:05:03 INF] TraceId=7d4b6e1b3f6066e799470291035b7900 HTTP POST /api/exports/quotes responded 202 in 19.4176 ms
[15:05:03 INF] TraceId=7d4b6e1b3f6066e799470291035b7900 Quote export 9822894d-... completed: 9822894d-....csv.
```

The **same TraceId** (`7d4b6e1b3f6066e799470291035b7900`) on the request line
and the background job's completion line — confirming the trace genuinely
spans API → worker. With Jaeger actually running (`OTEL_EXPORTER_OTLP_ENDPOINT`
set, per `docker-compose.yml`), searching that trace ID at
`http://localhost:16686` would show the full span tree: the ASP.NET Core root
span for `POST /api/exports/quotes`, `QuoteCsvExportJob.Process` as its child,
and the EF Core `SELECT` spans for each page of quotes nested under that —
API → worker → DB, one trace.
