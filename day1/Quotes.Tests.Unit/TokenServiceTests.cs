using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Options;
using NSubstitute;
using QuotesApi.Auth;
using QuotesApi.Data;
using QuotesApi.Dtos;
using QuotesApi.Models;
using QuotesApi.Repositories;
using QuotesApi.Services;
using Xunit;

namespace Quotes.Tests.Unit;

public class TokenServiceTests
{
    private static readonly JwtOptions Options = new()
    {
        Issuer = "test-issuer",
        Audience = "test-audience",
        SigningKey = Convert.ToBase64String(new byte[32]),
        AccessTokenExpirySeconds = 900
    };

    private static User MakeUser(string scopes = "") => new()
    {
        Id = 1,
        Email = "user@example.com",
        PasswordHash = "irrelevant-for-these-tests",
        Scopes = scopes
    };

    private static TokenService MakeSut(IClock clock) =>
        new(Microsoft.Extensions.Options.Options.Create(Options), clock);

    [Fact]
    public void CreateAccessToken_ValidUser_ReturnsConfiguredExpiresIn()
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var sut = MakeSut(clock);

        var (_, expiresIn) = sut.CreateAccessToken(MakeUser());

        expiresIn.Should().Be(900);
    }

    [Fact]
    public void CreateAccessToken_UsesInjectedClockForExpiryClaim()
    {
        var clock = Substitute.For<IClock>();
        var frozenNow = new DateTimeOffset(2030, 1, 1, 0, 0, 0, TimeSpan.Zero);
        clock.UtcNow.Returns(frozenNow);
        var sut = MakeSut(clock);

        var (accessToken, _) = sut.CreateAccessToken(MakeUser());
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);

        jwt.ValidTo.Should().Be(frozenNow.UtcDateTime.AddSeconds(900));
    }

    [Theory]
    [InlineData("quotes.write", "quotes.write")]
    [InlineData("quotes.write quotes.read", "quotes.write")]
    [InlineData("quotes.write quotes.read", "quotes.read")]
    public void CreateAccessToken_MintsAClaimForEachGrantedScope(string userScopes, string expectedScope)
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        var sut = MakeSut(clock);

        var (accessToken, _) = sut.CreateAccessToken(MakeUser(userScopes));
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);

        jwt.Claims.Where(c => c.Type == "scope").Select(c => c.Value).Should().Contain(expectedScope);
    }

    [Fact]
    public void CreateAccessToken_NoScopes_MintsNoScopeClaims()
    {
        var clock = Substitute.For<IClock>();
        clock.UtcNow.Returns(DateTimeOffset.UtcNow);
        var sut = MakeSut(clock);

        var (accessToken, _) = sut.CreateAccessToken(MakeUser(scopes: ""));
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(accessToken);

        jwt.Claims.Where(c => c.Type == "scope").Should().BeEmpty();
    }

    [Fact]
    public void CreateRefreshToken_ReturnsNonEmptyOpaqueString()
    {
        var sut = MakeSut(Substitute.For<IClock>());

        var token = sut.CreateRefreshToken();

        token.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void HashRefreshToken_SameInput_ProducesSameHash()
    {
        var sut = MakeSut(Substitute.For<IClock>());

        var hash1 = sut.HashRefreshToken("some-token");
        var hash2 = sut.HashRefreshToken("some-token");

        hash1.Should().Be(hash2);
    }

    [Fact]
    public void HashRefreshToken_DifferentInput_ProducesDifferentHash()
    {
        var sut = MakeSut(Substitute.For<IClock>());

        var hash1 = sut.HashRefreshToken("token-a");
        var hash2 = sut.HashRefreshToken("token-b");

        hash1.Should().NotBe(hash2);
    }
}
