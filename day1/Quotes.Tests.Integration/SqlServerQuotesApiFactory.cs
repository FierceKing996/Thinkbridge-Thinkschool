using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QuotesApi;

namespace Quotes.Tests.Integration;

// Same idea as the SQLite-backed QuotesApiFactory (real Program.cs, real DI,
// real middleware pipeline - only the DbContext and clock are swapped), but
// pointed at a real, already-running SQL Server 2022 container instead of an
// in-memory SQLite connection. Program.cs's own Database.MigrateAsync() call
// applies the SQL-Server-specific migrations the first time this connects;
// EF's migrations history table makes every subsequent instance's MigrateAsync
// a fast no-op against the same database.
public class SqlServerQuotesApiFactory(string connectionString) : WebApplicationFactory<Program>
{
    public FakeClock Clock { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureServices(services =>
        {
            // RemoveAll<DbContextOptions<AppDbContext>>() alone isn't enough:
            // Program.cs's AddInfrastructure already registered Sqlite's internal
            // provider services (IDatabaseProvider etc.) into this container, and
            // those aren't scoped to a single DbContext type - removing only the
            // options object leaves them behind. Adding SqlServer's provider
            // services on top of Sqlite's still-present ones makes EF Core throw
            // "Services for database providers 'Sqlite', 'SqlServer' have been
            // registered" the moment any DbContext is resolved. Every EF Core
            // service the original registration added has to go, not just one.
            var efCoreDescriptors = services
                .Where(d => d.ServiceType.Namespace is not null
                    && d.ServiceType.Namespace.StartsWith("Microsoft.EntityFrameworkCore"))
                .ToList();

            foreach (var descriptor in efCoreDescriptors)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<AppDbContext>(options =>
                options.UseSqlServer(connectionString,
                    sql => sql.MigrationsAssembly("QuotesApi.Migrations.SqlServer")));

            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(Clock);
        });
    }
}
