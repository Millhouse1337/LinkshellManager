import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, inject, model } from '@angular/core';

import { DiscordActivityService } from '../../discord/discord-activity.service';
import { type TreasuryView } from '../activity-home.types';
import { FinancesSectionComponent } from './finances-section.component';
import { ItemsSectionComponent } from './items-section.component';

/**
 * Treasury: everything the linkshell owns.
 *
 * Gil and Items, which used to be buried at the bottom of the Management tab below the roster. They
 * belong together and away from people: an item is unsold value and gil is realised value, and
 * selling an item turns one into the other in a single transaction.
 *
 * Three views, one at a time, picked by the strip at the top right: Balance Sheet and Transactions
 * (both Gil) and Items. Stacked, the transactions list ran long enough to bury everything under it.
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

  // The app's one current linkshell (AuthService.currentLinkshellId), or null when there is none.
  protected readonly linkshellId = computed<number | null>(() => this.activity.currentLinkshellId() || null);

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

  // Two-way `view` binding: ActivityHomeComponent keeps it in the location (/treasury/items).
  readonly view = model<TreasuryView>('balance');

  // Balance Sheet and Transactions are both Gil. (Gil and Items used to have a feature toggle each;
  // one Treasury toggle now hides the whole tab, in ActivityHomeComponent.)
  protected readonly views: readonly { key: TreasuryView; label: string }[] = [
    { key: 'balance', label: 'Balance Sheet' },
    { key: 'items', label: 'Items' },
    { key: 'transactions', label: 'Transactions' }
  ];
}
