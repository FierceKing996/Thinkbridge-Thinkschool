using QuotesApi.Auth;
using QuotesApi.Data;
using QuotesApi.Dtos;
using QuotesApi.Models;
using QuotesApi.Repositories;
using QuotesApi.Services;

namespace Quotes.Tests.Integration;

public class FakeClock : IClock
{
    // Defaults to real time - TokenService mints JWT "exp" claims from this clock,
    // and real-time validation would reject every token as already-expired if
    // this defaulted to DateTimeOffset's zero value (year 1).
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
}
