import { Routes } from '@angular/router';
import { authGuard } from './core/auth/auth-guard';

// Every entry uses loadComponent: () => import(...) - a *dynamic* import is
// what makes the bundler emit a separate lazy chunk per screen, so the
// initial bundle ships only the shell (app.ts) + the router itself, and each
// route component's code is fetched on first navigation to it. A static
// top-of-file `import { QuoteList }` would defeat this entirely (it would be
// pulled into the initial bundle), which is why none of these route
// components are imported at module scope anywhere.
//
// Ordering is load-bearing: 'quotes/new' MUST come before 'quotes/:id'. The
// router matches routes top-to-bottom and takes the first hit, so with the
// order reversed ':id' would swallow the literal segment 'new' and hand
// QuoteDetail the string "new" to parse as an int (see quote-detail.ts's
// param validation - it would just render the invalid-param state instead of
// the create form).
export const routes: Routes = [
  {
    path: '',
    redirectTo: 'quotes',
    pathMatch: 'full',
  },
  {
    path: 'quotes',
    loadComponent: () => import('./features/quotes/quote-list/quote-list').then((m) => m.QuoteList),
  },
  {
    // Guarded - see auth-guard.ts. This is the ONLY route whose backing
    // endpoint (POST /api/quotes) genuinely requires auth; the GET routes
    // below are unauthenticated on the server (day1/QuotesApi Extension.cs).
    path: 'quotes/new',
    canActivate: [authGuard],
    loadComponent: () => import('./features/quotes/create-quote/create-quote').then((m) => m.CreateQuote),
  },
  {
    path: 'quotes/:id',
    loadComponent: () => import('./features/quotes/quote-detail/quote-detail').then((m) => m.QuoteDetail),
  },
  {
    // NOT guarded: GET /api/collections/{id} is unauthenticated on the
    // server (day1/QuotesApi), so the detail view loads for anyone. Only
    // the add/remove buttons hit auth'd endpoints (POST/DELETE
    // .../items) - a missing/expired token there surfaces as an ApiError
    // with kind 'auth' and the component shows a sign-in link, rather
    // than the whole route being gated. No literal sub-segment sibling
    // ('collections/new' etc.) exists, so ordering here isn't
    // load-bearing the way 'quotes/new' vs 'quotes/:id' is.
    path: 'collections/:id',
    loadComponent: () =>
      import('./features/collections/collection-detail/collection-detail').then((m) => m.CollectionDetail),
  },
  {
    path: 'login',
    loadComponent: () => import('./features/auth/login/login').then((m) => m.Login),
  },
  {
    path: 'register',
    loadComponent: () => import('./features/auth/register/register').then((m) => m.Register),
  },
  {
    // Unknown URL -> back to the list rather than a dead end. redirectTo (not
    // a wildcard component) keeps the address bar honest.
    path: '**',
    redirectTo: 'quotes',
  },
];
