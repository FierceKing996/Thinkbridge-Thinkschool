using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using QuotesApi.Auth;
using QuotesApi.Data;
using QuotesApi.Dtos;
using QuotesApi.Models;
using QuotesApi.Repositories;
using QuotesApi.Services;
using Xunit;

namespace Quotes.Tests.Unit;

// Regression test for a real CI failure: switching AppDbContext from Sqlite to
// SqlServer inside a WebApplicationFactory override threw "Services for
// database providers 'Sqlite', 'SqlServer' have been registered" because
// RemoveAll<DbContextOptions<AppDbContext>>() alone doesn't remove the
// provider-specific internal EF services the first registration added. This
// reproduces the exact registration sequence without Docker or a real
// database - the conflict fires during model-build/provider-resolution,
// before any connection is ever opened, so an unreachable connection string
// is enough to prove the point either way.
public class MultiProviderDbContextSwitchTests
{
    [Fact]
    public void RemovingOnlyDbContextOptions_ThenSwitchingProvider_ThrowsProviderConflict()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite("Data Source=:memory:"));

        // The insufficient fix - what SqlServerQuotesApiFactory had before.
        services.RemoveAll<DbContextOptions<AppDbContext>>();
        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlServer("Server=unreachable;Database=x;Connect Timeout=1;"));

        using var provider = services.BuildServiceProvider();
        var act = () => provider.GetRequiredService<AppDbContext>().Model;

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*database providers*");
    }

    [Fact]
    public void RemovingAllEfCoreServices_ThenSwitchingProvider_DoesNotThrowProviderConflict()
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseSqlite("Data Source=:memory:"));

        // The actual fix used in SqlServerQuotesApiFactory.ConfigureWebHost.
        var efCoreDescriptors = services
            .Where(d => d.ServiceType.Namespace is not null
                && d.ServiceType.Namespace.StartsWith("Microsoft.EntityFrameworkCore"))
            .ToList();
        foreach (var descriptor in efCoreDescriptors)
        {
            services.Remove(descriptor);
        }

        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlServer("Server=unreachable;Database=x;Connect Timeout=1;"));

        using var provider = services.BuildServiceProvider();
        var act = () => provider.GetRequiredService<AppDbContext>().Model;

        act.Should().NotThrow<InvalidOperationException>();
    }
}
