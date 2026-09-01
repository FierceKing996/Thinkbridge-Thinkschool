import { Routes } from '@angular/router';
import { authGuard } from './auth-guard';

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
    loadComponent: () => import('./quote-list/quote-list').then((m) => m.QuoteList),
  },
  {
    // Guarded - see auth-guard.ts. This is the ONLY route whose backing
    // endpoint (POST /api/quotes) genuinely requires auth; the GET routes
    // below are unauthenticated on the server (day1/QuotesApi Extension.cs).
    path: 'quotes/new',
    canActivate: [authGuard],
    loadComponent: () => import('./create-quote/create-quote').then((m) => m.CreateQuote),
  },
  {
    path: 'quotes/:id',
    loadComponent: () => import('./quote-detail/quote-detail').then((m) => m.QuoteDetail),
  },
  {
    path: 'login',
    loadComponent: () => import('./login/login').then((m) => m.Login),
  },
  {
    // Unknown URL -> back to the list rather than a dead end. redirectTo (not
    // a wildcard component) keeps the address bar honest.
    path: '**',
    redirectTo: 'quotes',
  },
];
