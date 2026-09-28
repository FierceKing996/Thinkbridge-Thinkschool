import { inject } from '@angular/core';
import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { catchError, switchMap, throwError } from 'rxjs';
import { Auth } from './auth';

// Attaches a bearer token to writes only - GET /api/quotes,
// GET /api/quotes/{id} and GET /api/collections/{id} are unauthenticated per
// day1/QuotesApi (no RequireAuthorization on any of them), and there is
// nothing to attach for them.
//
// /api/auth/* is also excluded: Auth.login()/register()/refreshAccessToken()
// all POST through this same HttpClient, and attaching a (probably absent,
// or about-to-be-rotated) token to the very requests that fetch/rotate it is
// nonsense - skip them outright. This is also what avoids the interceptor
// recursively intercepting its own token-fetch/refresh request.
//
// On a 401 from a request that DID carry a token, this makes exactly one
// silent attempt to refresh the access token (via Auth.refreshAccessToken(),
// which itself coalesces concurrent callers) and retries the original
// request once with the new token. If the refresh itself fails - the
// refresh token is expired, revoked, or was reused (day1/QuotesApi's
// reuse-detection) - the session is genuinely over: Auth.logout() drops
// whatever's left locally and the ORIGINAL 401 is what propagates, so the
// caller's error handling (ApiError kind 'auth') is unaffected by the
// refresh attempt underneath it.
export const authInterceptor: HttpInterceptorFn = (req, next) => {
  if (req.url.includes('/api/auth/')) {
    return next(req);
  }

  const auth = inject(Auth);
  const token = auth.getToken();
  const authedReq =
    token !== null && req.method !== 'GET'
      ? req.clone({ setHeaders: { Authorization: `Bearer ${token}` } })
      : req;
  const tokenWasAttached = authedReq !== req;

  return next(authedReq).pipe(
    catchError((err: unknown) => {
      if (!(err instanceof HttpErrorResponse) || err.status !== 401 || !tokenWasAttached) {
        // Either not a 401, or there was no token to have gone stale in the
        // first place (an anonymous request, or a GET) - a refresh
        // couldn't fix either case.
        throw err;
      }

      return auth.refreshAccessToken().pipe(
        switchMap((newToken) => next(req.clone({ setHeaders: { Authorization: `Bearer ${newToken}` } }))),
        catchError(() => {
          auth.logout();
          // Surface the ORIGINAL 401, not the refresh failure - callers
          // already know how to handle a 401 (ApiError kind 'auth'); the
          // fact that a silent refresh was attempted and also failed is an
          // implementation detail, not a new error shape they need to
          // learn.
          return throwError(() => err);
        }),
      );
    }),
  );
};
