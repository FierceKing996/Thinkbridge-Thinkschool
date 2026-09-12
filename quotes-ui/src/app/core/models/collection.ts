import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

// The week-1 QuotesApi (day1/QuotesApi) serves a collection with TWO
// genuinely different item shapes depending on the endpoint, verified
// against the live server:
//
//   - the READ model (GET /api/collections/{id}) enriches every item with
//     its author + text, joined in from the quotes table.
//   - the WRITE model (POST/DELETE .../items) returns the collection with
//     items that are BARE references - quoteId + addedAt only, no author,
//     no text.
//
// These are modelled as two separate interface families on purpose so a
// consumer can't accidentally read `.author` off a write-model response
// (it isn't there) - the store (collection-store.ts) has to reconcile the
// two explicitly. Field names are the real API's camelCase output, not
// invented for this exercise.

// One enriched item from the READ model.
export interface CollectionItemDto {
  quoteId: number;
  author: string;
  text: string;
  addedAt: string;
}

// GET /api/collections/{id} - the read model. `items` is always present
// ([] when the collection is empty).
export interface CollectionDetailDto {
  id: number;
  name: string;
  ownerId: number;
  items: CollectionItemDto[];
}

// One bare item reference from the WRITE model - NO author/text.
export interface CollectionItemRefDto {
  quoteId: number;
  addedAt: string;
}

// POST /api/collections/{id}/items and DELETE /api/collections/{id}/items/
// {quoteId} both return this - the collection with UN-enriched items.
export interface CollectionWriteDto {
  id: number;
  name: string;
  ownerId: number;
  items: CollectionItemRefDto[];
}

// Request body for POST /api/collections/{id}/items - matches the real
// server's AddItemRequest (day1/QuotesApi), which accepts exactly this one
// field. Nothing else is read.
export interface AddCollectionItemRequest {
  quoteId: number;
}

@Injectable({
  providedIn: 'root',
})
export class Collection {
  // inject(), not a constructor parameter - matches Quote's convention.
  private readonly http = inject(HttpClient);

  // GET /api/collections/{id} - day1/QuotesApi, NO auth required. Returns
  // the READ model (CollectionDetailDto): items carry author + text. 200
  // with the collection if found; 404 with an EMPTY body (surfaces as an
  // HttpErrorResponse with status 404, then a typed ApiError with
  // kind === 'notFound' once it passes back through
  // errorMappingInterceptor) if the id doesn't exist.
  getCollection(id: number): Observable<CollectionDetailDto> {
    return this.http.get<CollectionDetailDto>(`/api/collections/${id}`);
  }

  // POST /api/collections/{id}/items - day1/QuotesApi, REQUIRES auth
  // (bearer). authInterceptor attaches the token automatically for this
  // non-GET request - no auth handling needed here. Returns the WRITE
  // model (CollectionWriteDto): items are bare { quoteId, addedAt }, with
  // NO author/text - do not read those off this response.
  //
  // 200 with the updated collection on success. 400 with a
  // ValidationProblemDetails body ({ errors: { QuoteId: [msg] }, ... }) on
  // a domain-rule break (duplicate quoteId; more than 50 items). 404 if
  // the collection id doesn't exist. 401 with no/expired token.
  addItem(id: number, quoteId: number): Observable<CollectionWriteDto> {
    const body: AddCollectionItemRequest = { quoteId };
    return this.http.post<CollectionWriteDto>(`/api/collections/${id}/items`, body);
  }

  // DELETE /api/collections/{id}/items/{quoteId} - day1/QuotesApi,
  // REQUIRES auth (bearer, attached by authInterceptor). Returns the same
  // WRITE model (CollectionWriteDto) as addItem - bare items, NO
  // author/text.
  //
  // 200 with the updated collection on success. 400 with
  // { errors: { quoteId: [msg] }, ... } when the quote isn't in the
  // collection. 404 if the collection id doesn't exist.
  removeItem(id: number, quoteId: number): Observable<CollectionWriteDto> {
    return this.http.delete<CollectionWriteDto>(`/api/collections/${id}/items/${quoteId}`);
  }
}
