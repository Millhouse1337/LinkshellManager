import {
  ApplicationConfig,
  inject,
  provideAppInitializer,
  provideBrowserGlobalErrorListeners,
  provideZoneChangeDetection
} from '@angular/core';
import { provideRouter } from '@angular/router';
import { DiscordActivityService } from './discord/discord-activity.service';
import { routes } from './app.routes';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideZoneChangeDetection({ eventCoalescing: true }),
    provideRouter(routes),
    // Run the Discord Activity handshake before the app renders. Replaces the
    // deprecated APP_INITIALIZER multi-provider with the Angular 19+ helper.
    provideAppInitializer(() => inject(DiscordActivityService).initialize())
  ]
};
