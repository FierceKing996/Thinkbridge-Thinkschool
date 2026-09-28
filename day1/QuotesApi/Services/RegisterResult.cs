using QuotesApi.Dtos;

namespace QuotesApi.Services;

// Mirrors RefreshResult's shape (Tokens xor Error) - same reason: the
// controller needs to distinguish "email already taken" (409) from other
// input problems (400) from success (200 + token pair), and a bool plus a
// nullable payload doesn't carry enough information to do that.
public class RegisterResult
{
    public LoginResponse? Tokens { get; }
    public string? Error { get; }
    public bool Succeeded => Error is null;

    private RegisterResult(LoginResponse? tokens, string? error)
    {
        Tokens = tokens;
        Error = error;
    }

    public static RegisterResult Success(LoginResponse tokens) => new(tokens, null);
    public static RegisterResult Fail(string error) => new(null, error);
}
