import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { QuoteDetail } from './quote-detail';
import type { QuoteDto } from '../quote';

describe('QuoteDetail', () => {
  let fixture: ComponentFixture<QuoteDetail>;
  let httpMock: HttpTestingController;

  const quoteA: QuoteDto = { id: 1, author: 'Marcus Aurelius', text: 'a', isDeleted: false, createdByUserId: 1 };
  const quoteB: QuoteDto = { id: 2, author: 'Seneca', text: 'b', isDeleted: false, createdByUserId: 1 };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [QuoteDetail],
      providers: [provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();

    fixture = TestBed.createComponent(QuoteDetail);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  it('shows a hint when no id is selected', () => {
    fixture.detectChanges();
    expect(fixture.componentInstance.data()).toBeNull();
    expect(fixture.componentInstance.loading()).toBe(false);
  });

  it('loads and displays the quote for the given id', () => {
    fixture.componentRef.setInput('id', 1);
    fixture.detectChanges();

    const req = httpMock.expectOne('/api/quotes/1');
    req.flush(quoteA);

    expect(fixture.componentInstance.data()).toEqual(quoteA);
    expect(fixture.componentInstance.loading()).toBe(false);
    expect(fixture.componentInstance.error()).toBeNull();
  });

  it('surfaces a 404 as a distinct not-found error, not a generic one', () => {
    fixture.componentRef.setInput('id', 999);
    fixture.detectChanges();

    const req = httpMock.expectOne('/api/quotes/999');
    req.flush(null, { status: 404, statusText: 'Not Found' });

    expect(fixture.componentInstance.data()).toBeNull();
    expect(fixture.componentInstance.error()).toContain('not found');
    // Distinct from the generic message used for other failures.
    expect(fixture.componentInstance.error()).not.toBe('Failed to load quote.');
  });

  it('surfaces non-404 failures as a generic error', () => {
    fixture.componentRef.setInput('id', 1);
    fixture.detectChanges();

    const req = httpMock.expectOne('/api/quotes/1');
    req.error(new ProgressEvent('network error'));

    expect(fixture.componentInstance.error()).toBe('Failed to load quote.');
  });

  it('never shows stale data: switching from A to B before A resolves cancels A and shows only B', () => {
    // Select quote A.
    fixture.componentRef.setInput('id', 1);
    fixture.detectChanges();
    const reqA = httpMock.expectOne('/api/quotes/1');

    // Before A responds, switch to quote B.
    fixture.componentRef.setInput('id', 2);
    fixture.detectChanges();

    // switchMap must have unsubscribed from A's request - HttpClient aborts
    // the underlying call on unsubscribe, which TestRequest surfaces here.
    expect(reqA.cancelled).toBe(true);

    const reqB = httpMock.expectOne('/api/quotes/2');

    // Simulate A's response arriving late, *after* B's request went out -
    // the real race described in the requirements. Because A was cancelled,
    // nothing is listening for it any more, so this must NOT be able to
    // overwrite whatever B ends up producing. (A cancelled TestRequest can't
    // be flushed at all - attempting it is itself proof there's no live
    // subscriber left to receive it.)
    expect(() => reqA.flush(quoteA)).toThrow();

    // B resolves normally and wins.
    reqB.flush(quoteB);

    expect(fixture.componentInstance.data()).toEqual(quoteB);
    expect(fixture.componentInstance.loading()).toBe(false);
  });
});
