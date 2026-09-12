import { Component, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed, toObservable } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { Observable, catchError, map, of, switchMap } from 'rxjs';
import { ApiError } from '../../../core/http/api-error';
import { Quote, QuoteDto } from '../../../core/models/quote';

type Status = 'loading' | 'error' | 'empty' | 'loaded';

type FetchResult = { kind: 'success'; quotes: QuoteDto[] } | { kind: 'error'; message: string };

@Component({
  selector: 'app-quote-list',
  imports: [RouterLink],
  templateUrl: './quote-list.html',
  styleUrl: './quote-list.css',
})
export class QuoteList {
  // inject(), not a constructor parameter - no DI in a constructor anywhere
  // in this component.
  private readonly quoteService = inject(Quote);

  readonly page = signal(1);
  readonly pageSize = signal(10);
  readonly authorFilter = signal('');

  private readonly quotes = signal<QuoteDto[]>([]);
  private readonly loading = signal(true);
  private readonly loadError = signal<string | null>(null);

  // The row the user just clicked to navigate to the detail view. Its <a>
  // gets `view-transition-name: quote-card` (see the template) for exactly
  // the duration of that navigation, so the browser can pair it with the
  // detail page's <article> and morph between them. It must be applied to a
  // SINGLE element - tagging every row with the same name at once is invalid
  // and the browser silently skips the whole transition. Limitation: on
  // Back navigation this component is recreated, so this resets to null and
  // the reverse morph doesn't fire - acceptable for the exercise.
  readonly transitioningId = signal<number | null>(null);

  // Derived from two signals - quotes() and authorFilter() - recomputed only
  // when either changes, not on every change-detection pass.
  readonly filteredQuotes = computed(() =>
    this.quotes().filter((q) => q.author.toLowerCase().includes(this.authorFilter().toLowerCase())),
  );

  readonly status = computed<Status>(() => {
    if (this.loading()) return 'loading';
    if (this.loadError()) return 'error';
    if (this.filteredQuotes().length === 0) return 'empty';
    return 'loaded';
  });

  constructor() {
    // Refetches whenever page or pageSize change - both are read inside the
    // computed, so both are tracked dependencies. authorFilter is NOT read
    // here on purpose: it only narrows what's already in memory, via the
    // computed above, so typing in the filter box never triggers a network
    // call.
    //
    // toObservable(...).pipe(switchMap(...)) - not a plain .subscribe() on
    // each effect run - is the fix for the same stale-response race that
    // quote-detail.ts already solved (see that file's constructor comment
    // for the full mechanism). It matters more here than it might look:
    // retryInterceptor (retry-interceptor.ts) can keep a transiently-failing
    // GET in flight for 750ms+ across its backoff attempts, which is easily
    // enough time for a user to click "Next"/"Prev" again before the first
    // request resolves. switchMap unsubscribing from the previous inner
    // Observable the instant page/pageSize changes again both aborts the
    // underlying HTTP call and - per RxJS's retry() semantics - cancels any
    // pending backoff timer, so a late/stale response (or a stale retry
    // attempt) can never arrive after the fact and overwrite what the new
    // page actually returned.
    toObservable(computed(() => ({ page: this.page(), pageSize: this.pageSize() })))
      .pipe(
        switchMap(({ page, pageSize }) => {
          this.loading.set(true);
          this.loadError.set(null);

          return this.quoteService.getQuotes(page, pageSize).pipe(
            map((quotes): FetchResult => ({ kind: 'success', quotes })),
            // The HTTP layer (error-mapping-interceptor.ts, wired in
            // globally in app.config.ts) has already turned whatever the
            // backend/network produced into a typed ApiError with a
            // friendly, human-readable .message by the time it reaches
            // here. Checking `instanceof ApiError` specifically - not just
            // `instanceof Error` - is deliberate: ApiError.message is built
            // to be shown to a user (see api-error.ts), but a plain Error
            // thrown from somewhere else in the pipe wouldn't necessarily
            // have a message that's safe/sensible to display, so it
            // shouldn't fall through to the same branch.
            catchError(
              (err: unknown): Observable<FetchResult> =>
                of({
                  kind: 'error',
                  message: err instanceof ApiError ? err.message : 'Failed to load quotes.',
                }),
            ),
          );
        }),
        takeUntilDestroyed(),
      )
      .subscribe((result) => {
        this.loading.set(false);
        if (result.kind === 'success') {
          this.quotes.set(result.quotes);
        } else {
          this.loadError.set(result.message);
        }
      });
  }

  onFilterInput(event: Event): void {
    this.authorFilter.set((event.target as HTMLInputElement).value);
  }

  nextPage(): void {
    this.page.update((p) => p + 1);
  }

  previousPage(): void {
    this.page.update((p) => Math.max(1, p - 1));
  }
}
