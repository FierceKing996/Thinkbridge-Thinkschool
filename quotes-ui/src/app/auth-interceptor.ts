import { inject } from '@angular/core';
import { HttpInterceptorFn } from '@angular/common/http';
import { switchMap } from 'rxjs';
import { Auth } from './auth';

// Attaches a bearer token to writes only - GET /api/quotes and
// GET /api/quotes/{id} are unauthenticated per Extension.cs (no
// RequireAuthorization on either), and calling the token endpoint for them
// would just be an extra unnecessary round trip.
//
// /api/auth/* must also be excluded, not just GET: Auth.getToken() calls
// POST /api/auth/login through this same HttpClient, so without this
// exclusion the interceptor intercepts its own token-fetching request -
// which needs a token, which needs this request to finish first. That's a
// real subscription deadlock (found live: the create-quote form got stuck
// on "Creating..." forever, zero network requests ever fired, because
// firstValueFrom() was awaiting an Observable that could never settle).
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  if (req.method === 'GET' || req.url.includes('/api/auth/')) {
    return next(req);
  }

  const auth = inject(Auth);
  return auth.getToken().pipe(
    switchMap((token) => next(req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }))),
  );
};
