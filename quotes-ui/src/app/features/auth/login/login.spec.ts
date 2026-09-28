import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { convertToParamMap } from '@angular/router';

import { Login } from './login';
import { errorMappingInterceptor } from '../../../core/http/error-mapping-interceptor';
import { Auth } from '../../../core/auth/auth';

describe('Login', () => {
  let fixture: ComponentFixture<Login>;
  let httpMock: HttpTestingController;
  let router: Router;

  function configure(returnUrl: string | null) {
    TestBed.configureTestingModule({
      imports: [Login],
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
    fixture = TestBed.createComponent(Login);
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

  function fillAndSubmit(email: string, password: string): void {
    setInput('input[name="email"]', email);
    setInput('input[name="password"]', password);
    fixture.detectChanges();
    const form = fixture.nativeElement.querySelector('form') as HTMLFormElement;
    form.dispatchEvent(new Event('submit', { cancelable: true }));
    fixture.detectChanges();
  }

  it('logs in with the entered credentials and navigates to the returnUrl from the query string', () => {
    configure('/quotes/new');
    const navSpy = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);

    fillAndSubmit('user@example.com', 'correct-password');

    const req = httpMock.expectOne('/api/auth/login');
    expect(req.request.body).toEqual({ email: 'user@example.com', password: 'correct-password' });
    req.flush({ access_token: 't', refresh_token: 'r', expires_in: 900 });

    expect(TestBed.inject(Auth).isAuthenticated()).toBe(true);
    expect(navSpy).toHaveBeenCalledWith('/quotes/new');
  });

  it('falls back to /quotes when no returnUrl is present', () => {
    configure(null);
    const navSpy = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);

    fillAndSubmit('user@example.com', 'correct-password');
    httpMock.expectOne('/api/auth/login').flush({
      access_token: 't',
      refresh_token: 'r',
      expires_in: 900,
    });

    expect(navSpy).toHaveBeenCalledWith('/quotes');
  });

  it('does not submit while email or password is empty', () => {
    configure(null);

    // Only email filled in - the submit button stays disabled and the form
    // handler bails out even if a submit event fires anyway.
    setInput('input[name="email"]', 'user@example.com');
    fixture.detectChanges();
    const button = fixture.nativeElement.querySelector('button[type="submit"]') as HTMLButtonElement;
    expect(button.disabled).toBe(true);

    const form = fixture.nativeElement.querySelector('form') as HTMLFormElement;
    form.dispatchEvent(new Event('submit', { cancelable: true }));

    httpMock.expectNone('/api/auth/login');
  });

  it('shows a specific message and does not navigate when login fails with 401', () => {
    configure('/quotes/new');
    const navSpy = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);

    fillAndSubmit('user@example.com', 'wrong-password');
    httpMock
      .expectOne('/api/auth/login')
      .flush(null, { status: 401, statusText: 'Unauthorized' });
    fixture.detectChanges();

    expect(navSpy).not.toHaveBeenCalled();
    expect(fixture.componentInstance.error()).toBe('Incorrect email or password.');
    expect(fixture.nativeElement.querySelector('.error')).toBeTruthy();
  });
});
