using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.IdentityModel.Tokens;
using QuotesApi;
using Xunit;

namespace Quotes.Tests.Unit;

// Covers Extensions.SelectScheme directly - the routing logic that decides
// whether an incoming bearer token should be validated against the internal
// HS256 scheme or Entra. This was previously only reachable through a full
// HTTP round trip against a real Entra tenant, which this project has no
// access to - it never ran in any test. Testing it directly needed only one
// change: making the method internal instead of private.
public class SelectSchemeTests
{
    private static readonly EntraOptions FakeEntraOptions = new() { TenantId = "test-tenant", Audience = "test-audience" };

    private static string BuildToken(string issuer)
    {
        var key = new SymmetricSecurityKey(RandomNumberGenerator.GetBytes(32));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var token = new JwtSecurityToken(
            issuer: issuer,
            claims: [new Claim(JwtRegisteredClaimNames.Sub, "1")],
            expires: DateTime.UtcNow.AddMinutes(5),
            signingCredentials: credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    private static HttpContext ContextWithAuthHeader(string? headerValue)
    {
        var context = new DefaultHttpContext();
        if (headerValue is not null)
        {
            context.Request.Headers["Authorization"] = headerValue;
        }
        return context;
    }

    [Fact]
    public void EntraNotConfigured_ReturnsInternal_EvenWithAnEntraLookingToken()
    {
        var context = ContextWithAuthHeader($"Bearer {BuildToken("https://login.microsoftonline.com/tenant/v2.0")}");

        var scheme = Extensions.SelectScheme(context, entraOptions: null);

        scheme.Should().Be("Internal");
    }

    [Fact]
    public void NoAuthorizationHeader_ReturnsInternal()
    {
        var context = ContextWithAuthHeader(null);

        var scheme = Extensions.SelectScheme(context, FakeEntraOptions);

        scheme.Should().Be("Internal");
    }

    [Theory]
    [InlineData("NotBearer sometoken")]
    [InlineData("bearertoken")]
    public void HeaderNotBearerPrefixed_ReturnsInternal(string headerValue)
    {
        var context = ContextWithAuthHeader(headerValue);

        var scheme = Extensions.SelectScheme(context, FakeEntraOptions);

        scheme.Should().Be("Internal");
    }

    [Fact]
    public void MalformedBearerToken_ReturnsInternal()
    {
        var context = ContextWithAuthHeader("Bearer not-a-real-jwt");

        var scheme = Extensions.SelectScheme(context, FakeEntraOptions);

        scheme.Should().Be("Internal");
    }

    [Fact]
    public void WellFormedTokenWithNonMicrosoftIssuer_ReturnsInternal()
    {
        var context = ContextWithAuthHeader($"Bearer {BuildToken("https://my-own-issuer")}");

        var scheme = Extensions.SelectScheme(context, FakeEntraOptions);

        scheme.Should().Be("Internal");
    }

    [Fact]
    public void TokenWithMicrosoftOnlineIssuer_ReturnsEntra()
    {
        var context = ContextWithAuthHeader($"Bearer {BuildToken("https://login.microsoftonline.com/some-tenant/v2.0")}");

        var scheme = Extensions.SelectScheme(context, FakeEntraOptions);

        scheme.Should().Be("Entra");
    }
}
