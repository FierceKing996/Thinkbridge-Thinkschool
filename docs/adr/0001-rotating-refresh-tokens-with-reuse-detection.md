# ADR-0001: Short-lived JWT access tokens with rotating, single-use refresh tokens

- **Status:** Accepted (recorded retroactively from the implemented code)
- **Date:** 2026-09-29
- **Deciders:** Project owner (confirm)
- **Scope:** Authentication for `quotes-ui` (Angular) and `day1/QuotesApi` (ASP.NET Core)

## Context

The app has public read endpoints (`GET /api/quotes`, `GET /api/collections/{id}`) and protected write endpoints. `POST /api/quotes` needs the `quotes.write` scope, and `DELETE /api/quotes/{id}` is limited to the quote's owner. The client is a browser SPA, so anything it stores can be read by script running on the page.

The design has to satisfy four constraints:

1. A stolen credential should do limited damage.
2. Users should not have to re-enter a password every few minutes.
3. Checking a request's identity should not need a database call on every request.
4. A leaked long-lived credential should be detectable, not just survivable.

## Options considered

| # | Option | Why it was not chosen |
|---|---|---|
| 1 | One long-lived JWT | A stolen token works until it expires and the server cannot cancel it. |
| 2 | Server-side session with a cookie | Needs a session store lookup on every request, plus CSRF handling. Conflicts with the stateless-API goal. |
| 3 | Short JWT + opaque refresh token, reused until expiry | Solves re-login, but a leaked refresh token stays valid for its whole life and nothing signals the theft. |
| 4 | **Short JWT + opaque refresh token that rotates on every use, with reuse detection** | **Chosen.** |

## Decision

**Access token.** A signed JWT (HS256) carrying `sub`, `email`, `jti` and one `scope` claim per permission (`TokenService.CreateAccessToken`). It is validated on every protected request by the JwtBearer middleware: signature, issuer, audience and lifetime, with `ClockSkew = 0`. There is no database lookup. It is sent as `Authorization: Bearer ...` on non-GET requests only.

**Refresh token.**
- 32 random bytes, base64, opaque (not a JWT).
- Stored only as an unsalted SHA-256 hash in `RefreshTokens` (`TokenHash`, unique index). The raw value exists only in the login/refresh response.
- Lifetime is 7 days, restarted at each rotation (`AuthService.RefreshTokenLifetime`).
- Sent only to `POST /api/auth/refresh` and `POST /api/auth/logout`, in the JSON body.

**Rotation.** Every successful refresh issues a new access and refresh token. The old row gets `ReplacedByTokenHash` and `RevokedAt`, and the new row is inserted in the same `SaveChanges`.

**Reuse detection.** If a presented token already has `ReplacedByTokenHash`, it has been used before. The server logs a warning and revokes every descendant token (`RevokeChainAsync`), which forces a full login for whoever holds the live token.

**Uniform failure response.** `invalid`, `reuse-detected` and `expired-or-revoked` all return the same empty 401. The specific reason is only written to the server log.

**Client behavior** (`auth.ts`, `auth-interceptor.ts`):
- Tokens live in signals mirrored to `sessionStorage`.
- On a 401 for a request that carried a token, the interceptor makes one refresh attempt and replays the request once.
- Concurrent 401s share a single refresh call (`refreshInFlight$` + `shareReplay(1)`). Two parallel refreshes would present the same token twice, which the server would read as theft.
- If the refresh fails, local tokens are cleared and the original 401 is what the caller sees.
- `logout()` clears local state first, then revokes the refresh token server-side on a best-effort basis.

## Consequences

### Positive

- A stolen access token is useful only until it expires.
- Request authentication is a CPU-only signature check.
- A refresh token that leaks and is then used by both the thief and the real user is caught on the second use, and the whole chain is killed.
- Tokens in the database are hashes, so a database leak does not yield usable refresh tokens.
- Unsalted SHA-256 is safe here because the input is 256 bits of randomness, and it allows lookup by exact hash. A salted hash such as BCrypt would force a table scan.
- Clients cannot tell a wrong token from a stolen one.

### Negative and risks

- **Access tokens cannot be revoked early.** Logout and scope changes only take effect when the current JWT expires.
- **Tokens are readable by page script.** `sessionStorage` exposes both tokens to any XSS bug. Moving them to `HttpOnly` cookies would change this and bring CSRF handling with it.
- **Sessions can last indefinitely.** Each rotation grants a fresh 7 days, and I found no absolute session cap in the files read. An active client never has to log in again.
- **Rotation has a check-then-act race.** `RefreshAsync` reads the token, checks `ReplacedByTokenHash`, then writes. Nothing (transaction, concurrency token) stops two simultaneous requests with the same token from both passing the check and each issuing a child token. The browser client avoids this by coalescing, but a direct API caller does not.
- **Logged-out tokens are not treated as theft.** Logout sets `RevokedAt` without `ReplacedByTokenHash`, so replaying such a token returns `expired-or-revoked`, not reuse, and no chain revocation happens.
- **Chain revocation walks one row at a time.** `RevokeChainAsync` follows `ReplacedByTokenHash` forward with one query per hop. There is no family id.
- **Row growth.** Rotated and revoked rows are kept. I did not find a cleanup job in the files read.
- **Deleted users cause a 500.** `RefreshAsync` throws `InvalidOperationException` if the token's user no longer exists.
- **Login timing leaks account existence.** An unknown email returns before BCrypt runs, a wrong password does not.

## Revisit when

- The app stores anything more sensitive than quotes, or XSS risk becomes a concern (consider cookies or a backend-for-frontend).
- Refresh is called by anything other than the single browser client.
- A requirement appears to sign a user out everywhere immediately (needs either very short JWTs or a revocation list).
- The Microsoft Entra scheme becomes the primary sign-in path, which would take token issuing out of this code.

## Code references

| Concern | Location |
|---|---|
| Token creation and hashing | `day1/QuotesApi/Services/TokenService.cs` |
| Login, register, refresh, logout, reuse detection | `day1/QuotesApi/Services/AuthService.cs` |
| Endpoints and uniform 401 | `day1/QuotesApi/Controllers/AuthController.cs` |
| Refresh token entity | `day1/QuotesApi/Models/RefreshToken.cs` |
| JWT validation and policies | `day1/QuotesApi/Auth/AuthenticationExtensions.cs` |
| Client token store and coalesced refresh | `quotes-ui/src/app/core/auth/auth.ts` |
| Bearer attach and retry on 401 | `quotes-ui/src/app/core/auth/auth-interceptor.ts` |
