using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using QuotesApi.Data;
using QuotesApi.Messaging;
using QuotesApi.Repositories;
using QuotesApi.Services;
using QuotesApi.Services.BackgroundJobs;

namespace QuotesApi.Extensions;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration config)
    {
        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlite(config.GetConnectionString("DefaultConnection") ?? "Data Source=quotes.db"));

        // Scoped: one instance per request, matching AppDbContext's lifetime -
        // repositories hold a DbContext, which isn't thread-safe to share across requests.
        services.AddScoped<IQuoteRepository, QuoteRepository>();
        services.AddScoped<ICollectionRepository, CollectionRepository>();
        services.AddScoped<IAddCollectionItemCommandHandler, AddCollectionItemCommandHandler>();
        services.AddScoped<ICollectionQueries, CollectionQueries>();
        // Day 21: IAuthorsReportQuery resolves to the HybridCache-wrapped decorator,
        // not EfAuthorsReportQuery directly - see CachedAuthorsReportQuery.
        services.AddScoped<EfAuthorsReportQuery>();
        services.AddScoped<IAuthorsReportQuery>(sp =>
            new CachedAuthorsReportQuery(sp.GetRequiredService<HybridCache>(), sp.GetRequiredService<EfAuthorsReportQuery>()));
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

        // Day 18: singleton queue (one for the whole app's lifetime, shared by every
        // request that enqueues work) drained by a singleton hosted service - see
        // DAY18_BACKGROUND_JOBS.md.
        services.AddSingleton<IBackgroundTaskQueue>(_ => new BackgroundTaskQueue(capacity: 100));
        services.AddSingleton<ExportJobStatusStore>();
        services.AddHostedService<QueuedHostedService>();

        // Day 19 + 20: RabbitMQ stands in for Azure Service Bus - free,
        // self-hosted (see docker-compose.yml), with the same topic/DLQ shape.
        // Wired up only when RabbitMq:HostName is actually configured; unset (the
        // appsettings.json default), NoOpEventPublisher is used and neither
        // consumer hosted service is registered at all - the same "opt in when
        // configured" pattern this file already uses, so `dotnet run` and the test
        // suite never need a broker running.
        var rabbitMqSection = config.GetSection("RabbitMq");
        var rabbitMqOptions = rabbitMqSection.Get<RabbitMqOptions>() ?? new RabbitMqOptions();
        services.AddSingleton(rabbitMqOptions);

        if (!string.IsNullOrEmpty(rabbitMqSection["HostName"]))
        {
            services.AddSingleton<RabbitMqConnection>();
            services.AddSingleton<IEventPublisher, RabbitMqEventPublisher>();
            services.AddHostedService<RabbitMqTopologyInitializer>();
            services.AddHostedService<QuoteSearchIndexConsumerService>();
            services.AddHostedService<QuoteAuditLogConsumerService>();
        }
        else
        {
            services.AddSingleton<IEventPublisher, NoOpEventPublisher>();
        }

        // The relay always runs - it just has nothing to successfully publish
        // until a broker is configured, which is the correct, honest behavior
        // (see NoOpEventPublisher) rather than pretending delivery happened.
        services.AddHostedService<OutboxRelayService>();

        // Day 21: HybridCache always registered - with no IDistributedCache behind
        // it, it's a correct (just single-node) in-memory-only cache, which is what
        // makes `dotnet run`/tests work with no Redis. Redis (open-source,
        // self-hosted via docker-compose - not Azure Cache for Redis) plugs in as
        // the L2/backplane only when ConnectionStrings:Redis is configured.
        services.AddHybridCache(options =>
        {
            options.DefaultEntryOptions = new HybridCacheEntryOptions
            {
                Expiration = TimeSpan.FromSeconds(30),
                LocalCacheExpiration = TimeSpan.FromSeconds(10),
            };
        });

        var redisConnectionString = config.GetConnectionString("Redis");
        if (!string.IsNullOrEmpty(redisConnectionString))
        {
            services.AddStackExchangeRedisCache(o => o.Configuration = redisConnectionString);
        }

        return services;
    }
}
