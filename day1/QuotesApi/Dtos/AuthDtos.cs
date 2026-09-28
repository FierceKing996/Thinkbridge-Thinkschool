using System.Text.Json.Serialization;

namespace QuotesApi.Dtos;

public record LoginRequest(string Email, string Password);
public record RegisterRequest(string Email, string Password);
public record RefreshRequest([property: JsonPropertyName("refresh_token")] string RefreshToken);
public record LogoutRequest([property: JsonPropertyName("refresh_token")] string RefreshToken);

public class LoginResponse
{
    [JsonPropertyName("access_token")]
    public required string AccessToken { get; init; }

    [JsonPropertyName("refresh_token")]
    public required string RefreshToken { get; init; }

    [JsonPropertyName("expires_in")]
    public required int ExpiresIn { get; init; }
}
