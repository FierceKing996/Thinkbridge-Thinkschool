namespace QuotesApi;

// No invariants or factory here on purpose - this exercise is about wiring auth,
// not about modeling User as a rich aggregate. Plain properties, matching what
// the original anemic Quote looked like before that exercise.
public class User
{
    public int Id { get; set; }
    public required string Email { get; set; }
    public required string PasswordHash { get; set; }
}
