using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;
using QuotesApi.Auth;
using QuotesApi.Data;
using QuotesApi.Extensions;
using QuotesApi.Models;
using QuotesApi.Services;
using Serilog;
using Serilog.Context;

var builder = WebApplication.CreateBuilder(args);

// Day 27: a hard ceiling on request body size, independent of any DTO-level
// validation (CreateQuoteRequest's own length limits still apply on top of
// this). 64KB is generous relative to the largest real payload (a quote body
// is at most ~1.2KB of JSON) but rules out a multi-MB body as a cheap
// single-request memory/CPU amplification vector before it ever reaches model
// binding.
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = 64 * 1024;
});

// Day 25: HashiCorp Vault (free, self-hosted) in place of Azure Key Vault - see
// DAY25_IDENTITY.md. Only wired when Vault:Address is actually configured, so
// local dev (user-secrets, see JwtOptions.SigningKey) and MonsterASP production
// (a real environment variable, Jwt__SigningKey - shared hosting with no room
// for a Vault sidecar) don't need one; an environment that *does* have a Vault
// reachable (a VM, a container platform) opts in with nothing else changing.
var vaultAddress = builder.Configuration["Vault:Address"];
if (!string.IsNullOrEmpty(vaultAddress))
{
    builder.Configuration.AddVaultSecrets(vaultAddress, mountPoint: "secret", secretPath: "quotesapi");
}

// Replaces the default logging providers entirely - levels, sinks, and
// enrichers all come from the "Serilog" section in appsettings.json (and
// appsettings.Development.json on top of it), not hardcoded here.
builder.Host.UseSerilog((context, services, configuration) => configuration
    .ReadFrom.Configuration(context.Configuration)
    .ReadFrom.Services(services)
    .Enrich.FromLogContext());

// AddSource(Telemetry.ServiceName) is what makes the custom "verify-password"
// span (see AuthService) actually get exported - without it, activities from
// that ActivitySource are created but silently dropped by the SDK. The
// AspNetCore instrumentation creates one root Activity per request, the same
// one ASP.NET Core's own hosting layer already derives HttpContext.TraceIdentifier
// from - which is exactly why the TraceId pushed into Serilog's LogContext
// below lines up with the trace ID shown in Jaeger for the same request,
// with no extra wiring needed.
//
// Day 26: Jaeger (traces) + Prometheus (metrics) in place of Azure Monitor/App
// Insights - see DAY26_OBSERVABILITY.md and docker-compose.yml. Tracing needed no
// new exporter at all: Jaeger speaks OTLP natively, so the AddOtlpExporter() call
// that already existed here (originally "in case a collector is configured")
// points straight at it - only App Insights's own exporter (UseAzureMonitor) is
// what's actually being replaced.
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(Telemetry.ServiceName))
    .WithTracing(tracing =>
    {
        tracing
            .AddSource(Telemetry.ServiceName)
            .AddAspNetCoreInstrumentation()
            .AddEntityFrameworkCoreInstrumentation()
            .AddHttpClientInstrumentation();

        // Only export when a collector endpoint is actually configured. The free
        // MonsterASP host has no OTLP collector, and an unconfigured exporter
        // logs a connection failure on a loop - same "opt in when configured"
        // pattern this file uses throughout.
        if (!string.IsNullOrEmpty(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            tracing.AddOtlpExporter();
        }
    })
    .WithMetrics(metrics =>
    {
        // ASP.NET Core and HttpClient have emitted their own metrics natively via
        // System.Diagnostics.Metrics since .NET 8 - no "AddXInstrumentation()" call
        // exists for metrics the way it does for tracing; subscribing to the meter
        // by name is the whole integration.
        metrics
            .AddMeter("Microsoft.AspNetCore.Hosting")       // http.server.request.duration -> p50/p99 by endpoint
            .AddMeter("System.Net.Http")                    // http.client.request.duration -> dependency call breakdown
            .AddRuntimeInstrumentation()
            // Always on, unlike the OTLP trace exporter above: Prometheus is
            // pull-based (it scrapes /metrics), so there's nothing to point at a
            // collector for - the endpoint just exists, and whether anything ever
            // scrapes it is Prometheus's problem, not this app's.
            .AddPrometheusExporter();
    });

builder.Services.AddHealthChecks();
builder.Services.AddControllers();

// Day 27: OpenAPI generation (built into ASP.NET Core, no Swashbuckle needed)
// with a security-scheme transformer so the generated document actually
// reflects that most endpoints require a bearer token - without it, the spec
// silently under-describes the API's real auth surface.
builder.Services.AddOpenApi(options =>
{
    options.AddDocumentTransformer<BearerSecuritySchemeTransformer>();
});

// API versioning: every controller is explicitly "1.0" now (see [ApiVersion]
// on each), read from a header/query string rather than the URL path, so no
// existing route (or the Angular UI's existing API calls) changes shape - an
// unversioned request still resolves to 1.0 exactly as it always has. This is
// the harness for a real v2 later, not a v2 that exists yet.
builder.Services.AddApiVersioning(options =>
{
    options.DefaultApiVersion = new Asp.Versioning.ApiVersion(1, 0);
    // Every controller already declares [ApiVersion("1.0")] explicitly, so this
    // is defense-in-depth rather than load-bearing today - it only matters once a
    // second version exists and a client hasn't been updated to ask for one.
    options.AssumeDefaultVersionWhenUnspecified = true;
    options.ReportApiVersions = true;
    options.ApiVersionReader = Asp.Versioning.ApiVersionReader.Combine(
        new Asp.Versioning.HeaderApiVersionReader("X-Api-Version"),
        new Asp.Versioning.QueryStringApiVersionReader("api-version"));
}).AddMvc(); // required for [ApiVersion] on MVC controllers to actually take effect

// 1. Add Infrastructure (DI, DbContext)
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddJwtAuth(builder.Configuration);

var app = builder.Build();

app.MapHealthChecks("/health");

// GET /api/meta - lets the deployed frontend show which live environment
// it's talking to (see render.yaml's Environment__Label env var and
// app.html's badge). Falls back to ASPNETCORE_ENVIRONMENT itself so this is
// never empty even on a host that doesn't set the custom var - local
// `dotnet run` (Development) and every other environment this app has run
// in until now (day1/QuotesApi's own CI, `docker compose` locally) all
// still get a sensible answer.
app.MapGet("/api/meta", (IConfiguration config, IHostEnvironment env) =>
    Results.Ok(new { environment = config["Environment:Label"] ?? env.EnvironmentName.ToLowerInvariant() }));

// Day 26: Prometheus scrapes this, on a plain-text pull model - no exporter
// endpoint or API key to configure anywhere, unlike App Insights's push model.
app.MapPrometheusScrapingEndpoint();

// Day 27: GET /openapi/v1.json - the hardened spec (bearer scheme included).
app.MapOpenApi();

// Pushes TraceId onto Serilog's LogContext for the lifetime of the request -
// every log line emitted anywhere downstream (including EF Core's own SQL
// logging and UseSerilogRequestLogging's summary line) picks it up via
// Enrich.FromLogContext() above, without passing it through every call site.
app.Use(async (context, next) =>
{
    using (LogContext.PushProperty("TraceId", context.TraceIdentifier))
    {
        await next();
    }
});

app.UseSerilogRequestLogging();

// 2. Exception middleware returning ProblemDetails
app.UseExceptionHandler(exceptionHandlerApp =>
{
    exceptionHandlerApp.Run(async context =>
    {
        var exception = context.Features.Get<IExceptionHandlerPathFeature>()?.Error;

        // Day 27 fix: two real gaps found while testing the new Kestrel body-size
        // limit. (1) BadHttpRequestException (e.g. a request over
        // MaxRequestBodySize) carries its own correct StatusCode - 413 here -
        // which this handler was previously discarding in favor of a hardcoded
        // 500, misrepresenting a client error as a server failure. (2) every path
        // through here put the raw exception message into the response body
        // unconditionally - fine for a client-caused 4xx (the message is
        // actionable, like "body too large"), but an information-disclosure risk
        // for a genuine unhandled 500 (internal exception text - stack details,
        // connection strings in ADO exceptions, etc. - reaching an external
        // caller). Only 4xx-shaped exceptions get their message echoed back now.
        var statusCode = exception is BadHttpRequestException badRequest
            ? badRequest.StatusCode
            : StatusCodes.Status500InternalServerError;
        var isClientError = statusCode is >= 400 and < 500;

        context.Response.StatusCode = statusCode;

        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Title = isClientError ? exception!.Message : "An unexpected error occurred.",
            Status = statusCode,
            Detail = isClientError ? exception!.Message : null
        });
    });
});

// Serve the built Angular app (wwwroot/) as static files. Registered before
// AuthN/AuthZ because the shell and its assets are public; MapFallbackToFile
// below hands unmatched non-API routes back to index.html so client-side
// routing deep links resolve. wwwroot/ is kept in the repo (a .gitkeep) so
// WebApplication.CreateBuilder can resolve the web root even before a UI
// build has staged anything into it; publish-monsterasp.ps1 fills it in.
app.UseDefaultFiles();
app.UseStaticFiles();

// 3. EF Core migrations applied at startup
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();

    // Production points the SQLite file at App_Data/ (not served, survives
    // redeploys) - SQLite won't create the directory itself. Derived from the
    // actual resolved connection string, not hardcoded to "App_Data", and
    // skipped entirely when the data source has no directory component (the
    // Development default, "Data Source=quotes.db", resolves straight to
    // ContentRootPath with nothing to create). This used to run
    // unconditionally regardless of environment - harmless under `dotnet run`
    // (the working directory is always writable there) but a real bug found
    // running this in a container built from the official non-root
    // mcr.microsoft.com/dotnet/aspnet image: /app itself is owned by root, so
    // creating App_Data/ under it as the container's unprivileged user threw
    // UnauthorizedAccessException on every boot in Development.
    var connectionString = dbContext.Database.GetConnectionString() ?? "Data Source=quotes.db";
    var sqliteDataSource = new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder(connectionString).DataSource;
    var sqliteDirectory = Path.GetDirectoryName(Path.Combine(app.Environment.ContentRootPath, sqliteDataSource));
    if (!string.IsNullOrEmpty(sqliteDirectory))
    {
        Directory.CreateDirectory(sqliteDirectory);
    }

    // EnsureCreated can be used for simple SQLite setups, or MigrateAsync if you create migrations
    await dbContext.Database.MigrateAsync();

    if (!dbContext.Users.Any())
    {
        dbContext.Users.Add(new User
        {
            Email = "demo@quotesapi.dev",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("correct-horse-battery-staple"),
            Scopes = "quotes.write"
        });
        // No scopes on purpose - seeded specifically to exercise the "authenticated
        // but not authorized" (403) path for the can-edit-quotes claim policy.
        dbContext.Users.Add(new User
        {
            Email = "readonly@quotesapi.dev",
            PasswordHash = BCrypt.Net.BCrypt.HashPassword("correct-horse-battery-staple")
        });
        await dbContext.SaveChangesAsync();
    }
}

// 4. AuthN/AuthZ - must come after routing is set up and before the endpoints
// that use [Authorize] actually run.
app.UseAuthentication();
app.UseAuthorization();

// 5. Map controllers (Controllers/*Controller.cs)
app.MapControllers();

// Anything not matched above and not a real file in wwwroot is a client-side
// route - hand back the SPA shell.
app.MapFallbackToFile("index.html");

app.Run();

public partial class Program { }