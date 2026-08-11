namespace QuotesApi;

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

public class Quote
{
    public const int MinAuthorLength = 1;
    public const int MaxAuthorLength = 200;
    public const int MinTextLength = 1;
    public const int MaxTextLength = 1000;

    public int Id { get; private set; }
    public string Author { get; private set; } = string.Empty;
    public string Text { get; private set; } = string.Empty;
    public bool IsDeleted { get; private set; }

    // EF Core materialization only. Application code must go through Create().
    private Quote() { }

    public static QuoteCreationResult Create(string author, string text)
    {
        if (string.IsNullOrWhiteSpace(author) || author.Length > MaxAuthorLength)
        {
            return QuoteCreationResult.Fail(
                $"Author must be between {MinAuthorLength} and {MaxAuthorLength} characters.");
        }

        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxTextLength)
        {
            return QuoteCreationResult.Fail(
                $"Text must be between {MinTextLength} and {MaxTextLength} characters.");
        }

        return QuoteCreationResult.Success(new Quote { Author = author, Text = text });
    }

    // No Rename/UpdateText method exists on purpose - Author and Text are set only
    // inside Create and never again anywhere in this class. The only state change
    // available after construction is soft-deletion.
    public void Delete() => IsDeleted = true;
}
