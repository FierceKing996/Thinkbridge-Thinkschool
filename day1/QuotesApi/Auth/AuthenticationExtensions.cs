using System.IdentityModel.Tokens.Jwt;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Http.Resilience;
using Microsoft.IdentityModel.Tokens;
using Polly;
using QuotesApi.Services;

namespace QuotesApi.Auth;

public static class AuthenticationExtensions
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
}
