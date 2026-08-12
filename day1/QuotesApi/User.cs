namespace QuotesApi;

// No invariants or factory here on purpose - this exercise is about wiring auth,
// not about modeling User as a rich aggregate. Plain properties, matching what
// the original anemic Quote looked like before that exercise.
public class User
{
    public int Id { get; set; }
    public required string Email { get; set; }
    public required string PasswordHash { get; set; }

    // Space-separated, e.g. "quotes.write quotes.read" - minted as one JWT "scope"
    // claim per entry, matching RequireClaim("scope", "quotes.write")'s expectation
    // of one claim per value rather than a single combined string.
    public string Scopes { get; set; } = string.Empty;
}
