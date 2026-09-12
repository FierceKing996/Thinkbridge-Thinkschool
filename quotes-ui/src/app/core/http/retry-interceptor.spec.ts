import { TestBed } from '@angular/core/testing';
import { HttpClient, HttpErrorResponse, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { retryInterceptor } from './retry-interceptor';

// Uses vitest's fake timers (not Angular's zone.js-based fakeAsync/tick -
// this app is zoneless, no zone.js is loaded at all) to advance past the
// backoff delay deterministically instead of waiting on real wall-clock
// time.
describe('retryInterceptor', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(withInterceptors([retryInterceptor])), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
    vi.useRealTimers();
  });

  it('does not retry a successful GET - only one request goes out', () => {
    let result: unknown;
    http.get('/api/quotes').subscribe((res) => (result = res));

    httpMock.expectOne('/api/quotes').flush([{ id: 1 }]);

    expect(result).toEqual([{ id: 1 }]);
  });

  it('retries a GET that fails with a network error (status 0) up to 3 total attempts, then succeeds if a retry does', async () => {
    vi.useFakeTimers();

    let result: unknown;
    http.get('/api/quotes').subscribe((res) => (result = res));

    // 1st attempt fails with a genuine network error (status 0).
    httpMock.expectOne('/api/quotes').error(new ProgressEvent('network error'), { status: 0, statusText: 'Unknown Error' });
    await vi.advanceTimersByTimeAsync(250);

    // 2nd attempt (1st retry) also fails.
    httpMock.expectOne('/api/quotes').error(new ProgressEvent('network error'), { status: 0, statusText: 'Unknown Error' });
    await vi.advanceTimersByTimeAsync(500);

    // 3rd attempt (2nd retry) succeeds.
    httpMock.expectOne('/api/quotes').flush([{ id: 1 }]);

    expect(result).toEqual([{ id: 1 }]);
  });

  it('gives up after 3 total attempts (1 original + 2 retries) and propagates the final failure', async () => {
    vi.useFakeTimers();

    let captured: HttpErrorResponse | undefined;
    http.get('/api/quotes').subscribe({ error: (err) => (captured = err) });

    httpMock.expectOne('/api/quotes').error(new ProgressEvent('network error'), { status: 0, statusText: 'Unknown Error' });
    await vi.advanceTimersByTimeAsync(250);

    httpMock.expectOne('/api/quotes').error(new ProgressEvent('network error'), { status: 0, statusText: 'Unknown Error' });
    await vi.advanceTimersByTimeAsync(500);

    // 3rd and final attempt also fails - no more retries left.
    httpMock.expectOne('/api/quotes').error(new ProgressEvent('network error'), { status: 0, statusText: 'Unknown Error' });

    expect(captured).toBeTruthy();
    expect(captured?.status).toBe(0);
    // Exactly 3 requests total - a 4th expectOne here would throw if the
    // interceptor kept retrying past the configured count.
    httpMock.expectNone('/api/quotes');
  });

  it('retries a 5xx the same way as a network error', async () => {
    vi.useFakeTimers();

    let result: unknown;
    http.get('/api/quotes').subscribe((res) => (result = res));

    httpMock.expectOne('/api/quotes').flush(null, { status: 503, statusText: 'Service Unavailable' });
    await vi.advanceTimersByTimeAsync(250);

    httpMock.expectOne('/api/quotes').flush([{ id: 1 }]);

    expect(result).toEqual([{ id: 1 }]);
  });

  it('never retries a 404 - only one request goes out and the error surfaces immediately', () => {
    let captured: HttpErrorResponse | undefined;
    http.get('/api/quotes/999999').subscribe({ error: (err) => (captured = err) });

    httpMock.expectOne('/api/quotes/999999').flush(null, { status: 404, statusText: 'Not Found' });

    expect(captured?.status).toBe(404);
    // No second request should ever have gone out - httpMock.verify() in
    // afterEach would also fail if a retry request was left dangling, but
    // this makes the "never retries a 4xx" guarantee explicit.
    httpMock.expectNone('/api/quotes/999999');
  });

  it('never retries a 401 - retrying an unauthenticated request cannot fix it and risks a confusing repeated-auth loop', () => {
    let captured: HttpErrorResponse | undefined;
    http.get('/api/quotes').subscribe({ error: (err) => (captured = err) });

    httpMock.expectOne('/api/quotes').flush(null, { status: 401, statusText: 'Unauthorized' });

    expect(captured?.status).toBe(401);
    httpMock.expectNone('/api/quotes');
  });

  it('never retries a non-GET request, even one that fails with a transient-looking 503', () => {
    let captured: HttpErrorResponse | undefined;
    http.post('/api/quotes', { author: 'A', text: 'B' }).subscribe({ error: (err) => (captured = err) });

    httpMock.expectOne('/api/quotes').flush(null, { status: 503, statusText: 'Service Unavailable' });

    expect(captured?.status).toBe(503);
    httpMock.expectNone('/api/quotes');
  });
});
