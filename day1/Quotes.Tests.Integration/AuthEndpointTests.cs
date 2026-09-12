using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using QuotesApi.Auth;
using QuotesApi.Data;
using QuotesApi.Dtos;
using QuotesApi.Models;
using QuotesApi.Repositories;
using QuotesApi.Services;
using Xunit;

namespace Quotes.Tests.Integration;

public class AuthEndpointTests : IntegrationTestBase
{
    [Fact]
    public async Task Login_ValidCredentials_ReturnsOkWithTokenPair()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("demo@quotesapi.dev", "correct-horse-battery-staple"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var tokens = await response.Content.ReadFromJsonAsync<LoginResponse>();
        tokens!.AccessToken.Should().NotBeNullOrWhiteSpace();
        tokens.RefreshToken.Should().NotBeNullOrWhiteSpace();
        tokens.ExpiresIn.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Login_WrongPassword_ReturnsUnauthorized()
    {
        var response = await Client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("demo@quotesapi.dev", "not-the-password"));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Refresh_ValidToken_ReturnsOkWithNewTokenPair()
    {
        var loginResponse = await Client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("demo@quotesapi.dev", "correct-horse-battery-staple"));
        var originalTokens = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>();

        var response = await Client.PostAsJsonAsync(
            "/api/auth/refresh", new RefreshRequest(originalTokens!.RefreshToken));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var newTokens = await response.Content.ReadFromJsonAsync<LoginResponse>();
        newTokens!.RefreshToken.Should().NotBe(originalTokens.RefreshToken);
    }

    [Fact]
    public async Task Refresh_ReusedToken_ReturnsUnauthorized()
    {
        var loginResponse = await Client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("demo@quotesapi.dev", "correct-horse-battery-staple"));
        var tokens = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>();

        await Client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(tokens!.RefreshToken));
        var reuseAttempt = await Client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(tokens.RefreshToken));

        reuseAttempt.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Logout_ValidToken_ReturnsNoContentAndInvalidatesIt()
    {
        var loginResponse = await Client.PostAsJsonAsync(
            "/api/auth/login", new LoginRequest("demo@quotesapi.dev", "correct-horse-battery-staple"));
        var tokens = await loginResponse.Content.ReadFromJsonAsync<LoginResponse>();

        var logoutResponse = await Client.PostAsJsonAsync("/api/auth/logout", new LogoutRequest(tokens!.RefreshToken));
        var refreshAfterLogout = await Client.PostAsJsonAsync("/api/auth/refresh", new RefreshRequest(tokens.RefreshToken));

        logoutResponse.StatusCode.Should().Be(HttpStatusCode.NoContent);
        refreshAfterLogout.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
