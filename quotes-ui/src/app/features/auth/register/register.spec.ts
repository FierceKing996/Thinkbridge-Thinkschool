import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, Router, provideRouter, convertToParamMap } from '@angular/router';

import { Register } from './register';
import { errorMappingInterceptor } from '../../../core/http/error-mapping-interceptor';
import { Auth } from '../../../core/auth/auth';

describe('Register', () => {
  let fixture: ComponentFixture<Register>;
  let httpMock: HttpTestingController;
  let router: Router;

  function configure(returnUrl: string | null) {
    TestBed.configureTestingModule({
      imports: [Register],
      providers: [
        provideHttpClient(withInterceptors([errorMappingInterceptor])),
        provideHttpClientTesting(),
        provideRouter([]),
        {
          provide: ActivatedRoute,
          useValue: {
            snapshot: {
              queryParamMap: convertToParamMap(returnUrl === null ? {} : { returnUrl }),
            },
          },
        },
      ],
    });
    fixture = TestBed.createComponent(Register);
    httpMock = TestBed.inject(HttpTestingController);
    router = TestBed.inject(Router);
    fixture.detectChanges();
  }

  afterEach(() => httpMock.verify());

  function setInput(selector: string, value: string): void {
    const input = fixture.nativeElement.querySelector(selector) as HTMLInputElement;
    input.value = value;
    input.dispatchEvent(new Event('input'));
  }

  function fillAndSubmit(email: string, password: string, confirmPassword: string): void {
    setInput('input[name="email"]', email);
    setInput('input[name="password"]', password);
    setInput('input[name="confirmPassword"]', confirmPassword);
    fixture.detectChanges();
    const form = fixture.nativeElement.querySelector('form') as HTMLFormElement;
    form.dispatchEvent(new Event('submit', { cancelable: true }));
    fixture.detectChanges();
  }

  it('registers with the entered credentials, signs the user in and navigates to the returnUrl', () => {
    configure('/quotes/new');
    const navSpy = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);

    fillAndSubmit('new@example.com', 'a-strong-password', 'a-strong-password');

    const req = httpMock.expectOne('/api/auth/register');
    expect(req.request.body).toEqual({ email: 'new@example.com', password: 'a-strong-password' });
    req.flush({ access_token: 't', refresh_token: 'r', expires_in: 900 });

    expect(TestBed.inject(Auth).isAuthenticated()).toBe(true);
    expect(navSpy).toHaveBeenCalledWith('/quotes/new');
  });

  it('falls back to /quotes when no returnUrl is present', () => {
    configure(null);
    const navSpy = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);

    fillAndSubmit('new@example.com', 'a-strong-password', 'a-strong-password');
    httpMock.expectOne('/api/auth/register').flush({ access_token: 't', refresh_token: 'r', expires_in: 900 });

    expect(navSpy).toHaveBeenCalledWith('/quotes');
  });

  it('disables submit and shows a hint when the password is too short', () => {
    configure(null);

    setInput('input[name="password"]', 'short');
    fixture.detectChanges();

    expect(fixture.componentInstance.passwordTooShort()).toBe(true);
    const button = fixture.nativeElement.querySelector('button[type="submit"]') as HTMLButtonElement;
    expect(button.disabled).toBe(true);
    httpMock.expectNone('/api/auth/register');
  });

  it('disables submit and shows a hint when the passwords do not match', () => {
    configure(null);

    setInput('input[name="email"]', 'new@example.com');
    setInput('input[name="password"]', 'a-strong-password');
    setInput('input[name="confirmPassword"]', 'something-else');
    fixture.detectChanges();

    expect(fixture.componentInstance.passwordsMismatch()).toBe(true);
    const button = fixture.nativeElement.querySelector('button[type="submit"]') as HTMLButtonElement;
    expect(button.disabled).toBe(true);
  });

  it('shows the server message and does not navigate when the email is already taken', () => {
    configure(null);
    const navSpy = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);

    fillAndSubmit('taken@example.com', 'a-strong-password', 'a-strong-password');
    httpMock.expectOne('/api/auth/register').flush(
      { detail: 'An account with that email already exists.' },
      { status: 409, statusText: 'Conflict' },
    );
    fixture.detectChanges();

    expect(navSpy).not.toHaveBeenCalled();
    expect(fixture.componentInstance.error()).toBe('An account with that email already exists.');
  });
});
