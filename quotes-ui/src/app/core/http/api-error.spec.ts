import { HttpErrorResponse } from '@angular/common/http';
import { ApiError, mapToApiError } from './api-error';

// mapToApiError is a plain function - no TestBed/HttpTestingController
// needed. Covered here with the exact real response shapes documented in
// quote.contract.spec.ts (captured from a live curl against the real
// backend), not invented fixtures.
describe('mapToApiError', () => {
  it('maps the real empty-bodied 404 (Results.NotFound(), no JSON body) to kind "notFound"', () => {
    const err = new HttpErrorResponse({ status: 404, statusText: 'Not Found', error: null, url: '/api/quotes/999999' });

    const result = mapToApiError(err);

    expect(result).toBeInstanceOf(ApiError);
    expect(result.kind).toBe('notFound');
    expect(result.status).toBe(404);
    expect(result.message.length).toBeGreaterThan(0);
  });

  it('maps the real empty-bodied 401 (Results.Unauthorized(), no JSON body) to kind "auth"', () => {
    const err = new HttpErrorResponse({ status: 401, statusText: 'Unauthorized', error: null, url: '/api/quotes' });

    const result = mapToApiError(err);

    expect(result.kind).toBe('auth');
    expect(result.status).toBe(401);
  });

  it('maps a 403 to kind "auth" as well (forbidden is still an auth problem, not a distinct kind)', () => {
    const err = new HttpErrorResponse({ status: 403, statusText: 'Forbidden', error: null, url: '/api/quotes' });

    const result = mapToApiError(err);

    expect(result.kind).toBe('auth');
    expect(result.status).toBe(403);
  });

  // Real bytes from an authenticated POST /api/quotes with an invalid body:
  // {"type":"https://tools.ietf.org/html/rfc9110#section-15.5.1","title":
  // "One or more validation errors occurred.","status":400,"errors":
  // {"error":["Author must be between 1 and 200 characters."]},"traceId":
  // "00-a4265a21d37a8ccd5964783a7397f5f9-b01378488a05a862-01"}
  it('maps the real 400 ValidationProblemDetails to kind "validation" and extracts errors.error[0]', () => {
    const err = new HttpErrorResponse({
      status: 400,
      statusText: 'Bad Request',
      error: {
        type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
        title: 'One or more validation errors occurred.',
        status: 400,
        errors: { error: ['Author must be between 1 and 200 characters.'] },
        traceId: '00-a4265a21d37a8ccd5964783a7397f5f9-b01378488a05a862-01',
      },
      url: '/api/quotes',
    });

    const result = mapToApiError(err);

    expect(result.kind).toBe('validation');
    expect(result.status).toBe(400);
    expect(result.message).toBe('Author must be between 1 and 200 characters.');
  });

  it('falls back to a generic validation message when errors.error is missing/empty', () => {
    const err = new HttpErrorResponse({
      status: 400,
      statusText: 'Bad Request',
      error: { type: 'about:blank', title: 'Bad Request', status: 400 },
      url: '/api/quotes',
    });

    const result = mapToApiError(err);

    expect(result.kind).toBe('validation');
    expect(result.message.length).toBeGreaterThan(0);
  });

  // Real bytes from POST /api/collections/{id}/items with a duplicate quote:
  // {"title":"One or more validation errors occurred.","status":400,"errors":
  // {"QuoteId":["Quote 3 is already in this collection."]},"traceId":"..."}
  // The collections endpoints key their messages by field name, NOT under the
  // single "error" key the quotes endpoints use - the extractor must still
  // surface the real sentence, not collapse it to the generic fallback.
  it('extracts the message from a field-name key (collections 400 shape), not just "error"', () => {
    const err = new HttpErrorResponse({
      status: 400,
      statusText: 'Bad Request',
      error: {
        title: 'One or more validation errors occurred.',
        status: 400,
        errors: { QuoteId: ['Quote 3 is already in this collection.'] },
      },
      url: '/api/collections/2/items',
    });

    const result = mapToApiError(err);

    expect(result.kind).toBe('validation');
    expect(result.message).toBe('Quote 3 is already in this collection.');
  });

  // DELETE /api/collections/{id}/items/{quoteId} uses a lowercase "quoteId"
  // key (nameof the route param) - a different key again from the POST above.
  it('extracts the message from the DELETE-items "quoteId" key too', () => {
    const err = new HttpErrorResponse({
      status: 400,
      statusText: 'Bad Request',
      error: { errors: { quoteId: ['Quote 999 is not in this collection.'] } },
      url: '/api/collections/2/items/999',
    });

    expect(mapToApiError(err).message).toBe('Quote 999 is not in this collection.');
  });

  it('maps a real network failure (status 0, no response reached the client) to kind "network"', () => {
    // This is exactly what HttpClient reports for offline/DNS failure/
    // connection refused/rejected CORS preflight - status is 0, never a
    // real HTTP status code from the server.
    const err = new HttpErrorResponse({ status: 0, statusText: 'Unknown Error', error: new ProgressEvent('error') });

    const result = mapToApiError(err);

    expect(result.kind).toBe('network');
    expect(result.status).toBe(0);
  });

  it('maps a 5xx to kind "server"', () => {
    const err = new HttpErrorResponse({ status: 500, statusText: 'Internal Server Error', error: null, url: '/api/quotes' });

    const result = mapToApiError(err);

    expect(result.kind).toBe('server');
    expect(result.status).toBe(500);
  });

  it('maps a non-HttpErrorResponse thrown value to a generic network ApiError rather than throwing', () => {
    const result = mapToApiError(new Error('something unrelated blew up'));

    expect(result).toBeInstanceOf(ApiError);
    expect(result.kind).toBe('network');
    expect(result.status).toBeNull();
  });

  it('ApiError is a real Error subclass usable with instanceof Error', () => {
    const result = mapToApiError(new HttpErrorResponse({ status: 404, error: null }));

    expect(result instanceof Error).toBe(true);
    expect(result instanceof ApiError).toBe(true);
  });
});
