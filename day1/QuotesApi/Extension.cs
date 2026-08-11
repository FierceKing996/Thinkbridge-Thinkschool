using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace QuotesApi;

public static class Extensions
{
    public static IServiceCollection AddJwtAuth(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<JwtOptions>(config.GetSection(JwtOptions.SectionName));
        services.AddSingleton<ITokenService, TokenService>();

        var jwtOptions = config.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
            ?? throw new InvalidOperationException("Jwt configuration section is missing.");
        var signingKey = new SymmetricSecurityKey(Convert.FromBase64String(jwtOptions.SigningKey));

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = jwtOptions.Issuer,
                    ValidateAudience = true,
                    ValidAudience = jwtOptions.Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = signingKey,
                    // Default is a 5-minute grace period past expiry - zero it so an
                    // expired token is rejected exactly when it says it expires.
                    ClockSkew = TimeSpan.Zero
                };
            });

        services.AddAuthorization();

        return services;
    }

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlite(config.GetConnectionString("DefaultConnection") ?? "Data Source=quotes.db"));
        
        // Scoped: one instance per request, matching AppDbContext's lifetime -
        // repositories hold a DbContext, which isn't thread-safe to share across requests.
        services.AddScoped<IQuoteRepository, QuoteRepository>();
        services.AddScoped<ICollectionRepository, CollectionRepository>();
        services.AddScoped<IRefreshTokenRepository, RefreshTokenRepository>();
        services.AddScoped<IAuthService, AuthService>();

        // Singleton: one clock for the whole app's lifetime. This is what makes it
        // swappable in tests - a test host registers a FixedClock instead, and every
        // consumer (now and any added later) sees the same frozen time with zero
        // reliance on wall-clock timing in assertions.
        services.AddSingleton<IClock, SystemClock>();

        // Transient: stateless and cheap, so a new instance per injection removes any
        // temptation to ever accumulate shared state on it.
        services.AddTransient<ITextNormalizer, TextNormalizer>();

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
        group.MapPost("/", async (CreateQuoteRequest req, IQuoteRepository repo, ITextNormalizer normalizer, CancellationToken ct) =>
        {
            // All invariant checking (length limits, etc.) lives on the aggregate itself.
            var result = Quote.Create(normalizer.Trim(req.Author), normalizer.Trim(req.Text));
            if (!result.Succeeded)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["error"] = [result.Error!] });
            }

            var created = await repo.CreateAsync(result.Quote!, ct);
            return Results.Created($"/api/quotes/{created.Id}", created);
        }).RequireAuthorization();

        // DELETE /api/quotes/{id}
        group.MapDelete("/{id:int}", async (int id, IQuoteRepository repo, CancellationToken ct) =>
        {
            var deleted = await repo.DeleteAsync(id, ct);
            return deleted ? Results.NoContent() : Results.NotFound();
        }).RequireAuthorization();

        return app;
    }

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth");

        // POST /api/auth/login
        group.MapPost("/login", async (LoginRequest req, IAuthService auth, CancellationToken ct) =>
        {
            var response = await auth.LoginAsync(req.Email, req.Password, ct);
            return response is not null ? Results.Ok(response) : Results.Unauthorized();
        });

        // POST /api/auth/refresh
        group.MapPost("/refresh", async (RefreshRequest req, IAuthService auth, CancellationToken ct) =>
        {
            var result = await auth.RefreshAsync(req.RefreshToken, ct);
            // Deliberately uniform 401 for every failure reason (invalid, expired,
            // reuse-detected) - the specific reason is logged server-side only, so
            // a client probing this endpoint can't distinguish "wrong token" from
            // "stolen token that tripped reuse detection".
            return result.Succeeded ? Results.Ok(result.Tokens) : Results.Unauthorized();
        });

        // POST /api/auth/logout
        group.MapPost("/logout", async (LogoutRequest req, IAuthService auth, CancellationToken ct) =>
        {
            await auth.LogoutAsync(req.RefreshToken, ct);
            return Results.NoContent();
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
        group.MapPost("/", async (CreateCollectionRequest req, ICollectionRepository repo, ITextNormalizer normalizer, CancellationToken ct) =>
        {
            Collection collection;
            try
            {
                // All invariant checking (name length, etc.) lives on the aggregate itself.
                collection = Collection.Create(normalizer.Trim(req.Name), req.OwnerId);
            }
            catch (DomainException ex)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { [nameof(req.Name)] = [ex.Message] });
            }

            var created = await repo.AddAsync(collection, ct);
            return Results.Created($"/api/collections/{created.Id}", created);
        });

        // POST /api/collections/{id}/items
        group.MapPost("/{id:int}/items", async (int id, AddCollectionItemRequest req, ICollectionRepository repo, IClock clock, CancellationToken ct) =>
        {
            var collection = await repo.GetByIdAsync(id, ct);
            if (collection is null) return Results.NotFound();

            try
            {
                // Mutation goes through the aggregate root, not db.Items.Add(...) directly,
                // so duplicate-quote and max-items-per-collection invariants can't be bypassed.
                collection.AddItem(req.QuoteId, clock.UtcNow);
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