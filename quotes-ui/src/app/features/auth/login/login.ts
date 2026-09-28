import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { ApiError } from '../../../core/http/api-error';
import { Auth } from '../../../core/auth/auth';

@Component({
  selector: 'app-login',
  imports: [RouterLink],
  templateUrl: './login.html',
  styleUrl: './login.css',
})
export class Login {
  private readonly auth = inject(Auth);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  // takeUntilDestroyed() needs the DestroyRef captured in an injection
  // context; onSubmit() runs from a form submit handler, which is not one.
  private readonly destroyRef = inject(DestroyRef);

  // Publicly readable so the spec can assert on them directly (matches the
  // convention in quote-list.ts / quote-detail.ts).
  readonly email = signal('');
  readonly password = signal('');
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  readonly canSubmit = computed(() => this.email().trim().length > 0 && this.password().length > 0);

  // Where to go after a successful login/register. authGuard parks the
  // originally attempted URL here as ?returnUrl= when it bounces an
  // unauthenticated user; if it's absent (someone hit /login directly) fall
  // back to the list. Read from the snapshot - this component is only ever
  // created fresh on navigation to /login, so the query param can't change
  // under it. Also forwarded onto the "Create an account" link so a bounce
  // through /register lands the user back at the same place.
  readonly returnUrl = this.route.snapshot.queryParamMap.get('returnUrl') ?? '/quotes';

  onEmailInput(value: string): void {
    this.email.set(value);
  }

  onPasswordInput(value: string): void {
    this.password.set(value);
  }

  onSubmit(event: Event): void {
    event.preventDefault();
    if (!this.canSubmit() || this.loading()) {
      return;
    }

    this.loading.set(true);
    this.error.set(null);

    this.auth
      .login(this.email().trim(), this.password())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => this.router.navigateByUrl(this.returnUrl),
        error: (err: unknown) => {
          this.loading.set(false);
          // The HTTP layer has already mapped this to a typed ApiError with
          // a friendly message (error-mapping-interceptor.ts). A 401 here
          // specifically means "wrong email or password" - AuthController's
          // uniform-401 policy (day1/QuotesApi's AuthController.cs) means
          // that's the only thing an ApiError of kind 'auth' can mean at
          // THIS call site (login is never sent an expired token to reject),
          // so it's worth a more specific message than ApiError's own
          // generic "sign in again" wording.
          this.error.set(
            err instanceof ApiError
              ? err.kind === 'auth'
                ? 'Incorrect email or password.'
                : err.message
              : 'Could not log in. Please try again.',
          );
        },
      });
  }
}
