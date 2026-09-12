import { Component, computed, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { Observable, catchError, map, of, switchMap } from 'rxjs';
import { ApiError } from '../../../core/http/api-error';
import { Quote, QuoteDto } from '../../../core/models/quote';

type FetchResult = { kind: 'success'; quote: QuoteDto } | { kind: 'error'; message: string };

@Component({
  selector: 'app-quote-detail',
  imports: [RouterLink],
  templateUrl: './quote-detail.html',
  styleUrl: './quote-detail.css',
})
export class QuoteDetail {
  // inject(), not a constructor parameter - matches QuoteList's convention.
  private readonly quoteService = inject(Quote);

  // Bound from the ':id' route segment by withComponentInputBinding() (see
  // app.config.ts). It is NOT a number: route params are always strings, and
  // it's `undefined` if the component is ever rendered off-route with no
  // param at all. Everything downstream goes through quoteId() below, never
  // this raw value.
  readonly id = input<string>();

  // The route param parsed to a usable quote id, or null if it isn't one.
  // The server route is constrained `{id:int}` and the id field is a
  // positive integer, so anything that isn't a run of digits (`''`, `'abc'`,
  // `'1.5'`, `'-3'`, `'3x'`) is rejected HERE, before any HTTP call -
  // hitting the API with it would be pointless (it couldn't match the route)
  // and the resulting error would be indistinguishable from a real 404.
  // A genuine-but-nonexistent id (e.g. 999999) is NOT caught here - it's a
  // valid integer, so it goes to the API and comes back as an ApiError with
  // kind === 'notFound', handled in the pipe below.
  readonly quoteId = computed<number | null>(() => {
    const raw = this.id();
    if (raw === undefined || !/^\d+$/.test(raw)) {
      return null;
    }
    const parsed = Number(raw);
    return Number.isSafeInteger(parsed) && parsed > 0 ? parsed : null;
  });

  // Drives the template's "that's not a valid quote id" branch - a missing
  // or non-numeric param, distinct from a well-formed id the server doesn't
  // have (which surfaces via error() as a not-found message).
  readonly invalidParam = computed(() => this.quoteId() === null);

  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly data = signal<QuoteDto | null>(null);

  constructor() {
    // toObservable(this.quoteId).pipe(switchMap(...)) is the fix for the
    // stale-response race, not just a "latest id wins" flag: switchMap
    // unsubscribes from the *previous* inner Observable the instant a new id
    // arrives, and HttpClient aborts the underlying XHR/fetch on unsubscribe
    // (HttpXhrBackend / HttpFetchBackend teardown). So navigating
    // /quotes/1 -> /quotes/2 before 1's response lands genuinely cancels 1's
    // request - nothing is left to receive it, so it can't clobber 2's data.
    toObservable(this.quoteId)
      .pipe(
        switchMap((id) => {
          this.data.set(null);
          this.error.set(null);

          if (id === null) {
            // Missing / non-numeric param - the template shows the
            // invalid-param state off invalidParam(); make no API call.
            this.loading.set(false);
            return of<FetchResult | null>(null);
          }

          this.loading.set(true);
          return this.quoteService.getQuoteById(id).pipe(
            map((quote): FetchResult => ({ kind: 'success', quote })),
            // The HTTP layer (error-mapping-interceptor.ts, wired in
            // globally in app.config.ts) has already turned whatever the
            // backend/network produced into a typed ApiError by the time it
            // reaches here - checking err.kind === 'notFound' instead of a
            // raw HttpErrorResponse's err.status === 404.
            catchError(
              (err: unknown): Observable<FetchResult> =>
                of({
                  kind: 'error',
                  message:
                    err instanceof ApiError && err.kind === 'notFound'
                      ? `Quote ${id} was not found.`
                      : 'Failed to load quote.',
                }),
            ),
          );
        }),
        takeUntilDestroyed(),
      )
      .subscribe((result) => {
        this.loading.set(false);
        if (result === null) return;

        if (result.kind === 'success') {
          this.data.set(result.quote);
        } else {
          this.error.set(result.message);
        }
      });
  }
}
