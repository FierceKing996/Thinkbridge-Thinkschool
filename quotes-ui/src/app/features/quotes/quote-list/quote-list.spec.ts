import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { By } from '@angular/platform-browser';
import { provideRouter } from '@angular/router';

import { QuoteList } from './quote-list';
import { errorMappingInterceptor } from '../../../core/http/error-mapping-interceptor';
import type { QuoteDto } from '../../../core/models/quote';

describe('QuoteList', () => {
  let component: QuoteList;
  let fixture: ComponentFixture<QuoteList>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [QuoteList],
      // errorMappingInterceptor matches app.config.ts's real interceptor
      // chain for the error path, so the effect()'s error handler is
      // exercised against the same typed ApiError it receives in the real
      // app (see quote-list.ts's err instanceof ApiError check), not a raw
      // HttpErrorResponse. retryInterceptor is deliberately left out - it
      // has its own dedicated spec. provideRouter([]) satisfies the
      // RouterLink now on each row.
      providers: [
        provideHttpClient(withInterceptors([errorMappingInterceptor])),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(QuoteList);
    component = fixture.componentInstance;
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  function flushQuotes(quotes: QuoteDto[]) {
    fixture.detectChanges();
    const req = httpMock.expectOne('/api/quotes?page=1&size=10');
    req.flush(quotes);
  }

  it('should create', () => {
    flushQuotes([]);
    expect(component).toBeTruthy();
  });

  it('status is empty on a genuinely empty page', () => {
    flushQuotes([]);
    expect(component.status()).toBe('empty');
    expect(component.filteredQuotes()).toEqual([]);
  });

  it('renders each row as a link to that quote detail route', () => {
    flushQuotes([{ id: 7, author: 'Ada Lovelace', text: 'c', isDeleted: false, createdByUserId: 1 }]);
    fixture.detectChanges();

    const link = fixture.debugElement.query(By.css('li a')).nativeElement as HTMLAnchorElement;
    expect(link.getAttribute('href')).toBe('/quotes/7');
  });

  it('filteredQuotes recomputes when authorFilter changes, with no new HTTP call', () => {
    flushQuotes([
      { id: 1, author: 'Marcus Aurelius', text: 'a', isDeleted: false, createdByUserId: 1 },
      { id: 2, author: 'Seneca', text: 'b', isDeleted: false, createdByUserId: 1 },
    ]);

    expect(component.filteredQuotes().length).toBe(2);

    component.authorFilter.set('marcus');
    expect(component.filteredQuotes().length).toBe(1);
    expect(component.filteredQuotes()[0].author).toBe('Marcus Aurelius');

    httpMock.expectNone('/api/quotes?page=1&size=10');
  });

  it('status is empty when the filter matches nothing on a non-empty page', () => {
    flushQuotes([{ id: 1, author: 'Seneca', text: 'a', isDeleted: false, createdByUserId: 1 }]);

    component.authorFilter.set('nobody');
    expect(component.status()).toBe('empty');
  });

  it('refetches from the API when the page changes', () => {
    flushQuotes([]);

    component.nextPage();
    fixture.detectChanges(); // effect() reruns are scheduled, not synchronous - a flush is required

    const req = httpMock.expectOne('/api/quotes?page=2&size=10');
    req.flush([{ id: 3, author: 'Ada Lovelace', text: 'c', isDeleted: false, createdByUserId: 1 }]);

    expect(component.page()).toBe(2);
    expect(component.filteredQuotes().length).toBe(1);
  });

  // Regression coverage: a page/pageSize change while the previous
  // getQuotes() call is still in flight (e.g. mid-retry-backoff in
  // retryInterceptor - see retry-interceptor.ts) used to let the stale
  // first request's response arrive *after* the new page's response and
  // silently overwrite it, leaving the pager showing "Page 2" while the
  // list still showed page 1's data.
  it('never shows stale data: changing the page before the previous request resolves cancels it and shows only the new page', () => {
    fixture.detectChanges();
    const req1 = httpMock.expectOne('/api/quotes?page=1&size=10');

    // Before page 1's request resolves, move to page 2.
    component.nextPage();
    fixture.detectChanges();

    // switchMap must have unsubscribed from page 1's request - HttpClient
    // aborts the underlying call on unsubscribe, which TestRequest surfaces
    // here.
    expect(req1.cancelled).toBe(true);

    const req2 = httpMock.expectOne('/api/quotes?page=2&size=10');

    // Simulate page 1's response arriving late, *after* page 2's request
    // went out. Because page 1's request was cancelled, nothing is listening
    // for it any more, so this must NOT be able to overwrite what page 2
    // ends up producing. (A cancelled TestRequest can't be flushed at all.)
    expect(() =>
      req1.flush([{ id: 4, author: 'Grace Hopper', text: 'd', isDeleted: false, createdByUserId: 1 }]),
    ).toThrow();

    // Page 2 resolves normally (legitimately empty) and wins.
    req2.flush([]);

    expect(component.page()).toBe(2);
    expect(component.filteredQuotes()).toEqual([]);
    expect(component.status()).toBe('empty');
  });

  it('status is error when the request fails', () => {
    fixture.detectChanges();
    const req = httpMock.expectOne('/api/quotes?page=1&size=10');
    req.error(new ProgressEvent('network error'));

    expect(component.status()).toBe('error');
  });
});
