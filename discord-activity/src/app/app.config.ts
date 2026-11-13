import {
  ApplicationConfig,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
  provideZoneChangeDetection
} from '@angular/core';
import { provideRouter, withNavigationErrorHandler } from '@angular/router';
import { DiscordActivityService } from './discord/discord-activity.service';
import { reloadOnceForStaleBuild } from './discord/stale-build';
import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
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
