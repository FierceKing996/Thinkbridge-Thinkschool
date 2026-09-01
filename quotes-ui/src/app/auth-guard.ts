import { inject } from '@angular/core';
import { CanActivateFn, Router } from '@angular/router';
import { Auth } from './auth';

// Functional guard (CanActivateFn), wired onto 'quotes/new' ONLY in
// app.routes.ts. That route's backing endpoint, POST /api/quotes, is the one
// that truly needs a bearer token (the "can-edit-quotes" / quotes.write
// policy in day1/QuotesApi Extension.cs); GET /api/quotes and
// GET /api/quotes/{id} have no RequireAuthorization, so guarding the list or
// detail routes would be pure theatre.
//
// It reads Auth.isAuthenticated SYNCHRONOUSLY - that's a signal-backed
// computed(), not an Observable - so the guard resolves in the same tick
// with no async plumbing, no CanActivate returning an Observable/Promise.
//
// On failure it returns a UrlTree, not `false`. `false` merely cancels the
// navigation and strands the user wherever they were (a blank page on a
// cold load); a UrlTree tells the router to redirect instead. The attempted
// URL rides along as ?returnUrl= so Login can bounce the user back to where
// they were originally headed after a successful sign-in.
export const authGuard: CanActivateFn = (_route, state) => {
  const auth = inject(Auth);
  const router = inject(Router);

  if (auth.isAuthenticated()) {
    return true;
  }

  return router.createUrlTree(['/login'], {
    queryParams: { returnUrl: state.url },
  });
};
