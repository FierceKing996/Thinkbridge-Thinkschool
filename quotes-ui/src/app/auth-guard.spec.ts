import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { ActivatedRouteSnapshot, RouterStateSnapshot, UrlTree, provideRouter } from '@angular/router';

import { authGuard } from './auth-guard';
import { Auth } from './auth';

describe('authGuard', () => {
  // A minimal stand-in for Auth: the guard only ever reads isAuthenticated().
  // A writable signal lets each test set the logged-in state without any HTTP.
  const isAuthenticated = signal(false);

  beforeEach(() => {
    isAuthenticated.set(false);
    TestBed.configureTestingModule({
      providers: [
        provideRouter([]),
        { provide: Auth, useValue: { isAuthenticated } },
      ],
    });
  });

  // CanActivateFn must run inside an injection context (it calls inject()).
  function runGuard(url: string): boolean | UrlTree {
    return TestBed.runInInjectionContext(
      () =>
        authGuard(
          {} as ActivatedRouteSnapshot,
          { url } as RouterStateSnapshot,
        ) as boolean | UrlTree,
    );
  }

  it('allows activation when authenticated', () => {
    isAuthenticated.set(true);
    expect(runGuard('/quotes/new')).toBe(true);
  });

  it('redirects to /login with the attempted URL as returnUrl when not authenticated', () => {
    const result = runGuard('/quotes/new');
    expect(result).toBeInstanceOf(UrlTree);
    expect((result as UrlTree).toString()).toBe('/login?returnUrl=%2Fquotes%2Fnew');
  });
});
