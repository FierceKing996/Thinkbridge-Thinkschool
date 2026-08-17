using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.IdentityModel.Tokens;
using Polly;

namespace QuotesApi;

public static class Extensions
{
    private const string PolicySchemeName = "InternalOrEntra";
    private const string InternalSchemeName = "Internal";
    private const string EntraSchemeName = "Entra";
    private const string EntraBackchannelClientName = "entra-backchannel";

    public static IServiceCollection AddJwtAuth(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<JwtOptions>(config.GetSection(JwtOptions.SectionName));
        services.AddSingleton<ITokenService, TokenService>();

        var jwtOptions = config.GetSection(JwtOptions.SectionName).Get<JwtOptions>()
            ?? throw new InvalidOperationException("Jwt configuration section is missing.");
        var signingKey = new SymmetricSecurityKey(Convert.FromBase64String(jwtOptions.SigningKey));

        // Absent unless an "Entra" section has been configured (see EntraOptions) -
        // that requires an app registration created in the Azure Portal, which this
        // code cannot do on your behalf.
        var entraOptions = config.GetSection(EntraOptions.SectionName).Get<EntraOptions>();

        var authBuilder = services.AddAuthentication(options =>
        {
            options.DefaultScheme = PolicySchemeName;
            options.DefaultChallengeScheme = PolicySchemeName;
        });

        // Picks a scheme by peeking at the token's issuer claim - unvalidated at
        // this point, just enough to route to the handler that will actually
        // validate it. A malformed or missing token falls through to Internal,
        // which then rejects it with a normal 401.
        authBuilder.AddPolicyScheme(PolicySchemeName, "Internal or Entra JWT", options =>
        {
            options.ForwardDefaultSelector = context => SelectScheme(context, entraOptions);
        });

        authBuilder.AddJwtBearer(InternalSchemeName, options =>
        {
            // Without this, the handler silently remaps short JWT claim names to
            // long legacy XML-schema URIs (e.g. "sub" becomes ".../nameidentifier"),
            // so a claims lookup by JwtRegisteredClaimNames.Sub finds nothing even
            // though the token genuinely contains a "sub" claim.
            options.MapInboundClaims = false;
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

        if (entraOptions is not null)
        {
            authBuilder.AddJwtBearer(EntraSchemeName, options =>
            {
                // Authority triggers OIDC metadata discovery (.well-known/openid-configuration),
                // so Entra's signing keys are fetched and rotated automatically -
                // this is the whole point of delegating: no key management here at all.
                options.Authority = $"https://login.microsoftonline.com/{entraOptions.TenantId}/v2.0";
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidAudience = entraOptions.Audience,
                    ValidateLifetime = true
                };
            });

            // Entra ID is the "other API" this app calls - the metadata/JWKS
            // fetch behind Authority above. A transient blip there (Entra
            // rate-limiting, a network hiccup) would otherwise reject every
            // in-flight token validation with no retry at all. Named, not
            // typed: JwtBearerOptions.Backchannel takes a plain HttpClient,
            // not a typed client.
            services.AddHttpClient(EntraBackchannelClientName)
                .AddResilienceHandler("default", ConfigureResilience);

            // AddJwtBearer's simple Action<JwtBearerOptions> delegate above has no
            // DI access, so Backchannel is wired separately via named-options
            // configuration, which does - this runs after the delegate above,
            // only setting Backchannel and leaving every other option untouched.
            services.AddOptions<JwtBearerOptions>(EntraSchemeName)
                .Configure<IHttpClientFactory>((options, httpClientFactory) =>
                {
                    options.Backchannel = httpClientFactory.CreateClient(EntraBackchannelClientName);
                });
        }

        // Policies, not roles: "can-edit-quotes" is a portable rule name, while
        // RequireRole("admin") would hard-wire a specific role's meaning into the
        // endpoint. If tomorrow "who can write quotes" changes from a scope claim
        // to something else entirely, only this policy definition needs to change -
        // every endpoint that references it by name stays untouched.
        services.AddAuthorization(options =>
        {
            options.AddPolicy("can-edit-quotes", policy => policy.RequireClaim("scope", "quotes.write"));
            options.AddPolicy("can-delete-own-quote", policy => policy.Requirements.Add(new SameOwnerRequirement()));
        });
        services.AddScoped<IAuthorizationHandler, SameOwnerAuthorizationHandler>();

        return services;
    }

    // internal, not private: lets tests exercise the exact same pipeline
    // configuration production code registers, instead of a hand-copied
    // duplicate that could silently drift from what actually runs.
    //
    // Defaults per spec: 3 retries, exponential backoff with jitter (spreads
    // retries out so a fleet of instances doesn't all hammer Entra at the
    // same instant after a shared blip); circuit opens once 50% of calls in
    // a rolling 30s window fail; a 10s ceiling on the whole call including
    // every retry, so a hung request can't block a token validation forever.
    // No custom OnRetry/logging callback needed - AddResilienceHandler wires
    // its own ILogger-based telemetry automatically, which is what actually
    // emits the retry log lines.
    internal static void ConfigureResilience(ResiliencePipelineBuilder<HttpResponseMessage> builder)
    {
        builder.AddRetry(new HttpRetryStrategyOptions
        {
            MaxRetryAttempts = 3,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true
        });

        builder.AddCircuitBreaker(new HttpCircuitBreakerStrategyOptions
        {
            FailureRatio = 0.5,
            SamplingDuration = TimeSpan.FromSeconds(30),
            MinimumThroughput = 4
        });

        builder.AddTimeout(TimeSpan.FromSeconds(10));
    }

    // internal, not private: makes the routing logic directly unit-testable
    // (construct a DefaultHttpContext, call this, assert the returned scheme
    // name) instead of only reachable through a full HTTP round trip against
    // a real Entra tenant this project doesn't have access to.
    internal static string SelectScheme(HttpContext context, EntraOptions? entraOptions)
    {
        if (entraOptions is null)
        {
            return InternalSchemeName;
        }

        var authHeader = context.Request.Headers.Authorization.FirstOrDefault();
        if (authHeader is null || !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return InternalSchemeName;
        }

        try
        {
            var rawToken = authHeader["Bearer ".Length..].Trim();
            var issuer = new JwtSecurityTokenHandler().ReadJwtToken(rawToken).Issuer;
            return issuer.Contains("login.microsoftonline.com", StringComparison.OrdinalIgnoreCase)
                ? EntraSchemeName
                : InternalSchemeName;
        }
        catch (ArgumentException)
        {
            // Not a well-formed JWT - let Internal's normal validation reject it.
            return InternalSchemeName;
        }
    }

    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlite(config.GetConnectionString("DefaultConnection") ?? "Data Source=quotes.db"));
        
        // Scoped: one instance per request, matching AppDbContext's lifetime -
        // repositories hold a DbContext, which isn't thread-safe to share across requests.
        services.AddScoped<IQuoteRepository, QuoteRepository>();
        services.AddScoped<ICollectionRepository, CollectionRepository>();
        services.AddScoped<IUserRepository, UserRepository>();
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
        group.MapPost("/", async (CreateQuoteRequest req, IQuoteRepository repo, ITextNormalizer normalizer, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var userId = int.Parse(user.FindFirstValue(JwtRegisteredClaimNames.Sub)!);

            // All invariant checking (length limits, etc.) lives on the aggregate itself.
            var result = Quote.Create(normalizer.Trim(req.Author), normalizer.Trim(req.Text), userId);
            if (!result.Succeeded)
            {
                return Results.ValidationProblem(new Dictionary<string, string[]> { ["error"] = [result.Error!] });
            }

            var created = await repo.CreateAsync(result.Quote!, ct);
            return Results.Created($"/api/quotes/{created.Id}", created);
        }).RequireAuthorization("can-edit-quotes");

        // DELETE /api/quotes/{id}
        group.MapDelete("/{id:int}", async (int id, IQuoteRepository repo, IAuthorizationService authService, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var quote = await repo.GetByIdAsync(id, ct);
            if (quote is null) return Results.NotFound();

            // Resource-based checks can't be expressed via RequireAuthorization() on
            // the route - there's no specific Quote to check against until it's been
            // fetched, so this has to be an explicit, imperative call.
            var authResult = await authService.AuthorizeAsync(user, quote, "can-delete-own-quote");
            if (!authResult.Succeeded)
            {
                return Results.Forbid();
            }

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
        group.MapGet("/{id:int}", async (int id, ICollectionRepository repo, IQuoteRepository quotes, CancellationToken ct) =>
        {
            var collection = await repo.GetByIdAsync(id, ct);
            if (collection is null) return Results.NotFound();

            // One batched WHERE Id IN (...) query for the whole collection instead
            // of one SELECT per item - was N+1 before (see PR history), caught via
            // the trace showing N sibling EF spans under the request's root span.
            var quoteIds = collection.Items.Select(i => i.QuoteId).ToList();
            var quotesById = (await quotes.GetByIdsAsync(quoteIds, ct)).ToDictionary(q => q.Id);
            var items = collection.Items
                .Select(i => new CollectionItemDetail(i.QuoteId, quotesById[i.QuoteId].Author, quotesById[i.QuoteId].Text, i.AddedAt))
                .ToList();

            return Results.Ok(new CollectionDetailResponse(collection.Id, collection.Name, collection.OwnerId, items));
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
        }).RequireAuthorization();

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
        }).RequireAuthorization();

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
        }).RequireAuthorization();

        return app;
    }
}