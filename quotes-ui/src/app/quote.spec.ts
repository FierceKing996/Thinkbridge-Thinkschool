import { TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { Quote } from './quote';
import type { QuoteDto } from './quote';

describe('Quote', () => {
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

  it('should be created', () => {
    expect(service).toBeTruthy();
  });

  it('createQuote POSTs exactly {author, text} to /api/quotes and returns the created quote', () => {
    const created: QuoteDto = { id: 5, author: 'Ada Lovelace', text: 'quote', isDeleted: false, createdByUserId: 1 };
    let result: QuoteDto | undefined;

    service.createQuote('Ada Lovelace', 'quote').subscribe((res) => (result = res));

    const req = httpMock.expectOne('/api/quotes');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ author: 'Ada Lovelace', text: 'quote' });
    req.flush(created, { status: 201, statusText: 'Created' });

    expect(result).toEqual(created);
  });
});
