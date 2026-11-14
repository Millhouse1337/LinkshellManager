import { ChangeDetectionStrategy, Component, inject } from '@angular/core';
import { RouterLink } from '@angular/router';

import { WebAdminService } from '../../discord/web-admin.service';
import { ADMIN_BADGE } from '../activity-home.helpers';

// Website only, super admins only: the server-wide switches that used to live on the Razor
// Settings page (addon and Claim Shield kill switches, admin override, launcher link on/off), plus
// the way into Data Admin. The launcher's address is server config and only shown here.
// ActivityHomeComponent never opens this tab inside Discord or for anyone else, and the API
// refuses them regardless ([SuperAdminOnly] on WebAdminController).
@Component({
  selector: 'app-admin-settings-tab',
  imports: [RouterLink],
  templateUrl: './admin-settings-tab.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class AdminSettingsTabComponent {
  protected readonly admin = inject(WebAdminService);
  protected readonly ADMIN_BADGE = ADMIN_BADGE;

  constructor() {
    void this.admin.load();
  }
}
