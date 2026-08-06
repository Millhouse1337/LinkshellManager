import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject } from '@angular/core';

import { DiscordActivityService } from '../../discord/discord-activity.service';
import { FinancesSectionComponent } from './finances-section.component';
import { ItemsSectionComponent } from './items-section.component';

/**
 * Treasury: everything the linkshell owns.
 *
 * Two sections — **Gil** and **Items** — which used to be buried at the bottom of the Management tab
 * below the roster. They belong together and away from people: an item is unsold value and gil is
 * realised value, and selling an item turns one into the other in a single transaction.
 *
 * Management is now just people again: roster, ranks, invites.
 */
@Component({
  selector: 'app-treasury-tab',
  imports: [CommonModule, FinancesSectionComponent, ItemsSectionComponent],
  templateUrl: './treasury-tab.component.html',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class TreasuryTabComponent {
  protected readonly activity = inject(DiscordActivityService);

  protected readonly linkshellId = computed<number | null>(() =>
    this.activity.overview()?.appUser?.primaryLinkshellId
      ?? this.activity.overview()?.primaryLinkshell?.id
      ?? this.activity.overview()?.linkshells?.[0]?.id
      ?? null
  );

  private readonly membership = computed(() => {
    const id = this.linkshellId();
    if (id === null) {
      return null;
    }
    return (this.activity.overview()?.linkshells ?? []).find(link => link.id === id) ?? null;
  });

  /**
   * Whether this member can change the stash. Gil has its own separate permission and
   * FinancesSectionComponent reads that one straight off the treasury payload — the server's own
   * answer rather than a second copy of the rule, since the copy is what drifted last time and showed
   * officers a form the server then rejected.
   */
  protected readonly canManageItems = computed(() => {
    const membership = this.membership();
    if (!membership) {
      return false;
    }
    if ((membership.rank ?? '').toLowerCase() === 'leader') {
      return true;
    }
    return membership.permissions?.canManageInventory === true;
  });

  // Feature toggles hide either half independently. Both default to shown when the settings have not
  // loaded yet, so the tab never flashes empty on a slow overview.
  protected readonly showGil = computed(
    () => this.membership()?.settings?.enableRevenue !== false
  );

  protected readonly showItems = computed(
    () => this.membership()?.settings?.enableItems !== false
  );
}
