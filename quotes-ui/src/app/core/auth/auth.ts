import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, catchError, finalize, map, shareReplay, tap, throwError } from 'rxjs';

// Real response shape from POST /api/auth/login and /api/auth/register
// (day1/QuotesApi Dtos/AuthDtos.cs, LoginResponse) - snake_case on purpose
// (OAuth2-style), unlike the rest of this API's camelCase. Getting this
// wrong silently produces undefined.
export interface LoginResponseDto {
  access_token: string;
  refresh_token: string;
  expires_in: number;
}

// Where the token pair lives so it survives a full page reload.
// sessionStorage, not localStorage: it should live for the tab's session and
// no longer - a page refresh or a deep-link into a guarded route (e.g.
// bookmarking /quotes/new) must NOT dump the user back at /login, but
// closing the tab is a fine place to require signing in again.
const ACCESS_TOKEN_STORAGE_KEY = 'quotes.access_token';
const REFRESH_TOKEN_STORAGE_KEY = 'quotes.refresh_token';

// sessionStorage access throws in some environments (SSR, privacy-mode
// iframes). Every touch is guarded so the service degrades to in-memory-only
// rather than failing to construct.
function readStoredValue(key: string): string | null {
  try {
    return sessionStorage.getItem(key);
  } catch {
    return null;
  }
}

function writeStoredValue(key: string, value: string | null): void {
  try {
    if (value === null) {
      sessionStorage.removeItem(key);
    } else {
      sessionStorage.setItem(key, value);
    }
  } catch {
    // Non-fatal: the in-memory signal is still authoritative for this
    // page load; only cross-reload persistence is lost.
  }
}

@Injectable({ providedIn: 'root' })
export class Auth {
  private readonly http = inject(HttpClient);

  // The single source of truth for "are we signed in". A route guard
  // (auth-guard.ts) reads isAuthenticated off this synchronously, so it has
  // to reflect real state - null until login()/register() actually complete,
  // or a previous session's token is found in sessionStorage on construction
  // (see readStoredValue calls below) so an F5 (or a bookmark straight into)
  // any authGuard-protected route doesn't bounce a still-signed-in user back
  // to /login.
  private readonly _accessToken = signal<string | null>(readStoredValue(ACCESS_TOKEN_STORAGE_KEY));
  private readonly _refreshToken = signal<string | null>(readStoredValue(REFRESH_TOKEN_STORAGE_KEY));

  // Exposed read-only for anything that wants the raw value reactively.
  readonly token = this._accessToken.asReadonly();

  // Synchronous, signal-derived. This is what authGuard consults.
  readonly isAuthenticated = computed(() => this._accessToken() !== null);

  // Coalesces concurrent refresh attempts: if two protected requests both hit
  // a 401 at once, authInterceptor must not fire two /api/auth/refresh
  // calls (the second would try to rotate a token the first has already
  // rotated - itself a "reuse" as far as the server's reuse-detection is
  // concerned, which would revoke the whole chain and force a real
  // re-login). Every caller within the same in-flight window gets the same
  // shared Observable instead.
  private refreshInFlight$: Observable<string> | null = null;

  // Explicit, caller-driven sign-in. POSTs the given credentials to
  // /api/auth/login and stashes both tokens. Returns Observable<void> so the
  // Login component can wait for it before navigating (and surface an error
  // if it fails). The auth interceptor excludes /api/auth/*, so this request
  // does not recurse back through token attachment.
  login(email: string, password: string): Observable<void> {
    return this.http.post<LoginResponseDto>('/api/auth/login', { email, password }).pipe(
      tap((res) => this.storeTokens(res)),
      map(() => undefined),
    );
  }

  // POST /api/auth/register (AuthController.Register) - same LoginResponse
  // shape as login, so a successful registration also signs the user in
  // immediately; there's no separate "verify your email" step in this
  // exercise.
  register(email: string, password: string): Observable<void> {
    return this.http.post<LoginResponseDto>('/api/auth/register', { email, password }).pipe(
      tap((res) => this.storeTokens(res)),
      map(() => undefined),
    );
  }

  // Drops both tokens from the signals and sessionStorage immediately - the
  // local sign-out must succeed even if the server is unreachable - and
  // best-effort revokes the refresh token server-side (POST /api/auth/logout)
  // so it can't be replayed later. The revoke's outcome is deliberately not
  // surfaced anywhere: a failed revoke here (network blip, token already
  // expired) shouldn't block or confuse a user who has already been signed
  // out locally.
  logout(): void {
    const refreshToken = this._refreshToken();
    this.clearTokens();

    if (refreshToken !== null) {
      this.http.post('/api/auth/logout', { refresh_token: refreshToken }).subscribe({ error: () => {} });
    }
  }

  // Returns the stored access token, or null if not signed in. Deliberately
  // synchronous, no side effect - the caller (authInterceptor) decides what
  // to do when it's null.
  getToken(): string | null {
    return this._accessToken();
  }

  // POST /api/auth/refresh with the stored refresh token. Rotates both
  // tokens on success (AuthService.RefreshAsync always issues a fresh pair -
  // see day1/QuotesApi's reuse-detection comments) and clears everything on
  // failure (an expired/revoked/reused refresh token means the session is
  // genuinely over - there is nothing left to retry with). Shared across
  // concurrent callers via refreshInFlight$ so a burst of 401s triggers at
  // most one real refresh call.
  refreshAccessToken(): Observable<string> {
    if (this.refreshInFlight$) {
      return this.refreshInFlight$;
    }

    const refreshToken = this._refreshToken();
    if (refreshToken === null) {
      return throwError(() => new Error('No refresh token available.'));
    }

    const request$ = this.http.post<LoginResponseDto>('/api/auth/refresh', { refresh_token: refreshToken }).pipe(
      tap((res) => this.storeTokens(res)),
      map((res) => res.access_token),
      catchError((err: unknown) => {
        this.clearTokens();
        return throwError(() => err);
      }),
      finalize(() => {
        this.refreshInFlight$ = null;
      }),
      // shareReplay(1), not a plain multicast: a request that subscribes
      // AFTER the refresh has already completed (a straggler in the same
      // 401 burst) still needs the resolved token/error, not a brand-new
      // HTTP call.
      shareReplay(1),
    );

    this.refreshInFlight$ = request$;
    return request$;
  }

  private storeTokens(res: LoginResponseDto): void {
    this._accessToken.set(res.access_token);
    this._refreshToken.set(res.refresh_token);
    writeStoredValue(ACCESS_TOKEN_STORAGE_KEY, res.access_token);
    writeStoredValue(REFRESH_TOKEN_STORAGE_KEY, res.refresh_token);
  }

  private clearTokens(): void {
    this._accessToken.set(null);
    this._refreshToken.set(null);
    writeStoredValue(ACCESS_TOKEN_STORAGE_KEY, null);
    writeStoredValue(REFRESH_TOKEN_STORAGE_KEY, null);
  }
}
