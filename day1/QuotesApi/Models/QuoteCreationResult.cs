namespace QuotesApi.Models;

public class QuoteCreationResult
{
    public Quote? Quote { get; }
    public string? Error { get; }
    public bool Succeeded => Error is null;

    private QuoteCreationResult(Quote? quote, string? error)
    {
        Quote = quote;
        Error = error;
    }

    public static QuoteCreationResult Success(Quote quote) => new(quote, null);
    public static QuoteCreationResult Fail(string error) => new(null, error);
}
