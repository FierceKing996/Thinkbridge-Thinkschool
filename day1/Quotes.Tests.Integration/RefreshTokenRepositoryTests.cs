using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using QuotesApi.Auth;
using QuotesApi.Data;
using QuotesApi.Dtos;
using QuotesApi.Models;
using QuotesApi.Repositories;
using QuotesApi.Services;
using Xunit;

namespace Quotes.Tests.Integration;

// Closes the last real gap: RefreshToken.Id is a database-generated primary
// key, never explicitly read anywhere else (the API only ever hands clients
// an opaque hashed token string, never the entity's Id). A real round trip
// through the repository is the only way to meaningfully exercise it - and
// it's a genuine regression guard, not a token gesture: if the primary key
// mapping ever broke (e.g. accidentally configured ValueGeneratedNever), this
// test would catch it instead of a fake token still working by coincidence.
public class RefreshTokenRepositoryTests
{
    [Fact]
    public async Task AddAsync_ThenSaveChanges_AssignsADatabaseGeneratedId()
    {
        using var connection = new SqliteConnection("DataSource=:memory:");
        connection.Open();

        var options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        await using var db = new AppDbContext(options);
        await db.Database.EnsureCreatedAsync();

        var user = new User { Email = "test@example.com", PasswordHash = "hash" };
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var repository = new RefreshTokenRepository(db);
        var token = RefreshToken.Create("some-hash", user.Id, DateTimeOffset.UtcNow.AddDays(7));

        await repository.AddAsync(token, CancellationToken.None);
        await repository.SaveChangesAsync(CancellationToken.None);

        token.Id.Should().BeGreaterThan(0);
    }
}
