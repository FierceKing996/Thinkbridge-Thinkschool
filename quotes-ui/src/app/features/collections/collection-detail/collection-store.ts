import { DestroyRef, Injectable, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ApiError, ApiErrorKind } from '../../../core/http/api-error';
import {
  Collection,
  CollectionItemDto,
  CollectionItemRefDto,
  CollectionWriteDto,
} from '../../../core/models/collection';

export type CollectionStatus = 'idle' | 'loading' | 'loaded' | 'error';

interface NormalisedError {
  kind: ApiErrorKind;
  message: string;
}

// Every failure reaching the store has already passed through
// errorMappingInterceptor (outermost in app.config.ts's chain), so it is an
// ApiError with a friendly, endpoint-appropriate .message - including the
// precise 400 sentence for the collections endpoints, whose ValidationProblem
// bodies key their messages by field name ("QuoteId" / "quoteId" / "Name")
// rather than the quotes endpoints' single "error" key (see api-error.ts's
// extractValidationMessage). The specs wire that same interceptor, so this is
// the only shape this function has to handle; anything that is somehow NOT an
// ApiError is a bug elsewhere and gets a safe generic.
function normaliseError(err: unknown): NormalisedError {
  if (err instanceof ApiError) {
    return { kind: err.kind, message: err.message };
  }
  return { kind: 'network', message: 'An unexpected error occurred. Please try again.' };
}

// Add/remove a single value to a ReadonlySet without mutating the original
// (signals must get a fresh reference to notify).
function setWith(source: ReadonlySet<number>, value: number): ReadonlySet<number> {
  const next = new Set(source);
  next.add(value);
  return next;
}
function setWithout(source: ReadonlySet<number>, value: number): ReadonlySet<number> {
  const next = new Set(source);
  next.delete(value);
  return next;
}

/**
 * Signal-backed store for ONE collection's detail view. Provided at the
 * route/component level (see collection-detail.ts's `providers`), NOT in
 * root: each navigation to `collections/:id` gets its own instance, so the
 * state, the in-flight-op sets and the teardown are all scoped to that one
 * viewed collection and nothing leaks between them.
 */
@Injectable()
export class CollectionStore {
  private readonly collectionService = inject(Collection);
  // Captured here (an injection context) so the mutating methods - which
  // run from template click handlers, NOT an injection context - can still
  // hand it to takeUntilDestroyed(). Same pattern as login.ts.
  private readonly destroyRef = inject(DestroyRef);

  // --- private writable state -------------------------------------------
  private readonly _status = signal<CollectionStatus>('idle');
  private readonly _error = signal<string | null>(null);
  private readonly _errorKind = signal<ApiErrorKind | null>(null);
  private readonly _id = signal<number | null>(null);
  private readonly _name = signal<string>('');
  // The ENRICHED shape - author/text kept available for rendering. An
  // optimistically-added row that the server hasn't been reconciled with
  // yet carries author === '' / text === '' (see addItem).
  private readonly _items = signal<CollectionItemDto[]>([]);

  // Per-quoteId in-flight tracking so the UI can disable exactly the row
  // being mutated and show a per-row spinner. Two overlapping adds (or an
  // add + a remove) are each tracked independently.
  private readonly _pendingAdds = signal<ReadonlySet<number>>(new Set());
  private readonly _pendingRemoves = signal<ReadonlySet<number>>(new Set());

  // --- public readonly views -------------------------------------------
  readonly status = this._status.asReadonly();
  readonly error = this._error.asReadonly();
  readonly errorKind = this._errorKind.asReadonly();
  readonly id = this._id.asReadonly();
  readonly name = this._name.asReadonly();
  readonly items = this._items.asReadonly();
  readonly pendingAdds = this._pendingAdds.asReadonly();
  readonly pendingRemoves = this._pendingRemoves.asReadonly();

  readonly itemCount = computed(() => this._items().length);
  readonly isEmpty = computed(() => this._status() === 'loaded' && this._items().length === 0);
  readonly isBusy = computed(() => this._pendingAdds().size > 0 || this._pendingRemoves().size > 0);

  // Template conveniences.
  isAdding(quoteId: number): boolean {
    return this._pendingAdds().has(quoteId);
  }
  isRemoving(quoteId: number): boolean {
    return this._pendingRemoves().has(quoteId);
  }

  clearError(): void {
    this._error.set(null);
    this._errorKind.set(null);
  }

  /**
   * GET /api/collections/{id} (read model, no auth). Populates state on
   * success. A 404 is surfaced as a distinct "collection not found"
   * message, separate from a generic failure.
   */
  load(id: number): void {
    this._id.set(id);
    this._status.set('loading');
    this._error.set(null);
    this._errorKind.set(null);

    this.collectionService
      .getCollection(id)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (dto) => {
          this._id.set(dto.id);
          this._name.set(dto.name);
          this._items.set(dto.items ?? []);
          this._status.set('loaded');
        },
        error: (err: unknown) => {
          const { kind, message } = normaliseError(err);
          this._errorKind.set(kind);
          this._error.set(
            kind === 'notFound' ? `Collection ${id} was not found.` : message,
          );
          this._status.set('error');
        },
      });
  }

  /**
   * OPTIMISTIC add. Appends a placeholder row and marks the quoteId
   * pending immediately, THEN POSTs. Reconciles with the write-model
   * response on success, rolls the placeholder back on failure.
   */
  addItem(quoteId: number): void {
    const id = this._id();
    if (id === null) {
      return;
    }
    // Guard against double-firing for a quoteId whose add is already in
    // flight (rapid double-click, or the picker re-emitting).
    if (this._pendingAdds().has(quoteId)) {
      return;
    }

    // Remember whether this quoteId was already a row before we touched
    // anything - decides what rollback does. If it was already present
    // (the duplicate case), a failed add must NOT delete the real row.
    const existedBefore = this._items().some((item) => item.quoteId === quoteId);

    if (!existedBefore) {
      // Placeholder: we have no author/text for it yet. DECISION: we
      // accept rendering it with just the quoteId ("Quote #N") until the
      // next load() re-fetches the enriched read model - no quotes cache,
      // no extra GET per add. The template shows a muted "details load on
      // refresh" hint for any row whose author is still ''.
      const placeholder: CollectionItemDto = {
        quoteId,
        author: '',
        text: '',
        addedAt: new Date().toISOString(),
      };
      this._items.update((items) => [...items, placeholder]);
    }
    this._pendingAdds.update((set) => setWith(set, quoteId));

    this.collectionService
      .addItem(id, quoteId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (response) => {
          this.reconcile(response);
          this._pendingAdds.update((set) => setWithout(set, quoteId));
        },
        error: (err: unknown) => {
          // Roll back the optimistic add - but only the row WE added.
          if (!existedBefore) {
            this._items.update((items) => items.filter((item) => item.quoteId !== quoteId));
          }
          this._pendingAdds.update((set) => setWithout(set, quoteId));
          this.applyOpError(err);
        },
      });
  }

  /**
   * OPTIMISTIC remove. Drops the row and marks the quoteId pending
   * immediately, THEN DELETEs. Reconciles on success, re-inserts the row
   * (at its original position) on failure.
   */
  removeItem(quoteId: number): void {
    const id = this._id();
    if (id === null) {
      return;
    }
    if (this._pendingRemoves().has(quoteId)) {
      return;
    }

    const index = this._items().findIndex((item) => item.quoteId === quoteId);
    if (index === -1) {
      return;
    }
    const removed = this._items()[index];

    this._items.update((items) => items.filter((item) => item.quoteId !== quoteId));
    this._pendingRemoves.update((set) => setWith(set, quoteId));

    this.collectionService
      .removeItem(id, quoteId)
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: (response) => {
          this.reconcile(response);
          this._pendingRemoves.update((set) => setWithout(set, quoteId));
        },
        error: (err: unknown) => {
          // Roll back: put the (still enriched) row back where it was.
          this._items.update((items) => {
            const next = [...items];
            next.splice(Math.min(index, next.length), 0, removed);
            return next;
          });
          this._pendingRemoves.update((set) => setWithout(set, quoteId));
          this.applyOpError(err);
        },
      });
  }

  /**
   * THE RECONCILIATION TRAP.
   *
   * The POST/DELETE write-model response (CollectionWriteDto) has items
   * WITHOUT author/text - `{ quoteId, addedAt }` only. Calling
   * `this._items.set(response.items)` would wipe author/text off every row
   * that already had it, blanking the whole list after a single add.
   *
   * So we MERGE instead of replace: the server's item list is authoritative
   * for MEMBERSHIP and ordering, but for each quoteId it still contains we
   * keep the enriched author/text we already hold and only refresh addedAt
   * from the server. A quoteId the server reports that we have no enriched
   * copy of (a freshly optimistically-added row) stays a placeholder
   * (author/text === '') until the next load().
   */
  private reconcile(response: CollectionWriteDto): void {
    this._name.set(response.name);
    const byId = new Map(this._items().map((item) => [item.quoteId, item]));
    const merged: CollectionItemDto[] = response.items.map((ref: CollectionItemRefDto) => {
      const existing = byId.get(ref.quoteId);
      return existing
        ? { ...existing, addedAt: ref.addedAt }
        : { quoteId: ref.quoteId, author: '', text: '', addedAt: ref.addedAt };
    });
    this._items.set(merged);
  }

  private applyOpError(err: unknown): void {
    const { kind, message } = normaliseError(err);
    this._errorKind.set(kind);
    this._error.set(message);
  }
}
