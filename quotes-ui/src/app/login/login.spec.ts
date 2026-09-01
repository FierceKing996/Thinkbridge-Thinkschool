import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ActivatedRoute, Router, provideRouter } from '@angular/router';
import { convertToParamMap } from '@angular/router';

import { Login } from './login';
import { errorMappingInterceptor } from '../error-mapping-interceptor';
import { Auth } from '../auth';

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

  function clickLogIn(): void {
    (fixture.nativeElement.querySelector('button') as HTMLButtonElement).click();
    fixture.detectChanges();
  }

  it('logs in then navigates to the returnUrl from the query string', () => {
    configure('/quotes/new');
    const navSpy = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);

    clickLogIn();

    httpMock.expectOne('/api/auth/login').flush({
      access_token: 't',
      refresh_token: 'r',
      expires_in: 900,
    });

    expect(TestBed.inject(Auth).isAuthenticated()).toBe(true);
    expect(navSpy).toHaveBeenCalledWith('/quotes/new');
  });

  it('falls back to /quotes when no returnUrl is present', () => {
    configure(null);
    const navSpy = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);

    clickLogIn();
    httpMock.expectOne('/api/auth/login').flush({
      access_token: 't',
      refresh_token: 'r',
      expires_in: 900,
    });

    expect(navSpy).toHaveBeenCalledWith('/quotes');
  });

  it('shows an error and does not navigate when login fails', () => {
    configure('/quotes/new');
    const navSpy = vi.spyOn(router, 'navigateByUrl').mockResolvedValue(true);

    clickLogIn();
    httpMock
      .expectOne('/api/auth/login')
      .flush(null, { status: 401, statusText: 'Unauthorized' });
    fixture.detectChanges();

    expect(navSpy).not.toHaveBeenCalled();
    expect(fixture.componentInstance.error()).not.toBeNull();
    expect(fixture.nativeElement.querySelector('.error')).toBeTruthy();
  });
});
