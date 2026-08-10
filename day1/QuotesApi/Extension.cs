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
        services.AddScoped<ICollectionRepository, CollectionRepository>();
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

    public static IEndpointRouteBuilder MapCollectionEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/collections");

        // GET /api/collections/{id}
        group.MapGet("/{id:int}", async (int id, ICollectionRepository repo, CancellationToken ct) =>
        {
            var collection = await repo.GetByIdAsync(id, ct);
            return collection is not null ? Results.Ok(collection) : Results.NotFound();
        });

        // POST /api/collections
        group.MapPost("/", async (CreateCollectionRequest req, ICollectionRepository repo, CancellationToken ct) =>
        {
            Collection collection;
            try
            {
                // All invariant checking (name length, etc.) lives on the aggregate itself.
                collection = Collection.Create(req.Name, req.OwnerId);
            }
            catch (DomainException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(req.Name)] = [ex.Message] });
            }

            var created = await repo.AddAsync(collection, ct);
            return Results.Created($"/api/collections/{created.Id}", created);
        });

        // POST /api/collections/{id}/items
        group.MapPost("/{id:int}/items", async (int id, AddCollectionItemRequest req, ICollectionRepository repo, CancellationToken ct) =>
        {
            var collection = await repo.GetByIdAsync(id, ct);
            if (collection is null) return Results.NotFound();

            try
            {
                // Mutation goes through the aggregate root, not db.Items.Add(...) directly,
                // so duplicate-quote and max-items-per-collection invariants can't be bypassed.
                collection.AddItem(req.QuoteId);
            }
            catch (DomainException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(req.QuoteId)] = [ex.Message] });
            }

            await repo.UpdateAsync(collection, ct);
            return Results.Ok(collection);
        });

        // DELETE /api/collections/{id}/items/{quoteId}
        group.MapDelete("/{id:int}/items/{quoteId:int}", async (int id, int quoteId, ICollectionRepository repo, CancellationToken ct) =>
        {
            var collection = await repo.GetByIdAsync(id, ct);
            if (collection is null) return Results.NotFound();

            try
            {
                collection.RemoveItem(quoteId);
            }
            catch (DomainException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(quoteId)] = [ex.Message] });
            }

            await repo.UpdateAsync(collection, ct);
            return Results.Ok(collection);
        });

        return app;
    }
}