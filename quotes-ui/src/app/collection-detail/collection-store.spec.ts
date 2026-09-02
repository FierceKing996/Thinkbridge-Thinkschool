import { TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';

import { CollectionStore } from './collection-store';
import { errorMappingInterceptor } from '../error-mapping-interceptor';
import type { CollectionDetailDto, CollectionWriteDto } from '../collection';

// errorMappingInterceptor is wired in here to match app.config.ts's real
// chain: every failure the store sees in production is an already-mapped
// ApiError, so the specs must exercise that same path. This is what makes
// the duplicate/remove 400 assertions meaningful - they prove the precise
// server sentence ("Quote 3 is already in this collection.") actually
// reaches the store's error() signal in the running app, not just in a
// no-interceptor unit harness. retryInterceptor is left out (it only
// touches GETs and has its own spec).
describe('CollectionStore', () => {
  let store: CollectionStore;
  let httpMock: HttpTestingController;

  const readModel: CollectionDetailDto = {
    id: 1,
    name: 'Now allowed',
    ownerId: 1,
    items: [
      { quoteId: 2, author: 'Grace Hopper', text: 'A ship in port is safe...', addedAt: '2026-09-02T05:00:08.22+00:00' },
    ],
  };

  // Write-model response - NOTE: items carry NO author/text.
  function writeModel(quoteIds: number[]): CollectionWriteDto {
    return {
      id: 1,
      name: 'Now allowed',
      ownerId: 1,
      items: quoteIds.map((quoteId) => ({ quoteId, addedAt: `2026-09-02T06:00:0${quoteId}.00+00:00` })),
    };
  }

  beforeEach(() => {
    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([errorMappingInterceptor])),
        provideHttpClientTesting(),
        CollectionStore,
      ],
    });
    store = TestBed.inject(CollectionStore);
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => httpMock.verify());

  function loadWith(dto: CollectionDetailDto): void {
    store.load(dto.id);
    httpMock.expectOne(`/api/collections/${dto.id}`).flush(dto);
  }

  // --- load ------------------------------------------------------------

  it('goes loading -> loaded and populates the enriched read model', () => {
    store.load(1);
    expect(store.status()).toBe('loading');

    httpMock.expectOne('/api/collections/1').flush(readModel);

    expect(store.status()).toBe('loaded');
    expect(store.id()).toBe(1);
    expect(store.name()).toBe('Now allowed');
    expect(store.items()).toEqual(readModel.items);
    expect(store.itemCount()).toBe(1);
    expect(store.isEmpty()).toBe(false);
  });

  it('load: empty collection -> isEmpty', () => {
    store.load(1);
    httpMock.expectOne('/api/collections/1').flush({ ...readModel, items: [] });

    expect(store.status()).toBe('loaded');
    expect(store.isEmpty()).toBe(true);
    expect(store.itemCount()).toBe(0);
  });

  it('load: 404 -> distinct "collection not found" error + errorKind notFound', () => {
    store.load(7);
    httpMock.expectOne('/api/collections/7').flush(null, { status: 404, statusText: 'Not Found' });

    expect(store.status()).toBe('error');
    expect(store.errorKind()).toBe('notFound');
    expect(store.error()).toBe('Collection 7 was not found.');
  });

  it('load: network failure -> error state, errorKind network, generic message', () => {
    store.load(1);
    httpMock.expectOne('/api/collections/1').error(new ProgressEvent('network error'));

    expect(store.status()).toBe('error');
    expect(store.errorKind()).toBe('network');
    expect(store.error()).not.toBe('Collection 1 was not found.');
  });

  // --- add -----------------------------------------------------------

  it('addItem: optimistic placeholder, then reconciles WITHOUT wiping author/text off existing rows', () => {
    loadWith(readModel);

    store.addItem(3);

    // Optimistic: placeholder row + pendingAdds tracks the quoteId.
    expect(store.pendingAdds().has(3)).toBe(true);
    expect(store.isBusy()).toBe(true);
    expect(store.items().map((i) => i.quoteId)).toEqual([2, 3]);
    expect(store.items()[1]).toMatchObject({ quoteId: 3, author: '', text: '' });

    const req = httpMock.expectOne('/api/collections/1/items');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ quoteId: 3 });
    req.flush(writeModel([2, 3]));

    // THE TRAP: the write model had no author/text. Quote 2 must still be
    // enriched; quote 3 stays a placeholder (no enrichment source).
    expect(store.items().find((i) => i.quoteId === 2)).toMatchObject({
      author: 'Grace Hopper',
      text: 'A ship in port is safe...',
    });
    expect(store.items().find((i) => i.quoteId === 3)).toMatchObject({ author: '', text: '' });
    expect(store.pendingAdds().has(3)).toBe(false);
    expect(store.isBusy()).toBe(false);
    expect(store.itemCount()).toBe(2);
  });

  it('addItem: a second call for a quoteId already pending is ignored (no duplicate request)', () => {
    loadWith(readModel);

    store.addItem(3);
    store.addItem(3);

    httpMock.expectOne('/api/collections/1/items').flush(writeModel([2, 3]));
    httpMock.expectNone('/api/collections/1/items');
  });

  it('addItem: FAILED add (duplicate -> 400) rolls back the placeholder and surfaces the server message', () => {
    loadWith(readModel);

    store.addItem(3);
    expect(store.items().map((i) => i.quoteId)).toEqual([2, 3]);

    httpMock.expectOne('/api/collections/1/items').flush(
      { errors: { QuoteId: ['Quote 3 is already in this collection.'] }, status: 400, title: 'Bad Request' },
      { status: 400, statusText: 'Bad Request' },
    );

    // Rolled back to just the original enriched row.
    expect(store.items()).toEqual(readModel.items);
    expect(store.pendingAdds().has(3)).toBe(false);
    expect(store.errorKind()).toBe('validation');
    expect(store.error()).toBe('Quote 3 is already in this collection.');
  });

  it('addItem: a 400 for a quoteId that WAS already present does not delete the real row', () => {
    loadWith(readModel);

    // quoteId 2 is already an enriched member.
    store.addItem(2);
    httpMock.expectOne('/api/collections/1/items').flush(
      { errors: { QuoteId: ['Quote 2 is already in this collection.'] } },
      { status: 400, statusText: 'Bad Request' },
    );

    expect(store.items()).toEqual(readModel.items);
    expect(store.error()).toBe('Quote 2 is already in this collection.');
  });

  // --- remove ------------------------------------------------------------

  it('removeItem: optimistic remove, reconciles from the write model, keeps other rows enriched', () => {
    const twoRows: CollectionDetailDto = {
      ...readModel,
      items: [
        readModel.items[0],
        { quoteId: 3, author: 'Ada Lovelace', text: 'That brain of mine...', addedAt: '2026-09-02T05:10:00+00:00' },
      ],
    };
    loadWith(twoRows);

    store.removeItem(2);

    expect(store.pendingRemoves().has(2)).toBe(true);
    expect(store.items().map((i) => i.quoteId)).toEqual([3]);

    const req = httpMock.expectOne('/api/collections/1/items/2');
    expect(req.request.method).toBe('DELETE');
    req.flush(writeModel([3]));

    expect(store.items().length).toBe(1);
    // Enriched author/text kept; addedAt refreshed from the write model.
    expect(store.items()[0]).toMatchObject({
      quoteId: 3,
      author: 'Ada Lovelace',
      text: 'That brain of mine...',
    });
    expect(store.items()[0].addedAt).toBe('2026-09-02T06:00:03.00+00:00');
    expect(store.pendingRemoves().has(2)).toBe(false);
  });

  it('removeItem: FAILED remove (quote not in collection -> 400) re-inserts the row and surfaces the message', () => {
    loadWith(readModel);

    store.removeItem(2);
    expect(store.items()).toEqual([]);

    httpMock.expectOne('/api/collections/1/items/2').flush(
      { errors: { quoteId: ['Quote 2 is not in this collection.'] } },
      { status: 400, statusText: 'Bad Request' },
    );

    expect(store.items()).toEqual(readModel.items);
    expect(store.pendingRemoves().has(2)).toBe(false);
    expect(store.error()).toBe('Quote 2 is not in this collection.');
  });

  // --- concurrency -----------------------------------------------------

  it('concurrent adds for different quoteIds: both tracked in pendingAdds, both reconcile', () => {
    loadWith(readModel);

    store.addItem(3);
    store.addItem(4);

    expect(store.pendingAdds().has(3)).toBe(true);
    expect(store.pendingAdds().has(4)).toBe(true);
    expect(store.items().map((i) => i.quoteId)).toEqual([2, 3, 4]);

    const posts = httpMock.match(
      (r) => r.url === '/api/collections/1/items' && r.method === 'POST',
    );
    expect(posts.length).toBe(2);
    const post3 = posts.find((r) => r.request.body.quoteId === 3)!;
    const post4 = posts.find((r) => r.request.body.quoteId === 4)!;

    // Reconcile the second to land first - order must not matter.
    post4.flush(writeModel([2, 3, 4]));
    expect(store.pendingAdds().has(4)).toBe(false);
    expect(store.pendingAdds().has(3)).toBe(true);

    post3.flush(writeModel([2, 3, 4]));
    expect(store.pendingAdds().size).toBe(0);

    expect(store.items().map((i) => i.quoteId).sort()).toEqual([2, 3, 4]);
    // Quote 2 never lost its enrichment through either reconcile.
    expect(store.items().find((i) => i.quoteId === 2)).toMatchObject({ author: 'Grace Hopper' });
  });

  it('overlapping addItem + removeItem: each pending set tracked independently, both reconcile', () => {
    loadWith(readModel); // items: [2]

    store.addItem(3);
    store.removeItem(2);

    expect(store.pendingAdds().has(3)).toBe(true);
    expect(store.pendingRemoves().has(2)).toBe(true);
    expect(store.isBusy()).toBe(true);

    // Model the server serialising them: DELETE lands first (collection now
    // empty), then POST (collection now just [3]).
    httpMock.expectOne('/api/collections/1/items/2').flush(writeModel([]));
    expect(store.pendingRemoves().size).toBe(0);
    expect(store.items()).toEqual([]);

    httpMock.expectOne('/api/collections/1/items').flush(writeModel([3]));
    expect(store.pendingAdds().size).toBe(0);
    expect(store.isBusy()).toBe(false);

    expect(store.items().map((i) => i.quoteId)).toEqual([3]);
  });
});
