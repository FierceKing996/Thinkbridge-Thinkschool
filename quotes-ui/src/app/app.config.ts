import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { authInterceptor } from './auth-interceptor';
import { errorMappingInterceptor } from './error-mapping-interceptor';
import { retryInterceptor } from './retry-interceptor';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    // ng new --zoneless already omits zone.js from the build (no polyfills
    // entry in angular.json), but it did NOT add this - the schematic leaves
    // the app relying on zone.js's mere absence rather than the documented,
    // explicit opt-in. Added by hand so the app's zoneless-ness doesn't
    // depend on an omission nobody has to notice.
    provideZonelessChangeDetection(),
    // Order matters. withInterceptors([...]) wraps outer-to-inner in array
    // order: the first entry is outermost (runs first on the request path,
    // and - critically - LAST on the response/error path, since it's
    // subscribing to everything after it in the array). The last entry is
    // innermost, sitting directly against the backend.
    //
    // errorMappingInterceptor is listed FIRST (outermost) specifically so it
    // only sees the FINAL error, after retryInterceptor has already
    // exhausted its retries against the real HttpErrorResponse/network
    // failure - see error-mapping-interceptor.ts and retry-interceptor.ts.
    // If the order were reversed, retryInterceptor would be deciding whether
    // to retry based on an already-mapped ApiError that no longer carries
    // the real HTTP status, which would break its "only retry on status 0 or
    // 5xx, never a 4xx" rule.
    //
    // authInterceptor's position relative to the other two doesn't affect
    // correctness - it only touches non-GET requests (attaching the bearer
    // token on the way out), retryInterceptor only touches GET requests, so
    // the two never act on the same request - but it's kept in the middle so
    // the array reads request-shaping (auth) sitting between the two
    // response-shaping concerns (error mapping outside, retry inside).
    provideHttpClient(withInterceptors([errorMappingInterceptor, authInterceptor, retryInterceptor])),
  ]
};
