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
            services.RemoveAll<DbContextOptions<AppDbContext>>();
            services.AddDbContext<AppDbContext>(options =>
                options.UseSqlServer(connectionString,
                    sql => sql.MigrationsAssembly("QuotesApi.Migrations.SqlServer")));

            services.RemoveAll<IClock>();
            services.AddSingleton<IClock>(Clock);
        });
    }
}
