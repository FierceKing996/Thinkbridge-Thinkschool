import { HttpErrorResponse, HttpInterceptorFn } from '@angular/common/http';
import { retry, timer } from 'rxjs';

// Total attempts = 1 (the original) + MAX_RETRIES. 3 total attempts is a
// reasonable default for a small demo API and doesn't need to be
// configurable.
const MAX_RETRIES = 2;
const BASE_DELAY_MS = 250;

// Only a network failure (status 0 - no response reached the client at all:
// offline, DNS failure, connection refused, a rejected CORS preflight) or a
// 5xx (the server itself failed) is worth retrying. Both are the kind of
// failure that can plausibly succeed on a second attempt with no side
// effects either way. A 4xx is never transient - a 404 won't start existing
// because you asked again, and blindly retrying a 401 in particular risks a
// confusing repeated-auth-attempt loop - so every 4xx must fall through
// untouched.
function isTransientFailure(error: unknown): boolean {
  return error instanceof HttpErrorResponse && (error.status === 0 || error.status >= 500);
}

// Retries idempotent GETs only, with exponential backoff, on transient
// failures only. Must sit closest to the backend in app.config.ts's
// withInterceptors([...]) array (last in the array = innermost = it wraps
// next(req) directly) so that the error it inspects here is the RAW
// HttpErrorResponse/network failure from the backend, not an already-mapped
// ApiError from errorMappingInterceptor that has lost the real status code -
// see error-mapping-interceptor.ts and app.config.ts for the full reasoning.
//
// POST/PUT/DELETE are never retried here - this app only ever does a
// non-idempotent write via POST /api/quotes, and blindly retrying that on a
// transient failure could double-create a quote if the first attempt's
// response was merely lost, not the request itself.
export const retryInterceptor: HttpInterceptorFn = (req, next) => {
  if (req.method !== 'GET') {
    return next(req);
  }

  return next(req).pipe(
    retry({
      count: MAX_RETRIES,
      delay: (error, retryCount) => {
        if (!isTransientFailure(error)) {
          // Rethrowing the original error (not a new one) here is what
          // makes retry() stop immediately and propagate it unchanged -
          // RxJS's retry() treats a throw from the delay selector as "give
          // up now", passing whatever is thrown straight through as the
          // resulting error.
          throw error;
        }
        // Exponential backoff: 250ms, then 500ms.
        return timer(BASE_DELAY_MS * 2 ** (retryCount - 1));
      },
    }),
  );
};
