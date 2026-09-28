using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using QuotesApi.Auth;
using QuotesApi.Data;
using QuotesApi.Dtos;
using QuotesApi.Models;
using QuotesApi.Repositories;
using QuotesApi.Services;
using Xunit;

namespace Quotes.Tests.Unit;

public class AuthServiceTests
{
    private readonly IUserRepository _users = Substitute.For<IUserRepository>();
    private readonly IRefreshTokenRepository _refreshTokens = Substitute.For<IRefreshTokenRepository>();
    private readonly ITokenService _tokenService = Substitute.For<ITokenService>();
    private readonly IClock _clock = Substitute.For<IClock>();
    private readonly ILogger<AuthService> _logger = Substitute.For<ILogger<AuthService>>();

    private AuthService MakeSut() => new(_users, _refreshTokens, _tokenService, _clock, _logger);

    private static User MakeUser(int id = 1, string password = "correct-password") => new()
    {
        Id = id,
        Email = "user@example.com",
        PasswordHash = BCrypt.Net.BCrypt.HashPassword(password),
        Scopes = "quotes.write"
    };

    [Fact]
    public async Task LoginAsync_ValidCredentials_ReturnsTokenPairAndPersistsRefreshToken()
    {
        var user = MakeUser();
        _users.GetByEmailAsync(user.Email, Arg.Any<CancellationToken>()).Returns(user);
        _tokenService.CreateAccessToken(user).Returns(("access-token-x", 900));
        _tokenService.CreateRefreshToken().Returns("raw-refresh-token");
        _tokenService.HashRefreshToken("raw-refresh-token").Returns("hashed-refresh-token");
        _clock.UtcNow.Returns(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var sut = MakeSut();

        var result = await sut.LoginAsync(user.Email, "correct-password", CancellationToken.None);

        result.Should().NotBeNull();
        result!.AccessToken.Should().Be("access-token-x");
        result.RefreshToken.Should().Be("raw-refresh-token");
        await _refreshTokens.Received(1).AddAsync(
            Arg.Is<RefreshToken>(t => t.TokenHash == "hashed-refresh-token" && t.UserId == user.Id),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoginAsync_WrongPassword_ReturnsNull()
    {
        var user = MakeUser();
        _users.GetByEmailAsync(user.Email, Arg.Any<CancellationToken>()).Returns(user);
        var sut = MakeSut();

        var result = await sut.LoginAsync(user.Email, "wrong-password", CancellationToken.None);

        result.Should().BeNull();
        await _refreshTokens.DidNotReceive().AddAsync(Arg.Any<RefreshToken>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LoginAsync_UnknownEmail_ReturnsNull()
    {
        _users.GetByEmailAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((User?)null);
        var sut = MakeSut();

        var result = await sut.LoginAsync("nobody@example.com", "whatever", CancellationToken.None);

        result.Should().BeNull();
    }

    [Fact]
    public async Task RegisterAsync_NewEmail_CreatesUserAndReturnsTokenPair()
    {
        _users.GetByEmailAsync("new@example.com", Arg.Any<CancellationToken>()).Returns((User?)null);
        _tokenService.CreateAccessToken(Arg.Any<User>()).Returns(("access-token-x", 900));
        _tokenService.CreateRefreshToken().Returns("raw-refresh-token");
        _tokenService.HashRefreshToken("raw-refresh-token").Returns("hashed-refresh-token");
        _clock.UtcNow.Returns(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var sut = MakeSut();

        var result = await sut.RegisterAsync("new@example.com", "a-strong-password", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Tokens!.AccessToken.Should().Be("access-token-x");
        result.Tokens.RefreshToken.Should().Be("raw-refresh-token");
        await _users.Received(1).AddAsync(
            Arg.Is<User>(u => u.Email == "new@example.com" && u.Scopes == "quotes.write"),
            Arg.Any<CancellationToken>());
        await _users.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RegisterAsync_EmailAlreadyRegistered_ReturnsFailureAndDoesNotCreateUser()
    {
        var existing = MakeUser();
        _users.GetByEmailAsync(existing.Email, Arg.Any<CancellationToken>()).Returns(existing);
        var sut = MakeSut();

        var result = await sut.RegisterAsync(existing.Email, "a-strong-password", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("email-taken");
        await _users.DidNotReceive().AddAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("not-an-email")]
    [InlineData("")]
    public async Task RegisterAsync_InvalidEmail_ReturnsFailureAndDoesNotCreateUser(string email)
    {
        var sut = MakeSut();

        var result = await sut.RegisterAsync(email, "a-strong-password", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("invalid-email");
        await _users.DidNotReceive().AddAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RegisterAsync_PasswordTooShort_ReturnsFailureAndDoesNotCreateUser()
    {
        _users.GetByEmailAsync("new@example.com", Arg.Any<CancellationToken>()).Returns((User?)null);
        var sut = MakeSut();

        var result = await sut.RegisterAsync("new@example.com", "short", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.Error.Should().Be("weak-password");
        await _users.DidNotReceive().AddAsync(Arg.Any<User>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshAsync_TokenNotFound_ReturnsFailure()
    {
        _tokenService.HashRefreshToken("unknown-token").Returns("unknown-hash");
        _refreshTokens.GetByTokenHashAsync("unknown-hash", Arg.Any<CancellationToken>()).Returns((RefreshToken?)null);
        var sut = MakeSut();

        var result = await sut.RefreshAsync("unknown-token", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task RefreshAsync_ExpiredToken_ReturnsFailure()
    {
        var now = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var expired = RefreshToken.Create("hash-expired", userId: 1, expiresAt: now.AddDays(-1));
        _clock.UtcNow.Returns(now);
        _tokenService.HashRefreshToken("expired-token").Returns("hash-expired");
        _refreshTokens.GetByTokenHashAsync("hash-expired", Arg.Any<CancellationToken>()).Returns(expired);
        var sut = MakeSut();

        var result = await sut.RefreshAsync("expired-token", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task RefreshAsync_LoggedOutToken_ReturnsFailure()
    {
        var now = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var revoked = RefreshToken.Create("hash-revoked", userId: 1, expiresAt: now.AddDays(7));
        revoked.Revoke(now); // simulates a prior logout, not a rotation
        _clock.UtcNow.Returns(now);
        _tokenService.HashRefreshToken("revoked-token").Returns("hash-revoked");
        _refreshTokens.GetByTokenHashAsync("hash-revoked", Arg.Any<CancellationToken>()).Returns(revoked);
        var sut = MakeSut();

        var result = await sut.RefreshAsync("revoked-token", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task RefreshAsync_ValidActiveToken_ReturnsSuccessAndRotatesIt()
    {
        var now = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var user = MakeUser();
        var active = RefreshToken.Create("hash-active", user.Id, expiresAt: now.AddDays(7));
        _clock.UtcNow.Returns(now);
        _tokenService.HashRefreshToken("active-token").Returns("hash-active");
        _tokenService.HashRefreshToken("new-raw-token").Returns("hash-new");
        _refreshTokens.GetByTokenHashAsync("hash-active", Arg.Any<CancellationToken>()).Returns(active);
        _users.GetByIdAsync(user.Id, Arg.Any<CancellationToken>()).Returns(user);
        _tokenService.CreateAccessToken(user).Returns(("new-access-token", 900));
        _tokenService.CreateRefreshToken().Returns("new-raw-token");
        var sut = MakeSut();

        var result = await sut.RefreshAsync("active-token", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.Tokens!.RefreshToken.Should().Be("new-raw-token");
        active.ReplacedByTokenHash.Should().Be("hash-new");
    }

    // The core behavior this exercise asks for: presenting an already-rotated
    // token must kill every token descended from it, not just reject the replay.
    [Fact]
    public async Task RefreshAsync_ReusedToken_RevokesEntireDescendantChain()
    {
        var now = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        _clock.UtcNow.Returns(now);

        // Chain: tokenA -> tokenB -> tokenC (tokenC is the current, still-active leaf)
        var tokenC = RefreshToken.Create("hash-c", userId: 1, expiresAt: now.AddDays(7));
        var tokenB = RefreshToken.Create("hash-b", userId: 1, expiresAt: now.AddDays(7));
        tokenB.MarkReplacedBy("hash-c", now.AddMinutes(-10));
        var tokenA = RefreshToken.Create("hash-a", userId: 1, expiresAt: now.AddDays(7));
        tokenA.MarkReplacedBy("hash-b", now.AddMinutes(-20));

        _tokenService.HashRefreshToken("stolen-token-a").Returns("hash-a");
        _refreshTokens.GetByTokenHashAsync("hash-a", Arg.Any<CancellationToken>()).Returns(tokenA);
        _refreshTokens.GetByTokenHashAsync("hash-b", Arg.Any<CancellationToken>()).Returns(tokenB);
        _refreshTokens.GetByTokenHashAsync("hash-c", Arg.Any<CancellationToken>()).Returns(tokenC);
        var sut = MakeSut();

        var result = await sut.RefreshAsync("stolen-token-a", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        tokenB.RevokedAt.Should().NotBeNull("tokenB descends from the compromised tokenA");
        tokenC.RevokedAt.Should().NotBeNull("tokenC is the live leaf of the compromised chain");
        await _refreshTokens.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RefreshAsync_ReusedToken_DoesNotIssueNewTokens()
    {
        var now = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        _clock.UtcNow.Returns(now);
        var tokenA = RefreshToken.Create("hash-a", userId: 1, expiresAt: now.AddDays(7));
        tokenA.MarkReplacedBy("hash-b", now.AddMinutes(-20));
        _tokenService.HashRefreshToken("stolen-token-a").Returns("hash-a");
        _refreshTokens.GetByTokenHashAsync("hash-a", Arg.Any<CancellationToken>()).Returns(tokenA);
        _refreshTokens.GetByTokenHashAsync("hash-b", Arg.Any<CancellationToken>()).Returns((RefreshToken?)null);
        var sut = MakeSut();

        await sut.RefreshAsync("stolen-token-a", CancellationToken.None);

        _tokenService.DidNotReceive().CreateAccessToken(Arg.Any<User>());
    }

    [Fact]
    public async Task LogoutAsync_ActiveToken_RevokesIt()
    {
        var now = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var active = RefreshToken.Create("hash-active", userId: 1, expiresAt: now.AddDays(7));
        _clock.UtcNow.Returns(now);
        _tokenService.HashRefreshToken("active-token").Returns("hash-active");
        _refreshTokens.GetByTokenHashAsync("hash-active", Arg.Any<CancellationToken>()).Returns(active);
        var sut = MakeSut();

        await sut.LogoutAsync("active-token", CancellationToken.None);

        active.RevokedAt.Should().Be(now);
        await _refreshTokens.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task LogoutAsync_UnknownToken_DoesNothingSilently()
    {
        _tokenService.HashRefreshToken("unknown-token").Returns("unknown-hash");
        _refreshTokens.GetByTokenHashAsync("unknown-hash", Arg.Any<CancellationToken>()).Returns((RefreshToken?)null);
        var sut = MakeSut();

        var act = () => sut.LogoutAsync("unknown-token", CancellationToken.None);

        await act.Should().NotThrowAsync();
        await _refreshTokens.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
