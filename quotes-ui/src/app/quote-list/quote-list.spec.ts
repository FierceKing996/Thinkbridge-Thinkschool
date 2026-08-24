import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { QuoteList } from './quote-list';
import type { QuoteDto } from '../quote';

describe('QuoteList', () => {
  let component: QuoteList;
  let fixture: ComponentFixture<QuoteList>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [QuoteList],
      providers: [provideHttpClient(), provideHttpClientTesting()],
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

  it('status is error when the request fails', () => {
    fixture.detectChanges();
    const req = httpMock.expectOne('/api/quotes?page=1&size=10');
    req.error(new ProgressEvent('network error'));

    expect(component.status()).toBe('error');
  });
});
