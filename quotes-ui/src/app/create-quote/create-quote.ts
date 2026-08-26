import { Component, computed, inject, signal } from '@angular/core';
import { firstValueFrom } from 'rxjs';
import { FormField, FormRoot, form, maxLength, minLength, required, schema, validate } from '@angular/forms/signals';
import { ApiError } from '../api-error';
import { Quote } from '../quote';

interface CreateQuoteModel {
  author: string;
  text: string;
}

const EMPTY_MODEL: CreateQuoteModel = { author: '', text: '' };

// Mirrors Quote.Create()'s invariants exactly (day1/QuotesApi/Quote.cs:
// MinAuthorLength=1, MaxAuthorLength=200, MinTextLength=1,
// MaxTextLength=1000) so the client rejects up front what the server would
// reject anyway.
const AUTHOR_MIN_LENGTH = 1;
const AUTHOR_MAX_LENGTH = 200;
const TEXT_MIN_LENGTH = 1;
const TEXT_MAX_LENGTH = 1000;

// required()/minLength() alone are not enough to match the server: the
// server trims both fields before checking length (Extension.cs calls
// ITextNormalizer.Trim before Quote.Create()), so a whitespace-only value
// must fail validation - but @angular/forms/signals' isEmpty() only checks
// `value === ''`, it doesn't trim. Hence the extra validate() blank check on
// both fields below.
const createQuoteSchema = schema<CreateQuoteModel>((p) => {
  required(p.author, { message: 'Author is required.' });
  minLength(p.author, AUTHOR_MIN_LENGTH);
  maxLength(p.author, AUTHOR_MAX_LENGTH, {
    message: `Author must be ${AUTHOR_MAX_LENGTH} characters or fewer.`,
  });
  validate(p.author, ({ value }) =>
    value().trim().length === 0 ? { kind: 'blank', message: 'Author cannot be blank.' } : undefined,
  );

  required(p.text, { message: 'Quote text is required.' });
  minLength(p.text, TEXT_MIN_LENGTH);
  maxLength(p.text, TEXT_MAX_LENGTH, {
    message: `Quote text must be ${TEXT_MAX_LENGTH} characters or fewer.`,
  });
  validate(p.text, ({ value }) =>
    value().trim().length === 0 ? { kind: 'blank', message: 'Quote text cannot be blank.' } : undefined,
  );
});

// Distinguishes what actually went wrong on submit, since it's a genuinely
// different situation for the user in each case: a 400 means the server
// rejected the *content*, a 401/403 means their session/permissions are the
// problem, and anything else (thrown before a response, e.g. offline) is a
// network failure - none of these should be collapsed into one message.
type SubmitError = { kind: 'validation' | 'network' | 'auth'; message: string };

@Component({
  selector: 'app-create-quote',
  imports: [FormRoot, FormField],
  templateUrl: './create-quote.html',
  styleUrl: './create-quote.css',
})
export class CreateQuote {
  private readonly quoteService = inject(Quote);

  private readonly model = signal<CreateQuoteModel>({ ...EMPTY_MODEL });

  // Not `protected` - QuoteList/QuoteDetail's convention (see quote-list.ts,
  // quote-detail.ts) keeps state signals publicly readable so specs can
  // assert on them directly instead of only through the DOM.
  readonly submitError = signal<SubmitError | null>(null);
  readonly submitSuccess = signal(false);

  // formRoot (see the template) calls submit(quoteForm) with no arguments on
  // native form submit, which pulls these submission options from the form
  // itself - see @angular/forms/signals' submit(): `options ?? node.structure
  // .fieldManager.submitOptions`.
  readonly quoteForm = form(this.model, createQuoteSchema, {
    submission: {
      action: async (field) => {
        this.submitError.set(null);
        this.submitSuccess.set(false);

        const { author, text } = field().value();
        try {
          await firstValueFrom(this.quoteService.createQuote(author, text));
          // reset(value) both restores the pristine model AND clears
          // touched/dirty on every field - a plain model.set() wouldn't
          // touch the latter, and the field-level error text is gated on
          // touched(), so it would otherwise linger after a successful
          // submit that happened to briefly show now-stale errors.
          field().reset({ ...EMPTY_MODEL });
          this.submitSuccess.set(true);
        } catch (err) {
          this.submitError.set(this.mapSubmitError(err));
        }
        // No submission errors are reported back through the forms error
        // channel - the server's single generic message isn't tied to
        // either field, so submitError (rendered as a form-level banner) is
        // a truer representation than attaching it to one input.
        return undefined;
      },
      // Runs instead of `action` when client-side validation fails at
      // submit time (submit() marks every field touched first, so this also
      // makes the field-level error text visible). Move focus to the first
      // invalid field so keyboard/screen-reader users land right on it.
      onInvalid: (field) => {
        this.submitSuccess.set(false);
        if (field.author().invalid()) {
          field.author().focusBoundControl();
        } else if (field.text().invalid()) {
          field.text().focusBoundControl();
        }
      },
    },
  });

  readonly authorInvalid = computed(
    () => this.quoteForm.author().touched() && this.quoteForm.author().invalid(),
  );
  readonly textInvalid = computed(
    () => this.quoteForm.text().touched() && this.quoteForm.text().invalid(),
  );

  // The HTTP layer (see error-mapping-interceptor.ts, wired in globally in
  // app.config.ts) has already turned whatever the backend/network produced
  // into a typed ApiError with a friendly message by the time it reaches
  // here - this just narrows ApiError's five kinds down to this form's
  // three, it no longer re-derives anything from a raw HttpErrorResponse
  // (status codes, response bodies) itself.
  private mapSubmitError(err: unknown): SubmitError {
    if (err instanceof ApiError) {
      switch (err.kind) {
        case 'validation':
          return { kind: 'validation', message: err.message };
        case 'auth':
          return { kind: 'auth', message: err.message };
        case 'network':
        case 'server':
        case 'notFound':
          // 'server' and 'notFound' collapse into this form's 'network'
          // bucket too: POST /api/quotes can't genuinely 404, and a 5xx is,
          // from this form's point of view, the same "couldn't complete the
          // request" situation as a real network failure - none of them are
          // about what the user typed (validation) or who they're signed in
          // as (auth), which are the two cases this form treats specially.
          return { kind: 'network', message: err.message };
      }
    }
    return { kind: 'network', message: 'An unexpected error occurred. Please try again.' };
  }
}
