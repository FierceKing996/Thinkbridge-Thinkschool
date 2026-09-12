using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using QuotesApi.Dtos;
using QuotesApi.Models;
using QuotesApi.Repositories;
using QuotesApi.Services;

namespace QuotesApi.Controllers;

[ApiController]
[Route("api/quotes")]
public class QuotesController : ApiControllerBase
{
    private readonly IQuoteRepository _quotes;
    private readonly ITextNormalizer _normalizer;
    private readonly IAuthorizationService _authorization;

    public QuotesController(
        IQuoteRepository quotes,
        ITextNormalizer normalizer,
        IAuthorizationService authorization)
    {
        _quotes = quotes;
        _normalizer = normalizer;
        _authorization = authorization;
    }

    // GET /api/quotes?page=N&size=N
    [HttpGet]
    public async Task<IActionResult> GetQuotes([FromQuery] int? page, [FromQuery] int? size, CancellationToken ct)
    {
        var p = page is > 0 ? page.Value : 1;
        var s = size is > 0 ? size.Value : 10;
        return Ok(await _quotes.GetPagedAsync(p, s, ct));
    }

    // GET /api/quotes/{id}
    [HttpGet("{id:int}")]
    public async Task<IActionResult> GetQuote(int id, CancellationToken ct)
    {
        var quote = await _quotes.GetByIdAsync(id, ct);
        return quote is not null ? Ok(quote) : NotFound();
    }

    // POST /api/quotes
    [HttpPost]
    [Authorize(Policy = "can-edit-quotes")]
    public async Task<IActionResult> CreateQuote([FromBody] CreateQuoteRequest req, CancellationToken ct)
    {
        var userId = int.Parse(User.FindFirstValue(JwtRegisteredClaimNames.Sub)!);

        // All invariant checking (length limits, etc.) lives on the aggregate itself.
        var result = Quote.Create(_normalizer.Trim(req.Author), _normalizer.Trim(req.Text), userId);
        if (!result.Succeeded)
        {
            return ValidationProblemResponse("error", result.Error!);
        }

        var created = await _quotes.CreateAsync(result.Quote!, ct);
        return Created($"/api/quotes/{created.Id}", created);
    }

    // DELETE /api/quotes/{id}
    [HttpDelete("{id:int}")]
    [Authorize]
    public async Task<IActionResult> DeleteQuote(int id, CancellationToken ct)
    {
        var quote = await _quotes.GetByIdAsync(id, ct);
        if (quote is null) return NotFound();

        // Resource-based checks can't be expressed via [Authorize] on the action -
        // there's no specific Quote to check against until it's been fetched, so
        // this has to be an explicit, imperative call.
        var authResult = await _authorization.AuthorizeAsync(User, quote, "can-delete-own-quote");
        if (!authResult.Succeeded)
        {
            return Forbid();
        }

        var deleted = await _quotes.DeleteAsync(id, ct);
        return deleted ? NoContent() : NotFound();
    }
}
