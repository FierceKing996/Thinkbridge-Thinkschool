import { Component, inject, signal } from '@angular/core';
import { HttpClient } from '@angular/common/http';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { Router, RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { catchError, of } from 'rxjs';
import { Auth } from './core/auth/auth';

interface MetaDto {
  environment: string;
}

// Pure shell now: the wordmark, a nav, and the routed view. All screen content
// lives behind lazy routes (app.routes.ts) - App itself imports no feature
// component, which is what keeps them out of the initial bundle.
@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
export class App {
  private readonly auth = inject(Auth);
  private readonly router = inject(Router);
  private readonly http = inject(HttpClient);

  // Read directly in the template (app.html) to swap the nav between
  // "Sign in"/"Create account" and "Logout" - signal-derived, so it updates
  // the moment Auth's token signal changes, with no manual subscription.
  readonly isAuthenticated = this.auth.isAuthenticated;

  // GET /api/meta (day1/QuotesApi Program.cs) - which live deployment this
  // is (dev/prod, per render.yaml's Environment__Label), so the same build
  // can be pointed at either Render service and still tell the two apart
  // on screen. Best-effort: a fetch failure (offline, CORS, whatever) just
  // leaves this null and the badge doesn't render - it's a nice-to-have,
  // never something the rest of the app should wait on or fail over.
  readonly environmentLabel = signal<string | null>(null);

  constructor() {
    this.http
      .get<MetaDto>('/api/meta')
      .pipe(
        catchError(() => of(null)),
        takeUntilDestroyed(),
      )
      .subscribe((meta) => this.environmentLabel.set(meta?.environment ?? null));
  }

  logout(): void {
    this.auth.logout();
    // Back to the public list - staying on whatever route triggered this
    // (e.g. quotes/new, which is authGuard-protected) would immediately
    // bounce through the guard to /login anyway; going there directly is
    // one redirect instead of two.
    this.router.navigateByUrl('/quotes');
  }
}
