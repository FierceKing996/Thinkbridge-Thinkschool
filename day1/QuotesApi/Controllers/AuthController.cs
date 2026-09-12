using Microsoft.AspNetCore.Mvc;
using QuotesApi.Dtos;
using QuotesApi.Services;

namespace QuotesApi.Controllers;

[ApiController]
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
