import { Component, inject } from '@angular/core';
import { RouterOutlet } from '@angular/router';

import { DiscordActivityService } from './discord/discord-activity.service';
import { WebSignInComponent } from './web/web-sign-in.component';

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, WebSignInComponent],
  templateUrl: './app.html',
  styleUrl: './app.scss'
})
export class App {
  protected readonly activity = inject(DiscordActivityService);
}
