import { Injectable, computed, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, map, tap } from 'rxjs';

// Real response shape from POST /api/auth/login (day1/QuotesApi/Auth.cs,
// LoginResponse) - snake_case on purpose (OAuth2-style), unlike the rest of
// this API's camelCase. Getting this wrong silently produces undefined.
export interface LoginResponseDto {
  access_token: string;
  refresh_token: string;
  expires_in: number;
}

// The seeded user from day1/QuotesApi/Program.cs, scoped "quotes.write" - the
// scope POST /api/quotes' can-edit-quotes policy requires. There is no real
// login UI for this exercise; the Login component just fires login() with
// these creds behind a button.
const DEMO_CREDENTIALS = {
  email: 'demo@quotesapi.dev',
  password: 'correct-horse-battery-staple',
};

// Where the access token from POST /api/auth/login's response (its
// `access_token` field - see LoginResponseDto) is parked so it survives a
// full page reload. sessionStorage, not localStorage: it should live for the
// tab's session and no longer - a page refresh or a deep-link into a guarded
// route (e.g. bookmarking /quotes/new) must NOT dump the user back at /login,
// but closing the tab is a fine place to require signing in again. The
// short-lived nature of the token (LoginResponse.expires_in is 900s) is not
// tracked here - an expired token just produces a 401 that the ApiError layer
// surfaces normally; refresh-token rotation is out of scope for this demo.
const TOKEN_STORAGE_KEY = 'quotes.access_token';

// sessionStorage access throws in some environments (SSR, privacy-mode
// iframes). Every touch is guarded so the service degrades to in-memory-only
// rather than failing to construct.
function readStoredToken(): string | null {
  try {
    return sessionStorage.getItem(TOKEN_STORAGE_KEY);
  } catch {
    return null;
  }
}

function writeStoredToken(token: string | null): void {
  try {
    if (token === null) {
      sessionStorage.removeItem(TOKEN_STORAGE_KEY);
    } else {
      sessionStorage.setItem(TOKEN_STORAGE_KEY, token);
    }
  } catch {
    // Non-fatal: the in-memory signal is still authoritative for this
    // page load; only cross-reload persistence is lost.
  }
}

@Injectable({ providedIn: 'root' })
export class Auth {
  private readonly http = inject(HttpClient);

  // The single source of truth for "are we signed in". Starts null: there is
  // NO auto-login side effect any more. The previous version logged in lazily
  // inside getToken() and cached the Observable via shareReplay, which meant
  // the app was effectively always-authenticated and a route guard could
  // never meaningfully say "no". A guard (auth-guard.ts) now reads
  // isAuthenticated off this synchronously, so it has to reflect real state -
  // false until login() actually completes.
  //
  // Seeded from sessionStorage on construction so a token obtained by a
  // previous login() on this tab outlives a page reload. Without this, F5 on
  // (or a bookmark straight into) any authGuard-protected route re-enters
  // with _token null and the guard bounces the user to /login even though
  // they signed in moments ago - a regression that removing the old
  // auto-login opened up.
  private readonly _token = signal<string | null>(readStoredToken());

  // Exposed read-only for anything that wants the raw value reactively.
  readonly token = this._token.asReadonly();

  // Synchronous, signal-derived. This is what authGuard consults.
  readonly isAuthenticated = computed(() => this._token() !== null);

  // Explicit, caller-driven sign-in. POSTs the demo creds to
  // /api/auth/login and stashes the access token in the signal. Returns
  // Observable<void> so the Login component can wait for it before
  // navigating (and surface an error if it fails). The auth interceptor
  // excludes /api/auth/*, so this request does not recurse back through
  // token attachment.
  login(): Observable<void> {
    return this.http.post<LoginResponseDto>('/api/auth/login', DEMO_CREDENTIALS).pipe(
      tap((res) => {
        this._token.set(res.access_token);
        writeStoredToken(res.access_token);
      }),
      map(() => undefined),
    );
  }

  // Drops the token from both the signal and sessionStorage. Not wired to any
  // UI in this exercise, but pairs with login() so a caller (or a spec) can
  // return the app to a genuinely signed-out state.
  logout(): void {
    this._token.set(null);
    writeStoredToken(null);
  }

  // Returns the stored access token, or null if login() has never
  // succeeded. Deliberately does NOT perform a login as a side effect any
  // more - the auth interceptor (auth-interceptor.ts) only ever reaches
  // non-GET requests, and the only write route in the app ('quotes/new') is
  // behind authGuard, so by the time this is consulted for a real request a
  // token is already present. If it somehow isn't, the interceptor sends
  // the request unauthenticated and the server's 401 surfaces normally
  // through the ApiError layer rather than being masked by a hidden login.
  getToken(): string | null {
    return this._token();
  }
}
