import { inject } from '@angular/core';
import { HttpInterceptorFn } from '@angular/common/http';
import { Auth } from './auth';

// Attaches a bearer token to writes only - GET /api/quotes and
// GET /api/quotes/{id} are unauthenticated per Extension.cs (no
// RequireAuthorization on either), and there is nothing to attach for them.
//
// /api/auth/* is also excluded: Auth.login() POSTs to /api/auth/login
// through this same HttpClient, and attaching a (probably absent) token to
// the very request that fetches the token is nonsense - skip it outright.
//
// getToken() is now synchronous (Auth no longer logs in as a side effect -
// see auth.ts). If a token is present we clone the request with the
// Authorization header; if not, the request goes out unauthenticated and the
// server's 401 flows back through the normal ApiError path. In practice the
// only write route ('quotes/new') sits behind authGuard, so a token is
// always set by the time a POST /api/quotes is made.
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  if (req.method === 'GET' || req.url.includes('/api/auth/')) {
    return next(req);
  }

  const token = inject(Auth).getToken();
  if (token === null) {
    return next(req);
  }

  return next(req.clone({ setHeaders: { Authorization: `Bearer ${token}` } }));
};
