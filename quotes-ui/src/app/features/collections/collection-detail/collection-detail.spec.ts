import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { provideRouter } from '@angular/router';
import { By } from '@angular/platform-browser';

import { CollectionDetail } from './collection-detail';
import { errorMappingInterceptor } from '../../../core/http/error-mapping-interceptor';
import type { CollectionDetailDto } from '../../../core/models/collection';
import type { QuoteDto } from '../../../core/models/quote';

// errorMappingInterceptor is wired in (matching app.config.ts's real chain
// for the error path) so the store's normaliseError() is exercised against
// the same typed ApiError the component sees in the running app - which is
// what makes the kind==='auth' -> "Sign in" link branch real.
describe('CollectionDetail', () => {
  let fixture: ComponentFixture<CollectionDetail>;
  let httpMock: HttpTestingController;

  const readModel: CollectionDetailDto = {
    id: 1,
    name: 'Now allowed',
    ownerId: 1,
    items: [
      { quoteId: 2, author: 'Grace Hopper', text: 'A ship in port is safe...', addedAt: '2026-09-02T05:00:08.22+00:00' },
    ],
  };

  const pickerQuotes: QuoteDto[] = [
    { id: 2, author: 'Grace Hopper', text: 'A ship in port is safe...', isDeleted: false, createdByUserId: 1 },
    { id: 4, author: 'Alan Turing', text: 'Machines take me by surprise...', isDeleted: false, createdByUserId: 1 },
  ];

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [CollectionDetail],
      providers: [
        provideHttpClient(withInterceptors([errorMappingInterceptor])),
        provideHttpClientTesting(),
        provideRouter([]),
      ],
    }).compileComponents();

    fixture = TestBed.createComponent(CollectionDetail);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  // The ':id' route param arrives as a STRING via withComponentInputBinding().
  function setId(id: string): void {
    fixture.componentRef.setInput('id', id);
    fixture.detectChanges();
  }

  // The component always fires GET /api/quotes (the add picker source) from
  // its constructor, regardless of the id. Flush it so verify() is happy.
  function flushPicker(quotes: QuoteDto[] = pickerQuotes): void {
    httpMock.expectOne('/api/quotes?page=1&size=50').flush(quotes);
  }

  it('treats a non-numeric param as invalid and makes no collections call', () => {
    setId('abc');
    flushPicker();

    expect(fixture.componentInstance.invalidParam()).toBe(true);
    expect(fixture.nativeElement.textContent).toContain("doesn't look like a valid collection id");
    httpMock.expectNone((r) => r.url.startsWith('/api/collections/'));
  });

  it('renders the loading state, then the loaded list with author + text', () => {
    setId('1');
    flushPicker();

    expect(fixture.nativeElement.textContent).toContain('Loading collection...');

    httpMock.expectOne('/api/collections/1').flush(readModel);
    fixture.detectChanges();

    const row = fixture.debugElement.query(By.css('.items li')).nativeElement as HTMLElement;
    expect(row.textContent).toContain('Grace Hopper');
    expect(row.textContent).toContain('A ship in port is safe...');
    expect(row.querySelector('button')!.textContent).toContain('Remove');
  });

  it('renders the empty state when the collection has no items', () => {
    setId('1');
    flushPicker();
    httpMock.expectOne('/api/collections/1').flush({ ...readModel, items: [] });
    fixture.detectChanges();

    expect(fixture.nativeElement.querySelector('.empty').textContent).toContain('no quotes yet');
  });

  it('renders a distinct not-found error for a 404', () => {
    setId('9');
    flushPicker();
    httpMock.expectOne('/api/collections/9').flush(null, { status: 404, statusText: 'Not Found' });
    fixture.detectChanges();

    expect(fixture.nativeElement.textContent).toContain('Collection 9 was not found.');
  });

  it('add control POSTs the picked quoteId', () => {
    setId('1');
    flushPicker();
    httpMock.expectOne('/api/collections/1').flush(readModel);
    fixture.detectChanges();

    const select = fixture.debugElement.query(By.css('#quote-picker')).nativeElement as HTMLSelectElement;
    // Quote 2 is already in the collection, so only quote 4 is offered.
    expect(Array.from(select.options).map((o) => o.value)).toEqual(['', '4']);
    select.value = '4';
    select.dispatchEvent(new Event('change'));
    fixture.detectChanges();

    (fixture.debugElement.query(By.css('form.add')).nativeElement as HTMLFormElement).dispatchEvent(
      new Event('submit', { cancelable: true }),
    );

    const req = httpMock.expectOne('/api/collections/1/items');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ quoteId: 4 });
    req.flush({ id: 1, name: 'Now allowed', ownerId: 1, items: [{ quoteId: 2, addedAt: 'x' }, { quoteId: 4, addedAt: 'y' }] });
  });

  it('shows a Sign in link when an add fails with an auth error', () => {
    setId('1');
    flushPicker();
    httpMock.expectOne('/api/collections/1').flush(readModel);
    fixture.detectChanges();

    fixture.componentInstance.selectedQuoteId.set(4);
    fixture.componentInstance.add();

    httpMock.expectOne('/api/collections/1/items').flush(null, { status: 401, statusText: 'Unauthorized' });
    fixture.detectChanges();

    const banner = fixture.nativeElement.querySelector('.banner') as HTMLElement;
    expect(banner).toBeTruthy();
    const link = banner.querySelector('a') as HTMLAnchorElement;
    expect(link.getAttribute('href')).toBe('/login');
  });
});
