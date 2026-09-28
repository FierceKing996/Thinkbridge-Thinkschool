import { Component, DestroyRef, computed, inject, signal } from '@angular/core';
import { takeUntilDestroyed } from '@angular/core/rxjs-interop';
import { ActivatedRoute, Router, RouterLink } from '@angular/router';
import { ApiError } from '../../../core/http/api-error';
import { Auth } from '../../../core/auth/auth';

// Mirrors AuthService.RegisterAsync's own rule (day1/QuotesApi
// Services/AuthService.cs) so the form can reject a too-short password
// before ever making a request, rather than only finding out from the
// server's 400.
const MIN_PASSWORD_LENGTH = 8;

@Component({
  selector: 'app-register',
  imports: [RouterLink],
  templateUrl: './register.html',
  styleUrl: './register.css',
})
export class Register {
  private readonly auth = inject(Auth);
  private readonly router = inject(Router);
  private readonly route = inject(ActivatedRoute);
  private readonly destroyRef = inject(DestroyRef);

  readonly email = signal('');
  readonly password = signal('');
  readonly confirmPassword = signal('');
  readonly loading = signal(false);
  readonly error = signal<string | null>(null);

  // Same returnUrl convention as Login (see login.ts) - forwarded here too so
  // a user who was bounced to /login, then followed "Create an account",
  // still lands where they originally meant to go once registration
  // succeeds.
  readonly returnUrl = this.route.snapshot.queryParamMap.get('returnUrl') ?? '/quotes';

  readonly passwordTooShort = computed(
    () => this.password().length > 0 && this.password().length < MIN_PASSWORD_LENGTH,
  );
  readonly passwordsMismatch = computed(
    () => this.confirmPassword().length > 0 && this.confirmPassword() !== this.password(),
  );

  readonly canSubmit = computed(
    () =>
      this.email().trim().length > 0 &&
      this.password().length >= MIN_PASSWORD_LENGTH &&
      this.confirmPassword() === this.password(),
  );

  onEmailInput(value: string): void {
    this.email.set(value);
  }

  onPasswordInput(value: string): void {
    this.password.set(value);
  }

  onConfirmPasswordInput(value: string): void {
    this.confirmPassword.set(value);
  }

  onSubmit(event: Event): void {
    event.preventDefault();
    if (!this.canSubmit() || this.loading()) {
      return;
    }

    this.loading.set(true);
    this.error.set(null);

    this.auth
      .register(this.email().trim(), this.password())
      .pipe(takeUntilDestroyed(this.destroyRef))
      .subscribe({
        next: () => this.router.navigateByUrl(this.returnUrl),
        error: (err: unknown) => {
          this.loading.set(false);
          // ApiError kinds actually reachable here: 'conflict' (email
          // already registered), 'validation' (server-side re-check of the
          // same rules this form enforces client-side), 'network'/'server'.
          // All of them already carry a message worth showing verbatim.
          this.error.set(
            err instanceof ApiError ? err.message : 'Could not create your account. Please try again.',
          );
        },
      });
  }
}
