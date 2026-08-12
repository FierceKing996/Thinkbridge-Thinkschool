using Testcontainers.MsSql;
using Xunit;

namespace Quotes.Tests.Integration;

// One SQL Server 2022 container for the entire "SqlServer" test collection -
// started once before any test in the collection runs, torn down once after
// the last one finishes. This is deliberately NOT "one container per test":
// pulling/starting SQL Server costs seconds, and the task only asks for real
// SQL Server to catch provider-specific bugs, not per-test database isolation.
// Individual tests are responsible for using their own distinguishable data
// rather than assuming an empty database.
public class SqlServerContainerFixture : IAsyncLifetime
{
    private readonly MsSqlContainer _container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();

    public string ConnectionString => _container.GetConnectionString();

    public Task InitializeAsync() => _container.StartAsync();

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}

[CollectionDefinition("SqlServer collection")]
public class SqlServerCollection : ICollectionFixture<SqlServerContainerFixture>;
