import { HttpErrorResponse } from '@angular/common/http';

// The real backend (day1/QuotesApi) produces exactly two different 4xx
// shapes, verified against live responses - see error-mapping-interceptor.ts
// for where this gets wired in:
//   - A real ValidationProblemDetails JSON body on 400s from
//     Results.ValidationProblem() (Extension.cs), always with every message
//     under a single literal "error" key, never per-field.
//   - A genuinely EMPTY body (Content-Length: 0) on 404s/401s from
//     Results.NotFound()/Results.Unauthorized() - this API has
//     AddProblemDetails() registered but no UseStatusCodePages()/
//     UseExceptionHandler wiring for status-code results, so those two never
//     get auto-wrapped into a ProblemDetails body here.
// ApiError exists so every consumer deals with ONE typed shape instead of
// each hand-rolling its own HttpErrorResponse/err.status checks (which is
// what create-quote.ts, create-quote-reactive.ts and quote-detail.ts used to
// do, duplicated three times).
export type ApiErrorKind = 'validation' | 'notFound' | 'auth' | 'conflict' | 'network' | 'server';

// A plain class (not just an interface) extending Error, not a bare object -
// so existing `err instanceof Error` style checks (see quote-list.ts) keep
// working, `throw`/`throwError(() => ...)` behave normally, and consumers
// get a real stack trace during debugging instead of an opaque object.
export class ApiError extends Error {
  readonly kind: ApiErrorKind;
  // null when there's no real HTTP status to report at all (e.g. something
  // thrown before a request was even attempted) - status 0 (a genuine
  // network failure) is reported as 0, not folded into null, so callers that
  // care can still tell "no network" apart from "no response object".
  readonly status: number | null;

  constructor(kind: ApiErrorKind, message: string, status: number | null) {
    super(message);
    this.name = 'ApiError';
    this.kind = kind;
    this.status = status;
    // Restores the prototype chain so `instanceof ApiError` and
    // `instanceof Error` both work correctly when compiled down - extending
    // built-ins like Error is otherwise broken by TS's ES5 downlevelling.
    Object.setPrototypeOf(this, ApiError.prototype);
  }
}

const GENERIC_VALIDATION_MESSAGE = 'That request could not be completed.';
const GENERIC_AUTH_MESSAGE = 'You are not authorized to perform this action. Please sign in again.';
const GENERIC_CONFLICT_MESSAGE = 'That could not be completed because of a conflict with existing data.';
const GENERIC_NOT_FOUND_MESSAGE = 'The requested item was not found.';
const GENERIC_NETWORK_MESSAGE = 'Could not reach the server. Check your connection and try again.';
const GENERIC_SERVER_MESSAGE = 'The server encountered an error. Please try again later.';
const GENERIC_UNKNOWN_MESSAGE = 'An unexpected error occurred. Please try again.';

// 400 bodies from this API don't all use the same key. The quotes endpoints
// (Extension.cs MapQuoteEndpoints) put every message under a single literal
// "error" key - Results.ValidationProblem(new Dictionary { ["error"] = [...] }).
// The collections endpoints instead key by field name: POST .../items ->
// { errors: { "QuoteId": [...] } } (nameof(req.QuoteId)), DELETE .../items/{id}
// -> { errors: { "quoteId": [...] } } (nameof the route param), POST
// /api/collections -> { errors: { "Name": [...] } }. So: prefer "error" for
// the quotes shape, otherwise take the first message under whatever key is
// present. Still never assume a SPECIFIC field key (errors.author etc.) exists.
function extractValidationMessage(err: HttpErrorResponse): string {
  const body = err.error as
    | { errors?: Record<string, string[]>; detail?: string; title?: string }
    | null
    | undefined;

  const errors = body?.errors;
  if (errors) {
    const preferred = errors['error'];
    if (preferred?.length) {
      return preferred[0];
    }
    for (const messages of Object.values(errors)) {
      if (messages?.length) {
        return messages[0];
      }
    }
  }

  // A ProblemDetails body with no `errors` map at all, just a detail line.
  if (typeof body?.detail === 'string' && body.detail.length > 0) {
    return body.detail;
  }

  return GENERIC_VALIDATION_MESSAGE;
}

// Maps whatever comes out of HttpClient into one typed, friendly shape.
// Deliberately takes `unknown`, not HttpErrorResponse - it's meant to sit at
// the boundary (see error-mapping-interceptor.ts's catchError) where the
// caught value's real type isn't guaranteed, and it must never itself throw
// trying to inspect a shape that isn't there (e.g. a null/empty error body).
export function mapToApiError(err: unknown): ApiError {
  if (!(err instanceof HttpErrorResponse)) {
    // Not even an HttpErrorResponse - something thrown outside the HTTP
    // layer entirely. Still surfaced as an ApiError so every consumer has
    // exactly one type to deal with, never a raw unknown.
    return new ApiError('network', GENERIC_UNKNOWN_MESSAGE, null);
  }

  // status 0 is what HttpClient reports for a request that never got a
  // response at all - offline, DNS failure, connection refused, a rejected
  // CORS preflight. It is never a real HTTP status code the server sent.
  if (err.status === 0) {
    return new ApiError('network', GENERIC_NETWORK_MESSAGE, 0);
  }

  if (err.status === 404) {
    return new ApiError('notFound', GENERIC_NOT_FOUND_MESSAGE, 404);
  }

  if (err.status === 401 || err.status === 403) {
    return new ApiError('auth', GENERIC_AUTH_MESSAGE, err.status);
  }

  if (err.status === 400) {
    return new ApiError('validation', extractValidationMessage(err), 400);
  }

  // 409 - so far only produced by POST /api/auth/register (email already
  // taken). That endpoint returns a plain ProblemDetails body (`Problem(...)`
  // in AuthController.cs), which is a `detail` string, not an `errors` map -
  // extractValidationMessage() already handles that shape, it just falls
  // back to the wrong generic message (validation's) when `detail` is
  // missing, so that fallback is overridden here.
  if (err.status === 409) {
    const message = extractValidationMessage(err);
    return new ApiError('conflict', message === GENERIC_VALIDATION_MESSAGE ? GENERIC_CONFLICT_MESSAGE : message, 409);
  }

  if (err.status >= 500) {
    return new ApiError('server', GENERIC_SERVER_MESSAGE, err.status);
  }

  // Any other/unanticipated 4xx this API doesn't document producing today -
  // still mapped rather than left as a raw HttpErrorResponse, so consumers
  // never have to handle two different error types.
  return new ApiError('server', GENERIC_UNKNOWN_MESSAGE, err.status);
}
