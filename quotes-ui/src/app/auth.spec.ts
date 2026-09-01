import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { Auth, LoginResponseDto } from './auth';

describe('Auth', () => {
  let auth: Auth;
  let httpMock: HttpTestingController;

  const loginResponse: LoginResponseDto = {
    access_token: 'the-access-token',
    refresh_token: 'the-refresh-token',
    expires_in: 900,
  };

  beforeEach(() => {
    // Auth seeds its token signal from sessionStorage on construction, so a
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

  it('login() POSTs the demo creds and stores the access token', () => {
    let completed = false;
    auth.login().subscribe(() => (completed = true));

    const req = httpMock.expectOne('/api/auth/login');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({
      email: 'demo@quotesapi.dev',
      password: 'correct-horse-battery-staple',
    });
    req.flush(loginResponse);

    expect(completed).toBe(true);
    expect(auth.isAuthenticated()).toBe(true);
    expect(auth.getToken()).toBe('the-access-token');
  });

  it('stays unauthenticated when login() fails', () => {
    auth.login().subscribe({ next: () => {}, error: () => {} });
    httpMock.expectOne('/api/auth/login').flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(auth.isAuthenticated()).toBe(false);
    expect(auth.getToken()).toBeNull();
  });

  it('persists the access token to sessionStorage so it survives a reload', () => {
    auth.login().subscribe();
    httpMock.expectOne('/api/auth/login').flush(loginResponse);
    expect(sessionStorage.getItem('quotes.access_token')).toBe('the-access-token');

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

  it('logout() clears the token from the signal and sessionStorage', () => {
    auth.login().subscribe();
    httpMock.expectOne('/api/auth/login').flush(loginResponse);
    expect(auth.isAuthenticated()).toBe(true);

    auth.logout();

    expect(auth.isAuthenticated()).toBe(false);
    expect(auth.getToken()).toBeNull();
    expect(sessionStorage.getItem('quotes.access_token')).toBeNull();
  });
});
