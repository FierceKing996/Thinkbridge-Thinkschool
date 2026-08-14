using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using QuotesApi;
using Serilog;
using Serilog.Context;

var builder = WebApplication.CreateBuilder(args);

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
builder.Services.AddOpenTelemetry()
    .ConfigureResource(resource => resource.AddService(Telemetry.ServiceName))
    .WithTracing(tracing => tracing
        .AddSource(Telemetry.ServiceName)
        .AddAspNetCoreInstrumentation()
        .AddEntityFrameworkCoreInstrumentation()
        .AddHttpClientInstrumentation()
        .AddOtlpExporter());

// 1. Add Infrastructure (DI, DbContext)
builder.Services.AddInfrastructure(builder.Configuration);
builder.Services.AddJwtAuth(builder.Configuration);

var app = builder.Build();

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
        var exceptionHandlerPathFeature = context.Features.Get<IExceptionHandlerPathFeature>();
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        
        await context.Response.WriteAsJsonAsync(new ProblemDetails
        {
            Title = "An unexpected error occurred.",
            Status = context.Response.StatusCode,
            Detail = exceptionHandlerPathFeature?.Error.Message
        });
    });
});

// 3. EF Core migrations applied at startup
using (var scope = app.Services.CreateScope())
{
    var dbContext = scope.ServiceProvider.GetRequiredService<AppDbContext>();
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
// that use RequireAuthorization() actually run.
app.UseAuthentication();
app.UseAuthorization();

// 5. Map Endpoints
app.MapAuthEndpoints();
app.MapQuoteEndpoints();
app.MapCollectionEndpoints();

app.Run();

public partial class Program { }