namespace QuotesApi.Auth;

public class JwtOptions
{
    public const string SectionName = "Jwt";

    public required string Issuer { get; set; }
    public required string Audience { get; set; }

    // Base64-encoded, must decode to >= 32 bytes (256 bits) for HS256.
    public required string SigningKey { get; set; }

    public int AccessTokenExpirySeconds { get; set; } = 900;
}
