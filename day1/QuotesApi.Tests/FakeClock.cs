using QuotesApi.Auth;
using QuotesApi.Data;
using QuotesApi.Dtos;
using QuotesApi.Models;
using QuotesApi.Repositories;
using QuotesApi.Services;

namespace QuotesApi.Tests;

public class FakeClock : IClock
{
    // Defaults to real time, not year 1: TokenService now mints JWT "exp" claims
    // from this clock, and real-time validation (JwtBearerHandler has no idea
    // this clock exists) would reject every token as already-expired if this
    // defaulted to DateTimeOffset's zero value. Tests that care about a specific
    // instant still override it explicitly.
    public DateTimeOffset UtcNow { get; set; } = DateTimeOffset.UtcNow;
}
