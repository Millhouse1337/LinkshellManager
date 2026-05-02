import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ActivityLinkshellSettings,
  ActivityLootStructure,
  DiscordActivityService
} from '../discord/discord-activity.service';
import { ActivitySidebarPanelComponent } from './activity-sidebar-panel.component';
import type { TabName } from './activity-home.types';
import { ConfigurationsTabComponent } from './tabs/configurations-tab.component';
import { DashboardTabComponent } from './tabs/dashboard-tab.component';
import { EventsTabComponent } from './tabs/events-tab.component';
import { LinkshellTabComponent } from './tabs/linkshell-tab.component';
import { TodsTabComponent } from './tabs/tods-tab.component';

@Component({
  selector: 'app-activity-home',
  imports: [
    CommonModule,
    FormsModule,
    ActivitySidebarPanelComponent,
    ConfigurationsTabComponent,
    DashboardTabComponent,
    EventsTabComponent,
    LinkshellTabComponent,
    TodsTabComponent
  ],
  templateUrl: './activity-home.component.html',
  styleUrl: './activity-home.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ActivityHomeComponent {
  protected readonly activity = inject(DiscordActivityService);
  protected readonly activeTab = signal<TabName>('dashboard');

  protected setActiveTab(tab: TabName): void {
    this.activeTab.set(tab);
    window.scrollTo({ top: 0, behavior: 'smooth' });
  }

  // Stable callback bindings handed to child tabs. Defining them as bound
  // arrow-property fields keeps `this` correct without re-allocating in the
  // template every change-detection pass.
  protected readonly setActiveTabFn = (tab: TabName): void => this.setActiveTab(tab);
  protected readonly deleteTodFn = (todId: number, monsterName: string): void =>
    this.deleteTod(todId, monsterName);

  // Roster search is shared by the Dashboard and Linkshell tabs — keeping it
  // here means the value persists when the user hops between those tabs.
  protected dashboardRosterSearch = '';
  protected readonly setDashboardRosterSearch = (value: string): void => {
    this.dashboardRosterSearch = value;
  };

  // ----- Identity bar -----

  protected initials(value: string | null | undefined): string {
    const name = (value ?? '').trim();
    if (!name) return '??';
    const parts = name.split(/\s+/).filter(Boolean);
    if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
    return (parts[0][0] + parts[1][0]).toUpperCase();
  }

  protected appUserRoleLabel(): string {
    const linkshells = this.activity.overview()?.linkshells ?? [];
    if (linkshells.length === 0) return 'Member';
    const primaryId = this.activity.overview()?.appUser?.primaryLinkshellId;
    const primary = linkshells.find(l => l.id === primaryId) ?? linkshells[0];
    const rank = (primary?.rank ?? 'Member').toString();
    return rank.charAt(0).toUpperCase() + rank.slice(1).toLowerCase();
  }

  protected primaryLinkshellName(): string {
    return this.primaryLinkshell()?.name || this.activity.overview()?.appUser?.primaryLinkshellName || 'No linkshell';
  }

  protected primaryMemberCount(): number {
    return this.primaryLinkshell()?.memberCount ?? 0;
  }

  protected appDisplayName(): string {
    const overviewUser = this.activity.overview()?.appUser;
    const localUser = this.activity.localUser();
    const sessionUser = this.activity.session()?.user;

    return (
      overviewUser?.characterName ||
      localUser?.appUser?.characterName ||
      localUser?.globalName ||
      sessionUser?.global_name ||
      overviewUser?.userName ||
      localUser?.username ||
      sessionUser?.username ||
      'Linkshell member'
    );
  }

  protected primaryLinkshell() {
    return this.activity.overview()?.primaryLinkshell ?? null;
  }

  protected primaryLinkshellSettings(): ActivityLinkshellSettings | null {
    const primaryId = this.activity.overview()?.appUser?.primaryLinkshellId;
    if (primaryId == null) return null;
    const link = this.activity.overview()?.linkshells?.find(l => l.id === primaryId);
    return link?.settings ?? null;
  }

  protected primaryLootStructure(): ActivityLootStructure {
    return this.primaryLinkshellSettings()?.lootStructure ?? 'Dkp';
  }

  protected isFeatureEnabled(key: keyof ActivityLinkshellSettings): boolean {
    const settings = this.primaryLinkshellSettings();
    if (!settings) return true;
    const value = settings[key];
    return value !== false;
  }

  protected isDkpModeEnabled(): boolean {
    return this.primaryLootStructure() !== 'LootCouncil';
  }

  // ----- Tab badges -----

  protected openEventsCount(): number {
    return this.liveEvents().length + this.queuedEvents().length;
  }

  protected openTodCount(): number {
    return (this.activity.overview()?.recentTods ?? []).filter(tod => {
      const repop = tod.repopTime ? new Date(tod.repopTime).getTime() : 0;
      return repop > 0 && repop <= Date.now();
    }).length;
  }

  protected liveAuctionCount(): number {
    const auctions = (this.activity.overview() as any)?.auctions ?? [];
    return auctions.filter((a: any) => a?.status === 'Live' || a?.status === 'live').length;
  }

  private liveEvents() {
    return (this.activity.overview()?.activeEvents ?? []).filter(event => Boolean(event.commencementStartTime));
  }

  private queuedEvents() {
    return (this.activity.overview()?.activeEvents ?? []).filter(event => !event.commencementStartTime);
  }

  // ----- ToD delete confirm modal (shared between Dashboard and ToDs tabs;
  // a single instance lives at the parent so the modal is rendered above all
  // tabs.) -----

  // Discord Activities run in a sandboxed iframe without `allow-modals`, so
  // window.confirm() returns false immediately. Use an in-app modal instead.
  protected readonly todDeleteConfirm = signal<{ id: number; name: string } | null>(null);

  protected deleteTod(todId: number, monsterName: string): void {
    this.todDeleteConfirm.set({ id: todId, name: monsterName });
  }

  protected cancelTodDelete(): void {
    this.todDeleteConfirm.set(null);
  }

  protected async confirmTodDelete(): Promise<void> {
    const pending = this.todDeleteConfirm();
    if (!pending) return;
    this.todDeleteConfirm.set(null);
    try {
      await this.activity.deleteTod(pending.id);
    } catch {
      // Service already exposes the action error state.
    }
  }
}
