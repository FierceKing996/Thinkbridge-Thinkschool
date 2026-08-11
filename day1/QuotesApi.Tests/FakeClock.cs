using QuotesApi;

namespace QuotesApi.Tests;

public class FakeClock : IClock
{
    public DateTimeOffset UtcNow { get; set; }
}
