import {
  APP_INITIALIZER,
  ApplicationConfig,
  provideBrowserGlobalErrorListeners,
  provideZoneChangeDetection
} from '@angular/core';
import { provideRouter } from '@angular/router';
import { DiscordActivityService } from './discord/discord-activity.service';
import { routes } from './app.routes';

function initializeDiscordActivity(service: DiscordActivityService): () => Promise<void> {
  return () => service.initialize();
}

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideZoneChangeDetection({ eventCoalescing: true }),
    provideRouter(routes),
    {
      provide: APP_INITIALIZER,
      useFactory: initializeDiscordActivity,
      deps: [DiscordActivityService],
      multi: true
    }
  ]
};
