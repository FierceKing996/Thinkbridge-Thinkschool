import { Component, computed, effect, inject, input, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { RouterLink } from '@angular/router';
import { catchError, of } from 'rxjs';
import { Quote, QuoteDto } from '../quote';
import { CollectionStore } from './collection-store';

@Component({
  selector: 'app-collection-detail',
  imports: [RouterLink],
  templateUrl: './collection-detail.html',
  styleUrl: './collection-detail.css',
  // Provided HERE, not in root: one store instance per viewed collection,
  // torn down (with any in-flight add/remove) when this route is left.
  providers: [CollectionStore],
})
export class CollectionDetail {
  // inject(), matching QuoteDetail's convention.
  protected readonly store = inject(CollectionStore);
  private readonly quoteService = inject(Quote);

  // Bound from the ':id' route segment by withComponentInputBinding(). It
  // is a STRING (or undefined off-route) - never trusted as a number.
  readonly id = input<string>();

  // Same parse/validate discipline as quote-detail.ts: the server route is
  // `{id:int}` and ids are positive integers, so anything that isn't a run
  // of digits is rejected here, before any HTTP call. A well-formed but
  // nonexistent id (e.g. 999999) is NOT caught here - it goes to the API
  // and comes back as the store's "collection not found" error state.
  readonly collectionId = computed<number | null>(() => {
    const raw = this.id();
    if (raw === undefined || !/^\d+$/.test(raw)) {
      return null;
    }
    const parsed = Number(raw);
    return Number.isSafeInteger(parsed) && parsed > 0 ? parsed : null;
  });

  readonly invalidParam = computed(() => this.collectionId() === null);

  // The "pick a quote to add" source - GET /api/quotes (no auth). Loaded
  // once; failure just leaves the picker empty (the number input still
  // works), it must not blow up the whole screen.
  readonly pickerQuotes = signal<QuoteDto[]>([]);
  readonly selectedQuoteId = signal<number | null>(null);

  // Quotes not already in the collection - what the dropdown should offer.
  readonly addableQuotes = computed(() => {
    const present = new Set(this.store.items().map((item) => item.quoteId));
    return this.pickerQuotes().filter((quote) => !present.has(quote.id));
  });

  constructor() {
    // Re-load whenever the parsed id changes. collectionId() is read here,
    // so it's a tracked dependency. A null id makes no call - the template
    // shows the invalid-param branch off invalidParam().
    effect(() => {
      const id = this.collectionId();
      if (id !== null) {
        this.store.load(id);
      }
    });

    this.quoteService
      .getQuotes(1, 50)
      .pipe(
        catchError(() => of<QuoteDto[]>([])),
        takeUntilDestroyed(),
      )
      .subscribe((quotes) => this.pickerQuotes.set(quotes));
  }

  onSelectQuote(event: Event): void {
    const value = (event.target as HTMLSelectElement).value;
    this.selectedQuoteId.set(value === '' ? null : Number(value));
  }

  onQuoteIdInput(event: Event): void {
    const value = (event.target as HTMLInputElement).value.trim();
    this.selectedQuoteId.set(/^\d+$/.test(value) ? Number(value) : null);
  }

  add(): void {
    const quoteId = this.selectedQuoteId();
    if (quoteId === null) {
      return;
    }
    this.store.addItem(quoteId);
    this.selectedQuoteId.set(null);
  }

  remove(quoteId: number): void {
    this.store.removeItem(quoteId);
  }
}
