import { Component, computed, effect, inject, signal } from '@angular/core';
import { Quote, QuoteDto } from '../quote';
import { QuoteDetail } from '../quote-detail/quote-detail';

type Status = 'loading' | 'error' | 'empty' | 'loaded';

@Component({
  selector: 'app-quote-list',
  imports: [QuoteDetail],
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

  // Which quote is selected for the detail pane - null means none selected
  // yet. Passed straight into <app-quote-detail>'s id input().
  readonly selectedId = signal<number | null>(null);

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
    // effect, so both are tracked dependencies. authorFilter is NOT read here
    // on purpose: it only narrows what's already in memory, via the computed
    // above, so typing in the filter box never triggers a network call.
    effect(() => {
      const page = this.page();
      const pageSize = this.pageSize();

      this.loading.set(true);
      this.loadError.set(null);

      this.quoteService.getQuotes(page, pageSize).subscribe({
        next: (result) => {
          this.quotes.set(result);
          this.loading.set(false);
        },
        error: (err: unknown) => {
          this.loadError.set(err instanceof Error ? err.message : 'Failed to load quotes.');
          this.loading.set(false);
        },
      });
    });

    // Whenever the query itself changes - a new page, or a new author
    // filter - any existing selection may no longer be in the visible list
    // (or may not even be on the new page at all). Reset it rather than let
    // <app-quote-detail> keep showing the last quote it successfully loaded,
    // which would be stale/unrelated data. This covers nextPage(),
    // previousPage(), and onFilterInput() uniformly - and any future caller
    // that changes page or authorFilter - rather than resetting inside each
    // of those methods individually.
    effect(() => {
      this.page();
      this.authorFilter();
      this.selectedId.set(null);
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

  selectQuote(id: number): void {
    this.selectedId.set(id);
  }
}
