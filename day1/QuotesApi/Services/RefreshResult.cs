using QuotesApi.Dtos;

namespace QuotesApi.Services;

public class RefreshResult
{
    public LoginResponse? Tokens { get; }
    public string? Error { get; }
    public bool Succeeded => Error is null;

    private RefreshResult(LoginResponse? tokens, string? error)
    {
        Tokens = tokens;
        Error = error;
    }

    public static RefreshResult Success(LoginResponse tokens) => new(tokens, null);
    public static RefreshResult Fail(string error) => new(null, error);
}
