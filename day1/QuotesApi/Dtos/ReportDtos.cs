namespace QuotesApi.Dtos;

public record AuthorSummary(string Author, int QuoteCount, string? MostRecentQuoteText);
