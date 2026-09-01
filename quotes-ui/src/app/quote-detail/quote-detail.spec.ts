import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';

import { QuoteDetail } from './quote-detail';
import { errorMappingInterceptor } from '../error-mapping-interceptor';
import type { QuoteDto } from '../quote';

describe('QuoteDetail', () => {
  let fixture: ComponentFixture<QuoteDetail>;
  let httpMock: HttpTestingController;

  const quoteA: QuoteDto = { id: 1, author: 'Marcus Aurelius', text: 'a', isDeleted: false, createdByUserId: 1 };
  const quoteB: QuoteDto = { id: 2, author: 'Seneca', text: 'b', isDeleted: false, createdByUserId: 1 };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [QuoteDetail],
      // errorMappingInterceptor matches app.config.ts's real interceptor
      // chain for the error path, so the component's catchError is exercised
      // against the same typed ApiError it receives in the real app (see
      // quote-detail.ts's err instanceof ApiError check), not a raw
      // HttpErrorResponse. retryInterceptor is deliberately left out - GET
      // requests never retry in this suite, keeping these tests synchronous.
      // provideRouter([]) satisfies the RouterLink in the template.
      providers: [
        provideHttpClient(withInterceptors([errorMappingInterceptor])),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(QuoteDetail);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  // The ':id' route param arrives as a STRING via withComponentInputBinding()
  // - setInput mirrors that here.
  function setId(id: string): void {
    fixture.componentRef.setInput('id', id);
    fixture.detectChanges();
  }

  it('treats a missing param as invalid and makes no API call', () => {
    fixture.detectChanges();
    expect(fixture.componentInstance.invalidParam()).toBe(true);
    expect(fixture.componentInstance.loading()).toBe(false);
    httpMock.expectNone(() => true);
  });

  it('treats a non-numeric param as invalid and makes no API call', () => {
    setId('abc');
    expect(fixture.componentInstance.invalidParam()).toBe(true);
    expect(fixture.componentInstance.data()).toBeNull();
    expect(fixture.componentInstance.error()).toBeNull();
    httpMock.expectNone(() => true);
  });

  it('treats a non-integer numeric param (1.5) as invalid and makes no API call', () => {
    setId('1.5');
    expect(fixture.componentInstance.invalidParam()).toBe(true);
    httpMock.expectNone(() => true);
  });

  it('loads and displays the quote for a valid numeric id', () => {
    setId('1');

    const req = httpMock.expectOne('/api/quotes/1');
    req.flush(quoteA);

    expect(fixture.componentInstance.invalidParam()).toBe(false);
    expect(fixture.componentInstance.data()).toEqual(quoteA);
    expect(fixture.componentInstance.loading()).toBe(false);
    expect(fixture.componentInstance.error()).toBeNull();
  });

  it('surfaces a 404 (well-formed id, no such quote) as a distinct not-found error', () => {
    setId('999');

    const req = httpMock.expectOne('/api/quotes/999');
    req.flush(null, { status: 404, statusText: 'Not Found' });

    expect(fixture.componentInstance.invalidParam()).toBe(false);
    expect(fixture.componentInstance.data()).toBeNull();
    expect(fixture.componentInstance.error()).toContain('not found');
    expect(fixture.componentInstance.error()).not.toBe('Failed to load quote.');
  });

  it('surfaces non-404 failures as a generic error', () => {
    setId('1');

    const req = httpMock.expectOne('/api/quotes/1');
    req.error(new ProgressEvent('network error'));

    expect(fixture.componentInstance.error()).toBe('Failed to load quote.');
  });

  it('never shows stale data: switching from 1 to 2 before 1 resolves cancels 1 and shows only 2', () => {
    setId('1');
    const reqA = httpMock.expectOne('/api/quotes/1');

    setId('2');

    // switchMap must have unsubscribed from 1's request - HttpClient aborts
    // the underlying call on unsubscribe, which TestRequest surfaces here.
    expect(reqA.cancelled).toBe(true);

    const reqB = httpMock.expectOne('/api/quotes/2');

    // 1's response arriving late must not be able to overwrite 2's - a
    // cancelled TestRequest can't be flushed at all.
    expect(() => reqA.flush(quoteA)).toThrow();

    reqB.flush(quoteB);

    expect(fixture.componentInstance.data()).toEqual(quoteB);
    expect(fixture.componentInstance.loading()).toBe(false);
  });
});
