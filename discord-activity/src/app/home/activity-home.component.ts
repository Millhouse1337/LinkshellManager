import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, effect, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import {
  ActivityLinkshellSettings,
  ActivityLootStructure,
  DiscordActivityService
} from '../discord/discord-activity.service';
import { ActivitySidebarPanelComponent } from './activity-sidebar-panel.component';
import { TAB_NAMES, type TabName } from './activity-home.types';
import { ConfigurationsTabComponent } from './tabs/configurations-tab.component';
import { DashboardTabComponent } from './tabs/dashboard-tab.component';
import { EventsTabComponent } from './tabs/events-tab.component';
import { LinkshellTabComponent } from './tabs/linkshell-tab.component';
import { TodsTabComponent } from './tabs/tods-tab.component';
import { WindowEventsTabComponent } from './tabs/window-events-tab.component';
import { LootHistoryPanelComponent } from './sidebar-panels/loot-history-panel.component';

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
    LootHistoryPanelComponent,
    TodsTabComponent,
    WindowEventsTabComponent
  ],
  templateUrl: './activity-home.component.html',
  styleUrl: './activity-home.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ActivityHomeComponent {
  protected readonly activity = inject(DiscordActivityService);
  protected readonly activeTab = signal<TabName>('dashboard');
  protected readonly reconnecting = signal(false);

  // Wall-clock display for the identity bar. We tick once per minute since the
  // header only shows hours/minutes; the initial timeout aligns the next tick
  // with the wall-clock minute boundary so the display flips on time.
  // The formatters are rebuilt whenever the configured profile zone changes so
  // the header always matches what's saved on the profile.
  private timeFmt: Intl.DateTimeFormat = new Intl.DateTimeFormat([], { hour: 'numeric', minute: '2-digit' });
  private zoneFmt: Intl.DateTimeFormat = new Intl.DateTimeFormat([], { timeZoneName: 'short' });
  private activeClockZone: string | null = null;
  protected readonly currentTime = signal<string>('');
  protected readonly currentZone = signal<string>('');

  constructor() {
    const destroyRef = inject(DestroyRef);

    const profileZone = (): string => {
      const fromOverview = this.activity.overview()?.appUser?.timeZone;
      const fromLocal = this.activity.localUser()?.appUser?.timeZone;
      return (fromOverview ?? fromLocal ?? '').trim();
    };

    const rebuildFormatters = (zone: string): void => {
      const options: Intl.DateTimeFormatOptions = { hour: 'numeric', minute: '2-digit' };
      const zoneOptions: Intl.DateTimeFormatOptions = { timeZoneName: 'short' };
      if (zone) {
        options.timeZone = zone;
        zoneOptions.timeZone = zone;
      }
      try {
        this.timeFmt = new Intl.DateTimeFormat([], options);
        this.zoneFmt = new Intl.DateTimeFormat([], zoneOptions);
        this.activeClockZone = zone;
      } catch {
        // A stale/invalid stored zone shouldn't break the header — keep the
        // last working formatter and mark the active zone empty so we retry
        // next tick if a valid zone arrives.
        this.activeClockZone = '';
      }
    };

    const tick = (): void => {
      const desired = profileZone();
      if (desired !== this.activeClockZone) {
        rebuildFormatters(desired);
      }
      const now = new Date();
      this.currentTime.set(this.timeFmt.format(now));
      const zonePart = this.zoneFmt.formatToParts(now).find(p => p.type === 'timeZoneName');
      this.currentZone.set(zonePart?.value ?? '');
    };

    rebuildFormatters(profileZone());
    tick();
    const msToNextMinute = 60000 - (Date.now() % 60000);
    let intervalId: ReturnType<typeof setInterval> | null = null;
    const timeoutId = setTimeout(() => {
      tick();
      intervalId = setInterval(tick, 60000);
    }, msToNextMinute);

    // Re-tick immediately whenever the saved profile zone changes (e.g. the
    // overview arrives after first paint, or the user updates their profile)
    // so we don't sit on a stale zone for up to a minute.
    effect(() => {
      this.activity.overview()?.appUser?.timeZone;
      this.activity.localUser()?.appUser?.timeZone;
      tick();
    });

    // Once the overview lands, ask the service to auto-detect-and-save the
    // browser zone if the saved value is still on its server default. The
    // service is idempotent (autoDetectAttempted guard) so re-firing is safe.
    effect(() => {
      const overview = this.activity.overview();
      if (overview?.appUser) {
        void this.activity.detectAndSaveTimeZoneIfUnset();
      }
    });

    destroyRef.onDestroy(() => {
      clearTimeout(timeoutId);
      if (intervalId !== null) clearInterval(intervalId);
    });
  }

  protected async reconnect(): Promise<void> {
    if (this.reconnecting()) return;
    this.reconnecting.set(true);
    try {
      await this.activity.reconnect();
    } finally {
      this.reconnecting.set(false);
    }
  }

  protected setActiveTab(tab: TabName): void {
    this.activeTab.set(tab);
    window.scrollTo({ top: 0, behavior: 'smooth' });
  }

  // True once the first overview load has resolved. We use it to keep the tab
  // strip empty during the very first render so feature-gated tabs (Auctions,
  // DKP, etc.) don't flash visible before settings arrive.
  protected hasOverview(): boolean {
    return this.activity.overview() != null;
  }

  // Keyboard tablist semantics: Left/Right cycle through visible tabs, Home/End
  // jump to the ends. We compute the visible list at click-time so the order
  // matches what's actually rendered.
  protected onTabKeydown(event: KeyboardEvent): void {
    const key = event.key;
    if (key !== 'ArrowLeft' && key !== 'ArrowRight' && key !== 'Home' && key !== 'End') {
      return;
    }

    const tabStrip = (event.currentTarget as HTMLElement) ?? null;
    if (!tabStrip) return;

    const visibleTabs = Array.from(
      tabStrip.querySelectorAll<HTMLButtonElement>('button.tab[role="tab"]')
    );
    if (visibleTabs.length === 0) return;

    const activeEl = document.activeElement as HTMLButtonElement | null;
    const currentIndex = activeEl ? visibleTabs.indexOf(activeEl) : -1;
    let nextIndex = currentIndex;

    if (key === 'ArrowLeft') {
      nextIndex = currentIndex <= 0 ? visibleTabs.length - 1 : currentIndex - 1;
    } else if (key === 'ArrowRight') {
      nextIndex = currentIndex < 0 || currentIndex >= visibleTabs.length - 1 ? 0 : currentIndex + 1;
    } else if (key === 'Home') {
      nextIndex = 0;
    } else if (key === 'End') {
      nextIndex = visibleTabs.length - 1;
    }

    const next = visibleTabs[nextIndex];
    if (!next) return;

    event.preventDefault();
    next.focus();
    next.click();
  }

  protected tabId(tab: TabName): string {
    return `activity-tab-${tab}`;
  }

  protected panelId(tab: TabName): string {
    return `activity-tabpanel-${tab}`;
  }

  protected readonly TAB_NAMES = TAB_NAMES;

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

  // Character names of every member on the primary linkshell, sorted for
  // the Loot History edit modal's winner dropdown. Null/blank names are
  // filtered so the dropdown never shows empty rows.
  protected primaryLinkshellRosterNames(): string[] {
    const members = this.primaryLinkshell()?.members ?? [];
    return members
      .map(m => m.characterName)
      .filter((name): name is string => !!name && name.trim().length > 0)
      .sort((a, b) => a.localeCompare(b));
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
    return this.activity.auctions().filter(auction => {
      const status = (auction.status ?? '').toLowerCase();
      return status === 'live';
    }).length;
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
