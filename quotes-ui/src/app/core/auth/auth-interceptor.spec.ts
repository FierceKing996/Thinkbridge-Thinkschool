import { TestBed } from '@angular/core/testing';
import { HttpClient, HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { authInterceptor } from './auth-interceptor';
import { Auth } from './auth';

describe('authInterceptor', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;
  let auth: Auth;

  beforeEach(() => {
    sessionStorage.clear();
    TestBed.configureTestingModule({
      providers: [provideHttpClient(withInterceptors([authInterceptor])), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
    auth = TestBed.inject(Auth);
  });

  afterEach(() => {
    httpMock.verify();
    sessionStorage.clear();
  });

  function signIn(): void {
    auth.login('user@example.com', 'correct-password').subscribe();
    httpMock
      .expectOne('/api/auth/login')
      .flush({ access_token: 'access-1', refresh_token: 'refresh-1', expires_in: 900 });
  }

  it('does not attach a token to a GET request', () => {
    signIn();

    http.get('/api/quotes').subscribe();

    const req = httpMock.expectOne('/api/quotes');
    expect(req.request.headers.has('Authorization')).toBe(false);
    req.flush([]);
  });

  it('attaches the bearer token to a non-GET request when signed in', () => {
    signIn();

    http.post('/api/quotes', { author: 'A', text: 'B' }).subscribe();

    const req = httpMock.expectOne('/api/quotes');
    expect(req.request.headers.get('Authorization')).toBe('Bearer access-1');
    req.flush({});
  });

  it('does not attach a token to /api/auth/* requests, even when signed in', () => {
    signIn();

    http.post('/api/auth/logout', { refresh_token: 'refresh-1' }).subscribe();

    const req = httpMock.expectOne('/api/auth/logout');
    expect(req.request.headers.has('Authorization')).toBe(false);
    req.flush(null);
  });

  it('sends a non-GET request unauthenticated (no refresh attempt) when never signed in', () => {
    let captured: HttpErrorResponse | undefined;
    http.post('/api/quotes', { author: 'A', text: 'B' }).subscribe({ error: (err) => (captured = err) });

    const req = httpMock.expectOne('/api/quotes');
    expect(req.request.headers.has('Authorization')).toBe(false);
    req.flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(captured?.status).toBe(401);
    // No refresh attempt - there was never a token to have gone stale.
    httpMock.expectNone('/api/auth/refresh');
  });

  it('on a 401 from an authenticated request, silently refreshes and retries once with the new token', () => {
    signIn();

    let result: unknown;
    http.post('/api/quotes', { author: 'A', text: 'B' }).subscribe((res) => (result = res));

    httpMock.expectOne('/api/quotes').flush(null, { status: 401, statusText: 'Unauthorized' });

    const refreshReq = httpMock.expectOne('/api/auth/refresh');
    expect(refreshReq.request.body).toEqual({ refresh_token: 'refresh-1' });
    refreshReq.flush({ access_token: 'access-2', refresh_token: 'refresh-2', expires_in: 900 });

    const retryReq = httpMock.expectOne('/api/quotes');
    expect(retryReq.request.headers.get('Authorization')).toBe('Bearer access-2');
    retryReq.flush({ id: 1 });

    expect(result).toEqual({ id: 1 });
    expect(auth.getToken()).toBe('access-2');
  });

  it('when the refresh itself fails, signs the user out and propagates the ORIGINAL 401', () => {
    signIn();

    let captured: HttpErrorResponse | undefined;
    http.post('/api/quotes', { author: 'A', text: 'B' }).subscribe({ error: (err) => (captured = err) });

    httpMock.expectOne('/api/quotes').flush(null, { status: 401, statusText: 'Unauthorized' });
    httpMock.expectOne('/api/auth/refresh').flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(captured?.status).toBe(401);
    expect(auth.isAuthenticated()).toBe(false);
    // The retry-after-refresh request must never have gone out.
    httpMock.expectNone('/api/quotes');
  });

  it('does not attempt a refresh for a non-401 failure', () => {
    signIn();

    let captured: HttpErrorResponse | undefined;
    http.post('/api/quotes', { author: 'A', text: 'B' }).subscribe({ error: (err) => (captured = err) });

    httpMock.expectOne('/api/quotes').flush(null, { status: 500, statusText: 'Server Error' });

    expect(captured?.status).toBe(500);
    httpMock.expectNone('/api/auth/refresh');
  });
});
