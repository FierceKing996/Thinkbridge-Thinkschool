using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using QuotesApi;

namespace QuotesApi.Migrations.SqlServer;

// Exists only so `dotnet ef migrations add` has a way to construct AppDbContext
// outside of ASP.NET Core's normal DI/hosting pipeline. The connection string
// here is never actually connected to for "migrations add" - EF only needs to
// know the provider (SqlServer) to generate provider-correct SQL. It matters
// for "database update", which this project's tests never call directly:
// Program.cs's own Database.MigrateAsync() does that against whatever
// connection string the WebApplicationFactory wires up at test time.
public class SqlServerDesignTimeFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();
        optionsBuilder.UseSqlServer(
            "Server=localhost;Database=DesignTimeOnly;Trusted_Connection=True;TrustServerCertificate=True;",
            sql => sql.MigrationsAssembly("QuotesApi.Migrations.SqlServer"));

        return new AppDbContext(optionsBuilder.Options);
    }
}
