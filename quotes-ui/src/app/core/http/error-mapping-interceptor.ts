import { HttpInterceptorFn } from '@angular/common/http';
import { catchError, throwError } from 'rxjs';
import { mapToApiError } from './api-error';

// Catches whatever comes back from the backend (or from retryInterceptor,
// once retries are exhausted) and rethrows the typed, friendly ApiError
// instead - see api-error.ts's mapToApiError for the actual mapping rules.
//
// Must sit outermost (first) in app.config.ts's withInterceptors([...])
// array so it only runs once, AFTER retryInterceptor has already had the
// chance to retry against the real HttpErrorResponse/network failure - if
// this were positioned inside (closer to the backend than) retryInterceptor,
// retryInterceptor would be making retry decisions against an already-mapped
// ApiError that's lost the real HTTP status, which is exactly what this
// ordering avoids. See retry-interceptor.ts and app.config.ts for the full
// reasoning.
export const errorMappingInterceptor: HttpInterceptorFn = (req, next) =>
  next(req).pipe(catchError((err: unknown) => throwError(() => mapToApiError(err))));
