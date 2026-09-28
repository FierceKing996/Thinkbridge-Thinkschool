import { ComponentFixture, TestBed } from '@angular/core/testing';
import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { Router, provideRouter } from '@angular/router';
import { App } from './app';
import { Auth } from './core/auth/auth';

describe('App', () => {
  let httpMock: HttpTestingController;

  beforeEach(async () => {
    sessionStorage.clear();
    await TestBed.configureTestingModule({
      imports: [App],
      // App is now a router shell. provideRouter([]) with no routes means
      // <router-outlet> renders nothing and no feature component mounts - so,
      // unlike before, there is no /api/quotes request to flush on creation.
      // App now also injects Auth (for the signed-in/signed-out nav split)
      // and HttpClient directly (to fetch GET /api/meta for the env badge -
      // see app.ts) - provideHttpClient/Testing is required here for both.
      providers: [provideRouter([]), provideHttpClient(), provideHttpClientTesting()],
    }).compileComponents();
    httpMock = TestBed.inject(HttpTestingController);
  });

  afterEach(() => {
    httpMock.verify();
    sessionStorage.clear();
  });

  // Every App creation fires GET /api/meta (app.ts's constructor) - drained
  // here so individual tests don't have to know about it unless they're
  // specifically asserting on the badge it renders.
  function createApp(metaEnvironment: string | null = 'dev'): ComponentFixture<App> {
    const fixture = TestBed.createComponent(App);
    fixture.detectChanges();
    const req = httpMock.expectOne('/api/meta');
    if (metaEnvironment === null) {
      req.flush(null, { status: 500, statusText: 'Server Error' });
    } else {
      req.flush({ environment: metaEnvironment });
    }
    fixture.detectChanges();
    return fixture;
  }

  it('should create the app', () => {
    const fixture = createApp();
    expect(fixture.componentInstance).toBeTruthy();
  });

  it('renders the title and nav, with the routed view empty until navigation', () => {
    const fixture = createApp();
    const compiled = fixture.nativeElement as HTMLElement;
    expect(compiled.querySelector('h1')?.textContent).toContain('Quotes');
    expect(compiled.querySelector('nav')).toBeTruthy();
    expect(compiled.querySelector('router-outlet')).toBeTruthy();
    // No feature component is mounted with an empty route table.
    expect(compiled.querySelector('app-quote-list')).toBeNull();
  });

  it('shows Sign in / Create account links, not Log out, when signed out', () => {
    const fixture = createApp();
    const compiled = fixture.nativeElement as HTMLElement;

    expect(compiled.querySelector('a[href="/login"]')).toBeTruthy();
    expect(compiled.querySelector('a[href="/register"]')).toBeTruthy();
    expect(compiled.querySelector('.nav-logout')).toBeNull();
  });

  it('renders the environment badge from GET /api/meta', () => {
    const fixture = createApp('dev');
    const compiled = fixture.nativeElement as HTMLElement;

    const badge = compiled.querySelector('.env-badge');
    expect(badge?.textContent?.trim()).toBe('dev');
    expect(badge?.classList.contains('env-badge--prod')).toBe(false);
  });

  it('renders no badge at all when GET /api/meta fails', () => {
    const fixture = createApp(null);
    const compiled = fixture.nativeElement as HTMLElement;

    expect(compiled.querySelector('.env-badge')).toBeNull();
  });

  it('shows Log out, not Sign in / Create account, once signed in, and logout() clears the session', () => {
    const fixture = createApp();
    // navigateByUrl is mocked - this spec asserts on Auth/nav state, not on
    // where the router actually lands (there are no real routes registered).
    const navSpy = vi.spyOn(TestBed.inject(Router), 'navigateByUrl').mockResolvedValue(true);

    TestBed.inject(Auth).login('user@example.com', 'correct-password').subscribe();
    httpMock.expectOne('/api/auth/login').flush({ access_token: 't', refresh_token: 'r', expires_in: 900 });
    fixture.detectChanges();

    const compiled = fixture.nativeElement as HTMLElement;
    const logoutButton = compiled.querySelector('.nav-logout') as HTMLButtonElement | null;
    expect(logoutButton).toBeTruthy();
    expect(compiled.querySelector('a[href="/login"]')).toBeNull();

    logoutButton!.click();
    fixture.detectChanges();

    expect(TestBed.inject(Auth).isAuthenticated()).toBe(false);
    expect(compiled.querySelector('a[href="/login"]')).toBeTruthy();
    expect(navSpy).toHaveBeenCalledWith('/quotes');

    // logout() best-effort revokes the refresh token server-side - drain
    // that request so httpMock.verify() in afterEach doesn't see it as
    // dangling.
    httpMock.expectOne('/api/auth/logout').flush(null);
  });
});
