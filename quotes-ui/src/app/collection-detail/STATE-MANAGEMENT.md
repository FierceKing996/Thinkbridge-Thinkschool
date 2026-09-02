# When to graduate from "signals + a service" to a real store

**Recommendation for sign-off.** Draft rule, tied to this codebase. Adjust the numbers if you disagree, but keep them numbers.

## Where we are today

State lives in three places, all small and all local:

- `Auth` — one root signal (`_token`) + two `computed`s. Genuinely global, genuinely tiny.
- Component-owned signals — `QuoteList` (`page`, `pageSize`, `authorFilter`, `quotes`, ...), `QuoteDetail`, `CreateQuote`. Each screen owns its own state; nothing is shared.
- `CollectionStore` — the first _service that owns view state_: writable signals, `computed` views, optimistic add/remove with rollback, per-row pending sets. One instance per route, provided at the component.

This is the right weight for the app as it stands. Do **not** add `@ngrx/signals` or NgRx now — there is no shared mutable server entity, no cross-feature derived state, and one copy of the rollback pattern.

## The rule — adopt @ngrx/signals SignalStore when ANY ONE of these is true

1. **Shared mutable entity.** The same server entity is read by **3 or more components that can each mutate it**, and they must not show stale copies of each other. Today: zero. A collections _list_ screen that edits item counts while `CollectionDetail` is open would make it two; add a sidebar "recent collections" widget and you are at three — graduate then. (One writer + N read-only views does **not** count; a `computed` off one shared service signal covers that.)
2. **Duplicated optimistic-rollback logic.** The optimistic-update + rollback + pending-set pattern in `collection-store.ts` gets **copy-pasted into a 3rd feature** (e.g. quote edit, collection rename). Two hand-rolled copies is tolerable; at three, move to `withEntities` / a shared `withOptimistic` feature instead of a 4th copy.
3. **Derived state crossing feature boundaries.** A single `computed` depends on **4 or more source signals owned by different features/services** (e.g. auth + collections + quotes + a filter). `filteredQuotes` (2 sources, one component) is fine; a cross-feature selector is not, and is the thing selectors exist for.
4. **Debugging needs a time machine.** A state bug is opened that **cannot be diagnosed in one sitting without action-by-action history** — you find yourself adding `console.log` to every `.set()` in `CollectionStore`. That is the signal to want Redux DevTools / time-travel, which NgRx gives for free.
5. **Store instances outlive a route.** We need a `CollectionStore` (or similar) to **survive navigation and be re-entered with its state intact** for more than one feature — i.e. real app-level caching of entities, not "re-`load()` on route enter".

## What does NOT justify it

More screens; more signals in one component; one more service that owns its own view state the way `CollectionStore` does; "it feels complex". Add the library when a threshold above is _crossed_, not in anticipation.
