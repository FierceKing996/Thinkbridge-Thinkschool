using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace QuotesApi;

public static class Extensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlite(config.GetConnectionString("DefaultConnection") ?? "Data Source=quotes.db"));
        
        // Requirement: DI with IQuoteRepository scoped
        services.AddScoped<IQuoteRepository, QuoteRepository>();
        services.AddProblemDetails();

        return services;
    }

    public static IEndpointRouteBuilder MapQuoteEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/quotes");

        // GET /api/quotes?page=N&size=N
        group.MapGet("/", async (int? page, int? size, IQuoteRepository repo, CancellationToken ct) =>
        {
            var p = page is > 0 ? page.Value : 1;
            var s = size is > 0 ? size.Value : 10;
            return Results.Ok(await repo.GetPagedAsync(p, s, ct));
        });

        // GET /api/quotes/{id}
        group.MapGet("/{id:int}", async (int id, IQuoteRepository repo, CancellationToken ct) =>
        {
            var quote = await repo.GetByIdAsync(id, ct);
            return quote is not null ? Results.Ok(quote) : Results.NotFound();
        });

        // POST /api/quotes
        group.MapPost("/", async (CreateQuoteRequest req, IQuoteRepository repo, CancellationToken ct) =>
        {
            // Requirement: Validation returning ValidationProblemDetails
            var errors = new Dictionary<string, string[]>();
            if (string.IsNullOrWhiteSpace(req.Author)) errors.Add(nameof(req.Author), ["Author is required."]);
            if (string.IsNullOrWhiteSpace(req.Text)) errors.Add(nameof(req.Text), ["Text is required."]);

            if (errors.Count > 0) return Results.ValidationProblem(errors);

            var quote = new Quote { Author = req.Author, Text = req.Text };
            var created = await repo.CreateAsync(quote, ct);
            
            return Results.Created($"/api/quotes/{created.Id}", created);
        });

        // DELETE /api/quotes/{id}
        group.MapDelete("/{id:int}", async (int id, IQuoteRepository repo, CancellationToken ct) =>
        {
            var deleted = await repo.DeleteAsync(id, ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        });

        return app;
    }
}