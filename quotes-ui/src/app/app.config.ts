import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { authInterceptor } from './auth-interceptor';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    // ng new --zoneless already omits zone.js from the build (no polyfills
    // entry in angular.json), but it did NOT add this - the schematic leaves
    // the app relying on zone.js's mere absence rather than the documented,
    // explicit opt-in. Added by hand so the app's zoneless-ness doesn't
    // depend on an omission nobody has to notice.
    provideZonelessChangeDetection(),
    provideHttpClient(withInterceptors([authInterceptor])),
  ]
};
