using Microsoft.AspNetCore.Mvc;
using QuotesApi.Dtos;
using QuotesApi.Services;

namespace QuotesApi.Controllers;

[ApiController]
[Asp.Versioning.ApiVersion("1.0")]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly IAuthService _auth;

    public AuthController(IAuthService auth)
    {
        _auth = auth;
    }

    // POST /api/auth/login
    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] LoginRequest req, CancellationToken ct)
    {
        var response = await _auth.LoginAsync(req.Email, req.Password, ct);
        return response is not null ? Ok(response) : Unauthorized();
    }

    // POST /api/auth/register
    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] RegisterRequest req, CancellationToken ct)
    {
        var result = await _auth.RegisterAsync(req.Email, req.Password, ct);
        if (result.Succeeded)
        {
            // 200, not 201: there's no GET /api/auth/users/{id} to point a
            // Location header at, and the body IS the useful thing here (the
            // token pair) - same shape POST /api/auth/login returns, so the
            // frontend's Auth service can treat register-then-login as one
            // response type.
            return Ok(result.Tokens);
        }

        // "email-taken" gets its own real status (409) with a real detail
        // message the frontend can show verbatim - unlike login's
        // deliberately uniform 401 (§3.4's reasoning doesn't apply here:
        // there's no reuse-detection-style attacker signal being protected,
        // just "someone already has this email").
        if (result.Error == "email-taken")
        {
            return Problem(
                detail: "An account with that email already exists.",
                statusCode: StatusCodes.Status409Conflict);
        }

        // Everything else is a plain input problem - same
        // ValidationProblemDetails shape (errors.error[0]) the quotes
        // endpoints use, so the frontend's existing extractValidationMessage
        // handles it with no new code path.
        ModelState.AddModelError("error", result.Error switch
        {
            "invalid-email" => "Enter a valid email address.",
            "weak-password" => "Password must be at least 8 characters long.",
            _ => "Registration could not be completed."
        });
        return ValidationProblem(ModelState);
    }

    // POST /api/auth/refresh
    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh([FromBody] RefreshRequest req, CancellationToken ct)
    {
        var result = await _auth.RefreshAsync(req.RefreshToken, ct);
        // Deliberately uniform 401 for every failure reason (invalid, expired,
        // reuse-detected) - the specific reason is logged server-side only, so
        // a client probing this endpoint can't distinguish "wrong token" from
        // "stolen token that tripped reuse detection".
        return result.Succeeded ? Ok(result.Tokens) : Unauthorized();
    }

    // POST /api/auth/logout
    [HttpPost("logout")]
    public async Task<IActionResult> Logout([FromBody] LogoutRequest req, CancellationToken ct)
    {
        await _auth.LogoutAsync(req.RefreshToken, ct);
        return NoContent();
    }
}
