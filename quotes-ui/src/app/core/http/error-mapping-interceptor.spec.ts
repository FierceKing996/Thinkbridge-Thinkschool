import { TestBed } from '@angular/core/testing';
import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { ApiError } from './api-error';
import { errorMappingInterceptor } from './error-mapping-interceptor';

// Exercises the interceptor itself (request -> mocked HTTP backend ->
// interceptor -> subscriber), not just the mapToApiError function it wraps
// (see api-error.spec.ts for that) - this confirms the interceptor actually
// rethrows the mapped ApiError instead of the raw HttpErrorResponse, and
// leaves a successful response completely untouched.
describe('errorMappingInterceptor', () => {
  let http: HttpClient;
  let httpMock: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [provideHttpClient(withInterceptors([errorMappingInterceptor])), provideHttpClientTesting()],
    });
    http = TestBed.inject(HttpClient);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('passes a successful response through unchanged', () => {
    let result: unknown;
    http.get('/api/quotes').subscribe((res) => (result = res));

    httpMock.expectOne('/api/quotes').flush([{ id: 1 }]);

    expect(result).toEqual([{ id: 1 }]);
  });

  it('rethrows the real empty-bodied 404 as a typed ApiError with kind "notFound", not a raw HttpErrorResponse', () => {
    let captured: unknown;
    http.get('/api/quotes/999999').subscribe({ error: (err) => (captured = err) });

    httpMock.expectOne('/api/quotes/999999').flush(null, { status: 404, statusText: 'Not Found' });

    expect(captured).toBeInstanceOf(ApiError);
    expect((captured as ApiError).kind).toBe('notFound');
    expect((captured as ApiError).status).toBe(404);
  });

  it('rethrows the real 400 ValidationProblemDetails as a typed ApiError carrying errors.error[0]', () => {
    let captured: unknown;
    http.post('/api/quotes', { author: '', text: 'x' }).subscribe({ error: (err) => (captured = err) });

    httpMock.expectOne('/api/quotes').flush(
      {
        type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
        title: 'One or more validation errors occurred.',
        status: 400,
        errors: { error: ['Author must be between 1 and 200 characters.'] },
      },
      { status: 400, statusText: 'Bad Request' },
    );

    expect(captured).toBeInstanceOf(ApiError);
    expect((captured as ApiError).kind).toBe('validation');
    expect((captured as ApiError).message).toBe('Author must be between 1 and 200 characters.');
  });
});
