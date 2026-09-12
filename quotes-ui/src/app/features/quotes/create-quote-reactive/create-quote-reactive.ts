import { Component, ElementRef, ViewChild, inject, signal } from '@angular/core';
import {
  AbstractControl,
  FormBuilder,
  ReactiveFormsModule,
  ValidationErrors,
  ValidatorFn,
  Validators,
} from '@angular/forms';
import { HttpErrorResponse } from '@angular/common/http';
import { firstValueFrom } from 'rxjs';
import { Quote } from '../../../core/models/quote';

// This is the Reactive Forms twin of create-quote.ts, built as a direct
// comparison baseline against the Signal Forms (preview) implementation. It
// intentionally reproduces the exact same behavior and messages - see
// create-quote.ts's comments for the authoritative rationale, including the
// primary-source references into day1/QuotesApi/Quote.cs and Extension.cs.

interface CreateQuoteFormValue {
  author: string;
  text: string;
}

const EMPTY_VALUE: CreateQuoteFormValue = { author: '', text: '' };

// Mirrors Quote.Create()'s invariants (day1/QuotesApi/Quote.cs:
// MinAuthorLength=1, MaxAuthorLength=200, MinTextLength=1,
// MaxTextLength=1000). Min lengths are both 1, so Validators.required
// already covers the "empty" case for both fields - a separate minLength
// validator would be redundant here (unlike a hypothetical min > 1).
const AUTHOR_MAX_LENGTH = 200;
const TEXT_MAX_LENGTH = 1000;

// Validators.required only rejects `''`/null/undefined - like
// @angular/forms/signals' isEmpty(), it does not trim. The server trims
// both fields before checking length (Extension.cs calls
// ITextNormalizer.Trim before Quote.Create()), so a whitespace-only value
// must fail validation too. This custom validator owns exactly that case;
// it defers the true-empty case to Validators.required (see the `=== ''`
// guard below) so the two validators don't both fire for the same input,
// keeping error precedence (required before blank) predictable.
function notBlankValidator(): ValidatorFn {
  return (control: AbstractControl): ValidationErrors | null => {
    const value = control.value as string | null;
    if (value == null || value === '') {
      return null;
    }
    return value.trim().length === 0 ? { blank: true } : null;
  };
}

// Distinguishes what actually went wrong on submit, since it's a genuinely
// different situation for the user in each case: a 400 means the server
// rejected the *content*, a 401/403 means their session/permissions are the
// problem, and anything else (thrown before a response, e.g. offline) is a
// network failure - none of these should be collapsed into one message.
type SubmitError = { kind: 'validation' | 'network' | 'auth'; message: string };

@Component({
  selector: 'app-create-quote-reactive',
  imports: [ReactiveFormsModule],
  templateUrl: './create-quote-reactive.html',
  styleUrl: './create-quote-reactive.css',
})
export class CreateQuoteReactive {
  private readonly quoteService = inject(Quote);
  private readonly fb = inject(FormBuilder);

  // Exposed for the template's [attr.maxlength] bindings (see
  // create-quote-reactive.html). Signal Forms' FormField directive sets the
  // native `maxlength` attribute automatically as a side effect of the
  // schema's maxLength() validator (see create-quote.ts); Validators.maxLength()
  // here is JS-only and never touches the DOM, so the native-truncation UX has
  // to be wired up by hand. Reusing these same constants (rather than
  // hardcoding 200/1000 again in the template) keeps them from drifting apart
  // from AUTHOR_MAX_LENGTH/TEXT_MAX_LENGTH above.
  readonly authorMaxLength = AUTHOR_MAX_LENGTH;
  readonly textMaxLength = TEXT_MAX_LENGTH;

  // Reactive Forms has no formField-style directive that both binds a
  // control AND exposes a focus hook, so a plain template reference variable
  // per native element is how focus-on-invalid is implemented below (see
  // focusFirstInvalid()).
  @ViewChild('authorInput') private readonly authorInputRef?: ElementRef<HTMLInputElement>;
  @ViewChild('textInput') private readonly textInputRef?: ElementRef<HTMLTextAreaElement>;

  // Not `protected` - matches create-quote.ts's convention of keeping state
  // signals publicly readable so specs can assert on them directly instead
  // of only through the DOM.
  readonly submitError = signal<SubmitError | null>(null);
  readonly submitSuccess = signal(false);

  // Reactive Forms has no built-in submitting() signal the way the Signal
  // Forms `form()` resource does - this is hand-rolled state, set/cleared
  // around the async submit call below.
  readonly submitting = signal(false);

  readonly quoteForm = this.fb.nonNullable.group({
    author: this.fb.nonNullable.control(EMPTY_VALUE.author, [
      Validators.required,
      Validators.maxLength(AUTHOR_MAX_LENGTH),
      notBlankValidator(),
    ]),
    text: this.fb.nonNullable.control(EMPTY_VALUE.text, [
      Validators.required,
      Validators.maxLength(TEXT_MAX_LENGTH),
      notBlankValidator(),
    ]),
  });

  get author(): AbstractControl<string> {
    return this.quoteForm.controls.author;
  }

  get text(): AbstractControl<string> {
    return this.quoteForm.controls.text;
  }

  // Plain methods, not computed() - FormControl's touched/invalid are
  // ordinary mutable properties, not signals, so a computed() here would
  // read them once and never invalidate. The app is zoneless
  // (provideZonelessChangeDetection() in app.config.ts); it relies on
  // Angular's zoneless scheduler re-running change detection after any
  // event handled through a template/host binding (which is how the
  // ControlValueAccessor directives underlying formControlName report
  // input/blur), so a plain method re-evaluated on each such pass is both
  // necessary and sufficient here.
  authorInvalid(): boolean {
    return this.author.touched && this.author.invalid;
  }

  textInvalid(): boolean {
    return this.text.touched && this.text.invalid;
  }

  authorErrorMessage(): string | null {
    return this.errorMessage(this.author, 'Author', AUTHOR_MAX_LENGTH);
  }

  textErrorMessage(): string | null {
    return this.errorMessage(this.text, 'Quote text', TEXT_MAX_LENGTH);
  }

  private errorMessage(control: AbstractControl, label: string, maxLength: number): string | null {
    const errors = control.errors;
    if (!errors) {
      return null;
    }
    if (errors['required']) {
      return `${label} is required.`;
    }
    if (errors['maxlength']) {
      return `${label} must be ${maxLength} characters or fewer.`;
    }
    if (errors['blank']) {
      return `${label} cannot be blank.`;
    }
    return 'Invalid ' + label.toLowerCase() + '.';
  }

  async onSubmit(): Promise<void> {
    if (this.quoteForm.invalid) {
      // Reactive Forms has no automatic "touch on submit" - markAllAsTouched()
      // is what makes the field-level error text visible (it's gated on
      // touched in the template, same as the Signal Forms version).
      this.quoteForm.markAllAsTouched();
      this.submitSuccess.set(false);
      this.focusFirstInvalid();
      return;
    }

    this.submitError.set(null);
    this.submitSuccess.set(false);
    this.submitting.set(true);

    const { author, text } = this.quoteForm.getRawValue();
    try {
      await firstValueFrom(this.quoteService.createQuote(author, text));
      // reset(value) restores the pristine value AND clears touched/dirty on
      // every control - a plain setValue() wouldn't touch the latter, and
      // the field-level error text is gated on touched, so it would
      // otherwise linger after a successful submit that happened to briefly
      // show now-stale errors.
      this.quoteForm.reset({ ...EMPTY_VALUE });
      this.submitSuccess.set(true);
    } catch (err) {
      this.submitError.set(this.mapSubmitError(err));
    } finally {
      this.submitting.set(false);
    }
  }

  // Focus moves to the first invalid field so keyboard/screen-reader users
  // land right on it - Reactive Forms has nothing like Signal Forms'
  // focusBoundControl(), hence the ElementRef-backed template refs above.
  private focusFirstInvalid(): void {
    if (this.author.invalid) {
      this.authorInputRef?.nativeElement.focus();
    } else if (this.text.invalid) {
      this.textInputRef?.nativeElement.focus();
    }
  }

  private mapSubmitError(err: unknown): SubmitError {
    if (err instanceof HttpErrorResponse) {
      if (err.status === 401 || err.status === 403) {
        return {
          kind: 'auth',
          message: 'You are not authorized to create quotes. Please sign in again.',
        };
      }
      if (err.status === 400) {
        return { kind: 'validation', message: this.extractValidationMessage(err) };
      }
      return {
        kind: 'network',
        message: 'Could not reach the server. Check your connection and try again.',
      };
    }
    return { kind: 'network', message: 'An unexpected error occurred. Please try again.' };
  }

  // The server always reports validation failures under a single generic
  // "error" key (Results.ValidationProblem(new Dictionary<string, string[]>
  // { ["error"] = [...] }) in Extension.cs) - never split per-field, even
  // though CreateQuoteRequest has two fields. Don't assume errors.author /
  // errors.text exist.
  private extractValidationMessage(err: HttpErrorResponse): string {
    const body = err.error as { errors?: Record<string, string[]> } | null | undefined;
    const messages = body?.errors?.['error'];
    return messages && messages.length > 0 ? messages[0] : 'The quote could not be created.';
  }
}
