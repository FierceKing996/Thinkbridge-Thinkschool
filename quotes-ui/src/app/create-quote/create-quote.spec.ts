import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { By } from '@angular/platform-browser';

import { CreateQuote } from './create-quote';
import { errorMappingInterceptor } from '../error-mapping-interceptor';
import type { QuoteDto } from '../quote';

describe('CreateQuote', () => {
  let fixture: ComponentFixture<CreateQuote>;
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [CreateQuote],
      // errorMappingInterceptor is wired in here (matching app.config.ts's
      // real interceptor chain for the error path) so mapSubmitError() is
      // exercised against the same typed ApiError it receives in the real
      // app, not a raw HttpErrorResponse. authInterceptor/retryInterceptor
      // are deliberately left out - this suite only cares about the
      // request/response shape on /api/quotes itself, and pulling in the
      // token round-trip or retry backoff timing would only add unrelated
      // noise to these tests.
      providers: [provideHttpClient(withInterceptors([errorMappingInterceptor])), provideHttpClientTesting()],
    }).compileComponents();

    fixture = TestBed.createComponent(CreateQuote);
    httpMock = TestBed.inject(HttpTestingController);
    fixture.detectChanges();
  });

  afterEach(() => httpMock.verify());

  function authorInput(): HTMLInputElement {
    return fixture.debugElement.query(By.css('#author')).nativeElement as HTMLInputElement;
  }

  function textInput(): HTMLTextAreaElement {
    return fixture.debugElement.query(By.css('#text')).nativeElement as HTMLTextAreaElement;
  }

  function formEl(): HTMLFormElement {
    return fixture.debugElement.query(By.css('form')).nativeElement as HTMLFormElement;
  }

  function setValue(el: HTMLInputElement | HTMLTextAreaElement, value: string): void {
    el.value = value;
    el.dispatchEvent(new Event('input'));
  }

  function submitForm(): void {
    formEl().dispatchEvent(new Event('submit', { cancelable: true }));
  }

  it('should create', () => {
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('shows no error text and no aria-invalid before any interaction (pristine)', () => {
    expect(fixture.nativeElement.querySelector('.error')).toBeNull();
    expect(authorInput().getAttribute('aria-invalid')).toBeNull();
    expect(authorInput().getAttribute('aria-describedby')).toBeNull();
  });

  it('shows validation errors and focuses the first invalid field after submitting empty', async () => {
    submitForm();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(authorInput().getAttribute('aria-invalid')).toBe('true');
    expect(authorInput().getAttribute('aria-describedby')).toBe('author-error');
    expect(fixture.nativeElement.querySelector('#author-error')).toBeTruthy();
    expect(document.activeElement).toBe(authorInput());
  });

  it('rejects a whitespace-only author the same way the server would (blank after trim)', async () => {
    setValue(authorInput(), '   ');
    setValue(textInput(), 'Real text');
    fixture.detectChanges();

    submitForm();
    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.componentInstance.quoteForm.author().invalid()).toBe(true);
    expect(fixture.nativeElement.querySelector('#author-error').textContent).toContain('blank');
    // No HTTP call should have been made - client-side validation must catch this.
    httpMock.expectNone('/api/quotes');
  });

  it('creates the quote, shows success, and clears the form', async () => {
    setValue(authorInput(), 'Ada Lovelace');
    setValue(textInput(), 'Some quote text');
    fixture.detectChanges();

    submitForm();
    fixture.detectChanges();

    const req = httpMock.expectOne('/api/quotes');
    expect(req.request.method).toBe('POST');
    expect(req.request.body).toEqual({ author: 'Ada Lovelace', text: 'Some quote text' });

    const created: QuoteDto = {
      id: 9,
      author: 'Ada Lovelace',
      text: 'Some quote text',
      isDeleted: false,
      createdByUserId: 1,
    };
    req.flush(created, { status: 201, statusText: 'Created' });

    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.componentInstance.submitSuccess()).toBe(true);
    expect(fixture.nativeElement.querySelector('.success')).toBeTruthy();
    expect(authorInput().value).toBe('');
    expect(textInput().value).toBe('');
  });

  it("surfaces the server's single generic validation message on a 400", async () => {
    setValue(authorInput(), 'A');
    setValue(textInput(), 'Some text');
    fixture.detectChanges();

    submitForm();
    fixture.detectChanges();

    const req = httpMock.expectOne('/api/quotes');
    req.flush(
      {
        type: 'https://tools.ietf.org/html/rfc9110#section-15.5.1',
        title: 'One or more validation errors occurred.',
        status: 400,
        errors: { error: ['Author must be between 1 and 200 characters.'] },
      },
      { status: 400, statusText: 'Bad Request' },
    );

    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.componentInstance.submitError()).toEqual({
      kind: 'validation',
      message: 'Author must be between 1 and 200 characters.',
    });
    expect(fixture.nativeElement.querySelector('.banner').textContent).toContain(
      'Author must be between 1 and 200 characters.',
    );
  });

  it('shows a distinct message for a network failure (no response)', async () => {
    setValue(authorInput(), 'Ada');
    setValue(textInput(), 'Some text');
    fixture.detectChanges();

    submitForm();
    fixture.detectChanges();

    const req = httpMock.expectOne('/api/quotes');
    req.error(new ProgressEvent('network error'));

    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.componentInstance.submitError()?.kind).toBe('network');
  });

  it('shows a distinct message for an auth failure (401)', async () => {
    setValue(authorInput(), 'Ada');
    setValue(textInput(), 'Some text');
    fixture.detectChanges();

    submitForm();
    fixture.detectChanges();

    const req = httpMock.expectOne('/api/quotes');
    req.flush(null, { status: 401, statusText: 'Unauthorized' });

    await fixture.whenStable();
    fixture.detectChanges();

    expect(fixture.componentInstance.submitError()?.kind).toBe('auth');
  });

  it('disables the submit button while the request is in flight (no double-submit)', () => {
    setValue(authorInput(), 'Ada');
    setValue(textInput(), 'Some text');
    fixture.detectChanges();

    submitForm();
    fixture.detectChanges();

    const button = fixture.nativeElement.querySelector('button[type="submit"]') as HTMLButtonElement;
    expect(button.disabled).toBe(true);

    httpMock.expectOne('/api/quotes').flush(
      { id: 1, author: 'Ada', text: 'Some text', isDeleted: false, createdByUserId: 1 },
      { status: 201, statusText: 'Created' },
    );
  });
});
