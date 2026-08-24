import { Component, computed, effect, inject, signal } from '@angular/core';
import { Quote, QuoteDto } from '../quote';

type Status = 'loading' | 'error' | 'empty' | 'loaded';

@Component({
  selector: 'app-quote-list',
  imports: [],
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
