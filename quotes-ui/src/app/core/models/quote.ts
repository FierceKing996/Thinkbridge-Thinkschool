import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

// Request body for POST /api/quotes - day1/QuotesApi/Extension.cs
// MapQuoteEndpoints, matches CreateQuoteRequest(string Author, string Text)
// exactly. Nothing else is accepted.
export interface CreateQuoteRequest {
  author: string;
  text: string;
}

// Matches QuotesApi's Quote entity (day1/QuotesApi/Quote.cs) as serialized by
// System.Text.Json's default camelCase output - these field names are the
// real API's, not invented for this exercise.
export interface QuoteDto {
  id: number;
  author: string;
  text: string;
  isDeleted: boolean;
  createdByUserId: number;
}

@Injectable({
  providedIn: 'root',
})
export class Quote {
  private readonly http = inject(HttpClient);

  // GET /api/quotes?page=&size= - day1/QuotesApi/Extension.cs MapQuoteEndpoints,
  // no auth required. page defaults to 1, size to 10 server-side when omitted.
  getQuotes(page: number, size: number): Observable<QuoteDto[]> {
    return this.http.get<QuoteDto[]>(`/api/quotes?page=${page}&size=${size}`);
  }

  // GET /api/quotes/{id} - day1/QuotesApi/Extension.cs MapQuoteEndpoints, no
  // auth required. 200 with the quote if found, 404 with an empty body
  // (surfaces as an HttpErrorResponse with status 404) if not.
  getQuoteById(id: number): Observable<QuoteDto> {
    return this.http.get<QuoteDto>(`/api/quotes/${id}`);
  }

  // POST /api/quotes - day1/QuotesApi/Extension.cs MapQuoteEndpoints, requires
  // "can-edit-quotes" (quotes.write scope). The auth interceptor attaches the
  // bearer token automatically since this is a non-GET request - no auth
  // handling needed here. 201 with the created Quote on success; 400 with a
  // ValidationProblemDetails body (all errors under the single "error" key,
  // never per-field) if Quote.Create()'s invariants are violated; a 401/403
  // or network failure both surface as an HttpErrorResponse for the caller to
  // distinguish.
  createQuote(author: string, text: string): Observable<QuoteDto> {
    const body: CreateQuoteRequest = { author, text };
    return this.http.post<QuoteDto>('/api/quotes', body);
  }
}
