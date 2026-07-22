import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, computed, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { DiscordActivityService } from '../../discord/discord-activity.service';
import { DkpSheetService } from '../../discord/dkp-sheet.service';
import type { ActivityDkpSheetMember, ActivityDkpSheetPool } from '../../discord/discord-activity.types';

// Always-on DKP sheet for the Activity: summary cards + a filterable member
// table, computed from the app's own DKP (no Google connection). Mirrors the
// web DKP Sheet page.
@Component({
  selector: 'app-dkp-sheet-tab',
  imports: [CommonModule, FormsModule],
  templateUrl: './dkp-sheet-tab.component.html',
  styleUrl: './dkp-sheet-tab.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class DkpSheetTabComponent {
  protected readonly activity = inject(DiscordActivityService);
  protected readonly dkpSheet = inject(DkpSheetService);

  protected readonly searchTerm = signal('');

  constructor() {
    effect(() => {
      const id = this.primaryLinkshellId();
      if (id) queueMicrotask(() => void this.dkpSheet.load(id));
    });
  }

  protected primaryLinkshellId(): number {
    return this.activity.overview()?.primaryLinkshell?.id ?? this.activity.overview()?.appUser?.primaryLinkshellId ?? 0;
  }

  protected data() {
    return this.dkpSheet.data();
  }

  protected readonly filteredMembers = computed<ActivityDkpSheetMember[]>(() => {
    const members = this.dkpSheet.data()?.members ?? [];
    const term = this.searchTerm().trim().toLowerCase();
    if (!term) return members;
    return members.filter(m =>
      `${m.name} ${m.alt1} ${m.alt2}`.toLowerCase().includes(term));
  });

  // Empty unless the linkshell has more than one DKP pool, in which case the table grows a
  // spendable-balance column per pool between the alts and Current DKP.
  protected readonly pools = computed<ActivityDkpSheetPool[]>(() => this.dkpSheet.data()?.pools ?? []);
}
