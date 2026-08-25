import { Injectable, inject } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { Observable, map, shareReplay } from 'rxjs';

// Real response shape from POST /api/auth/login (day1/QuotesApi/Auth.cs,
// LoginResponse) - snake_case on purpose (OAuth2-style), unlike the rest of
// this API's camelCase. Getting this wrong silently produces undefined.
export interface LoginResponseDto {
  access_token: string;
  refresh_token: string;
  expires_in: number;
}

@Injectable({ providedIn: 'root' })
export class Auth {
  private readonly http = inject(HttpClient);

  // Demo-only: the seeded user from day1/QuotesApi/Program.cs, scoped
  // "quotes.write" - the scope POST /api/quotes' can-edit-quotes policy
  // requires. No login UI for this exercise; cached for the app's lifetime.
  private token$: Observable<string> | null = null;

  getToken(): Observable<string> {
    this.token$ ??= this.http
      .post<LoginResponseDto>('/api/auth/login', {
        email: 'demo@quotesapi.dev',
        password: 'correct-horse-battery-staple',
      })
      .pipe(
        map((res) => res.access_token),
        shareReplay(1),
      );
    return this.token$;
  }
}
