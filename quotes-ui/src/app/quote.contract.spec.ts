import { TestBed } from '@angular/core/testing';
import { HttpErrorResponse, provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { Quote } from './quote';
import type { QuoteDto } from './quote';

// Characterization tests (Michael Feathers' sense): these pin down what the
// REAL backend actually does, using bytes captured from a live curl against
// it - not invented fixtures. Deliberately configured with plain
// provideHttpClient()/provideHttpClientTesting() and NO interceptors (same
// as quote.spec.ts) - this file exists to pin the raw Quote service/HTTP
// contract itself, independent of whatever interceptor layer sits in front
// of it in the real app (see error-mapping-interceptor.ts,
// retry-interceptor.ts, app.config.ts). It must stay green regardless of
// what those interceptors do later.
describe('Quote service - real backend contract (characterization)', () => {
  let service: Quote;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(), provideHttpClientTesting()],
    });
    service = TestBed.inject(Quote);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  // Real bytes from `curl .../api/quotes?page=1&size=2`:
  // [{"id":2,"author":"Grace Hopper","text":"A ship in port is safe, but
  // that is not what ships are built for.","isDeleted":false,
  // "createdByUserId":1}]
  it('getQuotes(1, 2) parses the real array shape, including fields the UI does not use', () => {
    const realBody = [
      {
        id: 2,
        author: 'Grace Hopper',
        text: 'A ship in port is safe, but that is not what ships are built for.',
        isDeleted: false,
        createdByUserId: 1,
      },
    ];

    let result: QuoteDto[] | undefined;
    service.getQuotes(1, 2).subscribe((res) => (result = res));

    const req = httpMock.expectOne('/api/quotes?page=1&size=2');
    expect(req.request.method).toBe('GET');
    req.flush(realBody, { status: 200, statusText: 'OK', headers: { 'Content-Type': 'application/json' } });

    // Exact equality, not a subset check - isDeleted/createdByUserId are on
    // the wire whether or not any current UI reads them, and a change that
    // silently drops them from QuoteDto should fail this test.
    expect(result).toEqual(realBody);
    expect(result?.[0].isDeleted).toBe(false);
    expect(result?.[0].createdByUserId).toBe(1);
  });

  // Real behavior from `curl .../api/quotes/999999` (nonexistent id): 404,
  // Content-Length: 0 - a genuinely empty body. Results.NotFound() in
  // Extension.cs; this API has AddProblemDetails() registered but no
  // UseStatusCodePages()/UseExceptionHandler wiring for status-code results,
  // so this does NOT come back as a ProblemDetails JSON body.
  it('getQuoteById(999999) surfaces the real empty 404 as an HttpErrorResponse without throwing on the empty body', () => {
    let captured: HttpErrorResponse | undefined;
    let threw = false;

    service.getQuoteById(999999).subscribe({
      next: () => {
        threw = true;
      },
      error: (err: unknown) => {
        // If Angular's HttpClient choked trying to parse an empty body as
        // JSON, the error path itself is where that would surface - proving
        // this callback runs at all (with a genuine HttpErrorResponse, not
        // some parse-error artifact) is the point of this test.
        if (err instanceof HttpErrorResponse) {
          captured = err;
        } else {
          threw = true;
        }
      },
    });

    const req = httpMock.expectOne('/api/quotes/999999');
    expect(req.request.method).toBe('GET');
    // null body + Content-Length effectively 0, exactly like the real
    // empty-bodied 404.
    req.flush(null, { status: 404, statusText: 'Not Found' });

    expect(threw).toBe(false);
    expect(captured).toBeTruthy();
    expect(captured?.status).toBe(404);
    // No JSON body to parse - error must be null/empty, not a parse-error
    // wrapper object.
    expect(captured?.error).toBeFalsy();
  });

  // Real bytes from an authenticated POST /api/quotes with an invalid body
  // (empty author):
  // {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":
  // "One or more validation errors occurred.","status":400,"errors":
  // {"error":["Author must be between 1 and 200 characters."]},"traceId":
  // "00-a4265a21d37a8ccd5964783a7397f5f9-b01378488a05a862-01"}
  // Content-Type: application/problem+json - a REAL ValidationProblemDetails,
  // unlike the empty-bodied 404/401 above. errors always has exactly one
  // key, literally "error", never per-field (Extension.cs:
  // Results.ValidationProblem(new Dictionary<string,string[]>
  // { ["error"] = [result.Error!] })).
  it('createQuote surfaces the exact errors.error[0] message from the real 400 ValidationProblemDetails body', () => {
    const realProblemDetails = {
      type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
      title: 'One or more validation errors occurred.',
      status: 400,
      errors: { error: ['Author must be between 1 and 200 characters.'] },
      traceId: '00-a4265a21d37a8ccd5964783a7397f5f9-b01378488a05a862-01',
    };

    let captured: HttpErrorResponse | undefined;
    let succeededUnexpectedly = false;
    service.createQuote('', 'Some text').subscribe({
      next: () => {
        succeededUnexpectedly = true;
      },
      error: (err: HttpErrorResponse) => (captured = err),
    });

    const req = httpMock.expectOne('/api/quotes');
    expect(req.request.method).toBe('POST');
    req.flush(realProblemDetails, {
      status: 400,
      statusText: 'Bad Request',
      headers: { 'Content-Type': 'application/problem+json' },
    });

    expect(succeededUnexpectedly).toBe(false);
    expect(captured?.status).toBe(400);
    const body = captured?.error as { errors: { error: string[] } };
    expect(body.errors.error).toEqual(['Author must be between 1 and 200 characters.']);
    expect(body.errors.error[0]).toBe('Author must be between 1 and 200 characters.');
  });
});
