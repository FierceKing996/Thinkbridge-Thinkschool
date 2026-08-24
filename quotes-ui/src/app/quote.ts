import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable } from 'rxjs';

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
}
