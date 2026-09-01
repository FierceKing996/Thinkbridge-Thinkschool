import { Component, DestroyRef, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router } from '@angular/router';
import { ApiError } from '../api-error';
import { Auth } from '../auth';

@Component({
  selector: 'app-login',
  imports: [],
  templateUrl: './login.html',
  styleUrl: './login.css',
})
export class Login {
  private readonly auth = inject(Auth);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  // takeUntilDestroyed() needs the DestroyRef captured in an injection
  // context; logIn() runs from a click handler, which is not one.
  private readonly destroyRef = inject(DestroyRef);

  // Publicly readable so the spec can assert on them directly (matches the
  // convention in quote-list.ts / quote-detail.ts).
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  // Where to go after a successful login. authGuard parks the originally
  // attempted URL here as ?returnUrl= when it bounces an unauthenticated
  // user; if it's absent (someone hit /login directly) fall back to the
  // list. Read from the snapshot - this component is only ever created fresh
  // on navigation to /login, so the query param can't change under it.
  private readonly returnUrl =
    this.route.snapshot.queryParamMap.get('returnUrl') ?? '/quotes';

  logIn(): void {
    this.loading.set(true);
    this.error.set(null);

    this.auth
      .login()
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => this.router.navigateByUrl(this.returnUrl),
        error: (err: unknown) => {
          this.loading.set(false);
          // The HTTP layer has already mapped this to a typed ApiError with
          // a friendly message (error-mapping-interceptor.ts).
          this.error.set(
            err instanceof ApiError
              ? err.message
              : 'Could not log in. Please try again.',
          );
        },
      });
  }
}
