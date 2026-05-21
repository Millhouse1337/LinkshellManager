import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';

import { DiscordActivityService } from '../../discord/discord-activity.service';
import { DkpSheetService } from '../../discord/dkp-sheet.service';
import type { ActivityDkpSheetTab } from '../../discord/discord-activity.types';

// Read-only viewer for the linkshell's synced Google Sheet: a tab strip + a
// search box + the active tab's table. Main tab pins column A narrow (mirrors
// the web viewer). Not-connected/error states render in place.
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

  protected readonly activeTabName = signal<string | null>(null);
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

  // Resolved active tab: the selected one, else the first (so the view has a
  // tab to show before the user clicks). Highlighting uses activeTab()?.name.
  protected activeTab(): ActivityDkpSheetTab | null {
    const tabs = this.data()?.tabs ?? [];
    const name = this.activeTabName();
    return tabs.find(tab => tab.name === name) ?? tabs[0] ?? null;
  }

  protected selectTab(name: string): void {
    this.activeTabName.set(name);
  }

  protected filteredRows() {
    const tab = this.activeTab();
    if (!tab) return [];
    const term = this.searchTerm().trim().toLowerCase();
    if (!term) return tab.rows;
    return tab.rows.filter(row => row.searchText.includes(term));
  }

  protected async openInSheets(): Promise<void> {
    const url = this.data()?.openInSheetsUrl;
    if (url) await this.activity.openExternalLink(url);
  }
}
