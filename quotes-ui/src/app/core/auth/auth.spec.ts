import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { Auth, LoginResponseDto } from './auth';

describe('Auth', () => {
  let auth: Auth;
  let httpMock: HttpTestingController;

  const tokenPair: LoginResponseDto = {
    access_token: 'the-access-token',
    refresh_token: 'the-refresh-token',
    expires_in: 900,
  };

  const rotatedTokenPair: LoginResponseDto = {
    access_token: 'the-rotated-access-token',
    refresh_token: 'the-rotated-refresh-token',
    expires_in: 900,
  };

  beforeEach(() => {
    // Auth seeds its token signals from sessionStorage on construction, so a
    // token left behind by another test would leak in as a false "already
    // signed in". Clear it before each case.
    sessionStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    auth = TestBed.inject(Auth);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
    sessionStorage.clear();
  });

  it('starts unauthenticated with no token and does NOT auto-login', () => {
    expect(auth.isAuthenticated()).toBe(false);
    expect(auth.getToken()).toBeNull();
    // The key behavioural change: reading the token must not fire a request.
    httpMock.expectNone('/api/auth/login');
  });

  it('login() POSTs the given credentials and stores both tokens', () => {
    let completed = false;
    auth.login('user@example.com', 'correct-password').subscribe(() => (completed = true));

    const req = httpMock.expectOne('/api/auth/login');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ email: 'user@example.com', password: 'correct-password' });
    req.flush(tokenPair);

    expect(completed).toBe(true);
    expect(auth.isAuthenticated()).toBe(true);
    expect(auth.getToken()).toBe('the-access-token');
  });

  it('stays unauthenticated when login() fails', () => {
    auth.login('user@example.com', 'wrong-password').subscribe({ next: () => {}, error: () => {} });
    httpMock.expectOne('/api/auth/login').flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(auth.isAuthenticated()).toBe(false);
    expect(auth.getToken()).toBeNull();
  });

  it('register() POSTs the given credentials and stores both tokens, same as login()', () => {
    let completed = false;
    auth.register('new@example.com', 'a-strong-password').subscribe(() => (completed = true));

    const req = httpMock.expectOne('/api/auth/register');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ email: 'new@example.com', password: 'a-strong-password' });
    req.flush(tokenPair);

    expect(completed).toBe(true);
    expect(auth.isAuthenticated()).toBe(true);
    expect(auth.getToken()).toBe('the-access-token');
  });

  it('persists both tokens to sessionStorage so they survive a reload', () => {
    auth.login('user@example.com', 'correct-password').subscribe();
    httpMock.expectOne('/api/auth/login').flush(tokenPair);
    expect(sessionStorage.getItem('quotes.access_token')).toBe('the-access-token');
    expect(sessionStorage.getItem('quotes.refresh_token')).toBe('the-refresh-token');

    // Simulate a full page reload: a brand-new Auth instance in a fresh
    // injector, reading only what's in storage - no login() call.
    TestBed.resetTestingModule();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    const reloaded = TestBed.inject(Auth);
    TestBed.inject(HttpTestingController).expectNone('/api/auth/login');

    expect(reloaded.isAuthenticated()).toBe(true);
    expect(reloaded.getToken()).toBe('the-access-token');
  });

  it('logout() clears both tokens locally and best-effort revokes the refresh token server-side', () => {
    auth.login('user@example.com', 'correct-password').subscribe();
    httpMock.expectOne('/api/auth/login').flush(tokenPair);
    expect(auth.isAuthenticated()).toBe(true);

    auth.logout();

    // Local sign-out is synchronous and unconditional.
    expect(auth.isAuthenticated()).toBe(false);
    expect(auth.getToken()).toBeNull();
    expect(sessionStorage.getItem('quotes.access_token')).toBeNull();
    expect(sessionStorage.getItem('quotes.refresh_token')).toBeNull();

    // The revoke call fires, but its outcome must not matter to the caller -
    // flushing an error here must not throw or resurrect the session.
    const req = httpMock.expectOne('/api/auth/logout');
    expect(req.request.body).toEqual({ refresh_token: 'the-refresh-token' });
    req.flush(null, { status: 500, statusText: 'Server Error' });
    expect(auth.isAuthenticated()).toBe(false);
  });

  it('logout() when never signed in does not call the backend at all', () => {
    auth.logout();
    httpMock.expectNone('/api/auth/logout');
  });

  it('refreshAccessToken() rotates both tokens on success', () => {
    auth.login('user@example.com', 'correct-password').subscribe();
    httpMock.expectOne('/api/auth/login').flush(tokenPair);

    let newAccessToken: string | undefined;
    auth.refreshAccessToken().subscribe((token) => (newAccessToken = token));

    const req = httpMock.expectOne('/api/auth/refresh');
    expect(req.request.body).toEqual({ refresh_token: 'the-refresh-token' });
    req.flush(rotatedTokenPair);

    expect(newAccessToken).toBe('the-rotated-access-token');
    expect(auth.getToken()).toBe('the-rotated-access-token');
    expect(sessionStorage.getItem('quotes.refresh_token')).toBe('the-rotated-refresh-token');
  });

  it('refreshAccessToken() coalesces concurrent callers into a single HTTP call', () => {
    auth.login('user@example.com', 'correct-password').subscribe();
    httpMock.expectOne('/api/auth/login').flush(tokenPair);

    const results: string[] = [];
    auth.refreshAccessToken().subscribe((token) => results.push(token));
    auth.refreshAccessToken().subscribe((token) => results.push(token));

    // Exactly one real request for both callers.
    httpMock.expectOne('/api/auth/refresh').flush(rotatedTokenPair);

    expect(results).toEqual(['the-rotated-access-token', 'the-rotated-access-token']);
  });

  it('refreshAccessToken() clears both tokens when the refresh token itself is rejected', () => {
    auth.login('user@example.com', 'correct-password').subscribe();
    httpMock.expectOne('/api/auth/login').flush(tokenPair);

    auth.refreshAccessToken().subscribe({ next: () => {}, error: () => {} });
    httpMock.expectOne('/api/auth/refresh').flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(auth.isAuthenticated()).toBe(false);
    expect(auth.getToken()).toBeNull();
  });

  it('refreshAccessToken() errors immediately with no HTTP call when there is no refresh token', () => {
    let errored = false;
    auth.refreshAccessToken().subscribe({ next: () => {}, error: () => (errored = true) });

    expect(errored).toBe(true);
    httpMock.expectNone('/api/auth/refresh');
  });
});
