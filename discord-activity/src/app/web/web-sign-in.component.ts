import { ChangeDetectionStrategy, Component, inject } from '@angular/core';

import { DiscordActivityService } from '../discord/discord-activity.service';

// The website's sign-in screen. app.html shows it INSTEAD of the app (so nothing polls)
// whenever the browser has no cookie session: on first visit, or when a request comes back
// 401 because the session ended. Never shown inside Discord, which signs in on its own.
@Component({
  selector: 'app-web-sign-in',
  templateUrl: './web-sign-in.component.html',
  styleUrl: './web-sign-in.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class WebSignInComponent {
  protected readonly activity = inject(DiscordActivityService);

  // Comes back to the same page after Discord sign-in. DiscordAccountController.Callback only
  // follows local URLs, so this can't be turned into an open redirect.
  protected readonly signInUrl =
    `/auth/discord/login?returnUrl=${encodeURIComponent(location.pathname + location.search)}`;
}
