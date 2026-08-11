namespace QuotesApi;

public class RefreshToken
{
    public int Id { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public int UserId { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    // Stores the *hash* of the token that replaced this one, not the raw value -
    // raw refresh tokens are never persisted anywhere, only their hashes.
    public string? ReplacedByTokenHash { get; private set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && now < ExpiresAt;

    // EF Core materialization only.
    private RefreshToken() { }

    public static RefreshToken Create(string tokenHash, int userId, DateTimeOffset expiresAt) =>
        new() { TokenHash = tokenHash, UserId = userId, ExpiresAt = expiresAt };

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;

    // Rotation: this token dies at the same moment its replacement is born.
    public void MarkReplacedBy(string newTokenHash, DateTimeOffset now)
    {
        ReplacedByTokenHash = newTokenHash;
        RevokedAt = now;
    }
}
