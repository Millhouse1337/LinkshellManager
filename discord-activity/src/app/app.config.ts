import {
  ApplicationConfig,
  ErrorHandler,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
  provideZoneChangeDetection
} from '@angular/core';
import { provideRouter, withNavigationErrorHandler } from '@angular/router';
import { DiscordActivityService } from './discord/discord-activity.service';
import { reloadOnceForStaleBuild } from './discord/stale-build';
import { routes } from './app.routes';

// Each tab's code loads through an @defer block the first time it opens. After a deploy, a page
// left open asks for the old build's tab file, which is gone: Angular then reports the failure
// here (not to the router) and leaves the tab blank, so reload once, like the router's handler below.
class StaleBuildErrorHandler extends ErrorHandler {
  override handleError(error: unknown): void {
    if (reloadOnceForStaleBuild(error)) {
      return;
    }
    super.handleError(error);
  }
}

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    { provide: ErrorHandler, useClass: StaleBuildErrorHandler },
    provideZoneChangeDetection({ eventCoalescing: true }),
    // A page left open across a deploy asks for the old build's home-screen file, which is gone:
    // reload once to pick up the new build instead of showing a blank page (stale-build.ts).
    provideRouter(routes, withNavigationErrorHandler(navigationError => {
      reloadOnceForStaleBuild(navigationError.error);
    })),
    // Run the Discord Activity handshake before the app renders. Replaces the
    // deprecated APP_INITIALIZER multi-provider with the Angular 19+ helper.
    provideAppInitializer(() => inject(DiscordActivityService).initialize())
  ]
};
