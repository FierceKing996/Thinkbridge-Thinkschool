import { Component, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { HttpErrorResponse } from '@angular/common/http';
import { Observable, catchError, map, of, switchMap } from 'rxjs';
import { Quote, QuoteDto } from '../quote';

type FetchResult = { kind: 'success'; quote: QuoteDto } | { kind: 'error'; message: string };

@Component({
  selector: 'app-quote-detail',
  imports: [],
  templateUrl: './quote-detail.html',
  styleUrl: './quote-detail.css',
})
export class QuoteDetail {
  // inject(), not a constructor parameter - matches QuoteList's convention.
  private readonly quoteService = inject(Quote);

  // Selected quote id, driven by the parent (QuoteList). null means nothing
  // is selected yet.
  readonly id = input<number | null>(null);

  readonly loading = signal(false);
  readonly error = signal<string | null>(null);
  readonly data = signal<QuoteDto | null>(null);

  constructor() {
    // toObservable(this.id).pipe(switchMap(...)) is the actual fix for the
    // stale-response race, not just a "latest id wins" flag: switchMap
    // unsubscribes from the *previous* inner Observable the instant a new id
    // arrives on the outer stream, and HttpClient's Observable aborts the
    // underlying XHR/fetch call on unsubscribe (see HttpXhrBackend /
    // HttpFetchBackend teardown). So clicking quote A then quote B before A's
    // response arrives genuinely cancels A's in-flight request - there is no
    // subscriber left to receive A's response even if the server had already
    // sent it, so it can never reach next() below and clobber B's data.
    toObservable(this.id)
      .pipe(
        switchMap((id) => {
          this.data.set(null);
          this.error.set(null);

          if (id === null) {
            this.loading.set(false);
            return of<FetchResult | null>(null);
          }

          this.loading.set(true);
          return this.quoteService.getQuoteById(id).pipe(
            map((quote): FetchResult => ({ kind: 'success', quote })),
            catchError(
              (err: HttpErrorResponse): Observable<FetchResult> =>
                of({
                  kind: 'error',
                  message:
                    err.status === 404 ? `Quote ${id} was not found.` : 'Failed to load quote.',
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
