import { CommonModule } from '@angular/common';
import { ChangeDetectionStrategy, Component, DestroyRef, computed, effect, inject, signal, untracked } from '@angular/core';
import { toSignal } from '@angular/core/rxjs-interop';
import { FormsModule } from '@angular/forms';
import { ActivatedRoute, Router } from '@angular/router';
import { map } from 'rxjs';
import { AutoRefreshService } from '../discord/auto-refresh.service';
import { ClaimService } from '../discord/claim.service';
import type { ActivityClaimCandidate } from '../discord/discord-activity.types';
import { LiveUpdateService } from '../discord/live-update.service';
import {
  ActivityLinkshellSettings,
  ActivityLootStructure,
  DiscordActivityService
} from '../discord/discord-activity.service';
import { ActivitySidebarPanelComponent } from './activity-sidebar-panel.component';
import { TAB_NAMES, type ManagementView, type ProfileView, type TabName, type TreasuryView } from './activity-home.types';
import { ADMIN_BADGE, primaryLinkshellSettings, rankIcon } from './activity-home.helpers';
import { slugForTab, tabFromSlug } from './tab-routes';

type DkpView = 'history' | 'sheet' | 'grouping' | 'import';
import { DataAdminTabComponent } from './data-admin/data-admin-tab.component';
import { AdminSettingsTabComponent } from './tabs/admin-settings-tab.component';
import { ChartsTabComponent, chartsSectionFromSlug } from './tabs/charts-tab.component';
import { ConfigurationsTabComponent } from './tabs/configurations-tab.component';
import { DashboardTabComponent } from './tabs/dashboard-tab.component';
import { DkpGroupingTabComponent } from './tabs/dkp-grouping-tab.component';
import { DkpImportCardComponent } from './tabs/dkp-import-card.component';
import { DkpSheetTabComponent } from './tabs/dkp-sheet-tab.component';
import { EventsTabComponent } from './tabs/events-tab.component';
import { LinkshellTabComponent } from './tabs/linkshell-tab.component';
import { TreasuryTabComponent } from './tabs/treasury-tab.component';
import { PartySetupTabComponent } from './tabs/party-setup-tab.component';
import { TodsTabComponent } from './tabs/tods-tab.component';
import { LootHistoryPanelComponent } from './sidebar-panels/loot-history-panel.component';
import type { AuctionsView } from './sidebar-panels/auctions-panel.component';

@Component({
  selector: 'app-activity-home',
  imports: [
    CommonModule,
    FormsModule,
    ActivitySidebarPanelComponent,
    AdminSettingsTabComponent,
    ChartsTabComponent,
    ConfigurationsTabComponent,
    DataAdminTabComponent,
    DashboardTabComponent,
    DkpGroupingTabComponent,
    DkpImportCardComponent,
    DkpSheetTabComponent,
    EventsTabComponent,
    LinkshellTabComponent,
    TreasuryTabComponent,
    LootHistoryPanelComponent,
    PartySetupTabComponent,
    TodsTabComponent
  ],
  templateUrl: './activity-home.component.html',
  styleUrl: './activity-home.component.scss',
  changeDetection: ChangeDetectionStrategy.OnPush
})
export class ActivityHomeComponent {
  protected readonly activity = inject(DiscordActivityService);
  protected readonly claim = inject(ClaimService);
  private readonly autoRefresh = inject(AutoRefreshService);
  private readonly liveUpdate = inject(LiveUpdateService);
  private readonly router = inject(Router);

  // Where you are, as URL-style parts: ['events'], ['dkp', 'sheet'] (tab-routes.ts). On the
  // website that is the URL itself. Inside Discord the iframe's URL is never touched (see
  // setActiveTab), so the same parts live in memory there. No parts, or a name nobody knows,
  // is the dashboard.
  private readonly route = inject(ActivatedRoute);
  private readonly urlSegments = toSignal(this.route.url, { requireSync: true });
  private readonly discordLocation = signal<readonly string[]>([]);
  private readonly location = computed<readonly string[]>(() =>
    this.activity.isWebsite ? this.urlSegments().map(segment => segment.path) : this.discordLocation());
  protected readonly activeTab = computed<TabName>(() => {
    const tab = tabFromSlug(this.location()[0]) ?? 'dashboard';
    return this.canOpenTab(tab) ? tab : 'dashboard';
  });
  private readonly subView = computed(() => this.location()[1] ?? null);

  // The sub-views other tabs keep in the location too: /events/pending, /events/past,
  // /auctions/pending, /auctions/history,
  // /profile/crafts, /treasury/items, /charts/sea. Each tab two-way binds its own view, so a click there
  // updates the URL.
  protected readonly eventsView = computed(() => {
    const view = this.subView();
    return view === 'pending' || view === 'past' ? view : 'current';
  });
  protected readonly auctionsView = computed<AuctionsView>(() => {
    const view = this.subView();
    return view === 'pending' || view === 'history' ? view : 'active';
  });
  protected readonly profileViews = [
    { key: 'jobs' as const, label: 'Jobs' },
    { key: 'crafts' as const, label: 'Crafts' },
    { key: 'ratings' as const, label: 'Player Ratings' },
  ];
  protected readonly profileView = computed<ProfileView>(() => {
    // No linkshell yet: Jobs, Crafts and Player Ratings all need one, so the tab is Onboarding,
    // which is where a new member's character name and time zone go. (Only once the overview has
    // loaded, so a slow load doesn't flash Onboarding at someone who has linkshells.)
    if (this.activity.overview()?.linkshells?.length === 0) return 'onboarding';
    const view = this.subView();
    return view === 'crafts' || view === 'ratings' || view === 'onboarding' ? view : 'jobs';
  });
  protected readonly treasuryView = computed<TreasuryView>(() => {
    const view = this.activeTab() === 'treasury' ? this.subView() : null;
    return view === 'items' || view === 'transactions' ? view : 'balance';
  });
  protected readonly managementView = computed<ManagementView>(() =>
    (this.activeTab() === 'linkshell' && this.subView() === 'invites' ? 'invites' : 'roster'));
  protected readonly chartsView = computed(() => chartsSectionFromSlug(this.subView()));
  // Data Admin keeps a whole path below its tab: /data-admin/rule/5/edit.
  protected readonly dataAdminPath = computed(() => (this.activeTab() === 'data-admin' ? this.location().slice(1) : []));

  // ?ls=<id> (old website links that named a linkshell) — handled once, see the constructor.
  private readonly requestedLinkshellId = toSignal(
    this.route.queryParamMap.pipe(map(params => Number(params.get('ls')) || null)),
    { requireSync: true });

  protected readonly reconnecting = signal(false);
  protected readonly signingOut = signal(false);

  // Which of the DKP tab's views is showing. They share one space and one strip of sub-tabs
  // rather than being top-level tabs of their own: all are the same DKP economy at different zoom
  // levels, and splitting them across the main nav would put the linkshell-wide total, and the
  // rules that produced it, a full tab away from the member row you were just reading. The Sheet,
  // first in the strip, is what the tab opens on (bare /dkp); History is /dkp/history. (/dkp/sheet
  // still reads as the Sheet, for links written when History was the default.)
  //
  // Import DKP (/dkp/import) is the fourth, for whoever may customize the linkshell, since the
  // import rewrites balances. Configurations used to carry it, website only; the upload now goes
  // as base64 JSON and its template download through a one-time link, so Discord has it too.
  protected readonly dkpViews = computed(() => [
    { key: 'sheet' as const, label: 'DKP Sheet' },
    { key: 'history' as const, label: 'DKP History' },
    { key: 'grouping' as const, label: 'DKP Grouping' },
    ...(this.canImportDkp() ? [{ key: 'import' as const, label: 'Import DKP' }] : []),
  ]);
  protected readonly dkpView = computed<DkpView>(() => {
    const view = this.activeTab() === 'dkp' ? this.subView() : null;
    if (view === 'import') return this.canImportDkp() ? view : 'sheet';
    return view === 'history' || view === 'grouping' ? view : 'sheet';
  });

  // What the Refresh button and the refresh timer reload on top of the overview
  // (AutoRefreshService.loadForTab). The DKP Sheet is a view inside the DKP tab but reads its own
  // endpoint, so while it's showing it refreshes under its old 'dkp-sheet' key; it no longer has
  // a Refresh button of its own.
  private readonly refreshTarget = computed<TabName>(() =>
    this.activeTab() === 'dkp' && this.dkpView() === 'sheet' ? 'dkp-sheet' : this.activeTab());

  // The linkshell the import writes to: the one the top bar has selected, as Configurations did.
  protected readonly dkpImportLinkshellId = computed(() => {
    const overview = this.activity.overview();
    return overview?.appUser?.primaryLinkshellId ?? overview?.primaryLinkshell?.id ?? overview?.linkshells?.[0]?.id ?? 0;
  });
  private readonly canImportDkp = computed(() => {
    const id = this.dkpImportLinkshellId();
    const link = (this.activity.overview()?.linkshells ?? []).find(l => l.id === id);
    return !!link?.permissions?.canCustomizeLinkshell;
  });

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
      // Touch these signals so the effect re-runs whenever the saved zone
      // changes (reading a signal registers it as a dependency).
      this.activity.overview();
      this.activity.localUser();
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

    // Once signed in, look for imported DKP waiting to be claimed (a placeholder
    // matching this user's character name). The service guards against re-fetch.
    effect(() => {
      const overview = this.activity.overview();
      if (overview?.appUser) {
        void this.claim.load();
      }
    });

    // Central polling — keeps every tab's data fresh without per-feature
    // plumbing. Cadence flips to 5s on live surfaces (open window event,
    // started auction, commenced timed event), 25s otherwise; pauses while
    // the iframe is hidden.
    let refreshTab = this.refreshTarget();
    this.autoRefresh.setActiveTab(refreshTab);
    this.autoRefresh.start();

    // Follow every tab change, whether a click, Back/Forward or a typed URL. Skips the
    // tab already handed over above so startup doesn't fetch twice.
    effect(() => {
      const tab = this.refreshTarget();
      if (tab !== refreshTab) {
        refreshTab = tab;
        untracked(() => this.autoRefresh.setActiveTab(tab));
      }
    });

    // Website only: ?ls=<id> makes that linkshell the active one (when the user belongs to it),
    // then leaves the URL, so a reload can't switch back after they pick another linkshell.
    effect(() => {
      const requested = this.requestedLinkshellId();
      const overview = this.activity.overview();
      if (!requested || !overview || !this.activity.isWebsite) {
        return;
      }
      untracked(() => {
        if (overview.linkshells.some(ls => ls.id === requested)
            && overview.appUser?.primaryLinkshellId !== requested) {
          void this.activity.setPrimaryLinkshell(requested);
        }
        void this.router.navigate([], { queryParams: { ls: null }, queryParamsHandling: 'merge', replaceUrl: true });
      });
    });

    // Live change feed (long-poll). Primary path for freshness; when it's
    // connected the auto-refresh timer relaxes to a safety-net cadence, and if
    // it drops the timer takes back over — so updates never stop.
    this.liveUpdate.start();

    destroyRef.onDestroy(() => {
      clearTimeout(timeoutId);
      if (intervalId !== null) clearInterval(intervalId);
      this.autoRefresh.stop();
      this.liveUpdate.stop();
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

  // Website only. On success the page leaves for the landing page, so the flag never resets.
  protected async signOut(): Promise<void> {
    if (this.signingOut()) return;
    this.signingOut.set(true);
    if (!(await this.activity.signOut())) {
      this.signingOut.set(false);
    }
  }

  // Changing tab changes the location; activeTab follows it. On the website that is the URL,
  // one history entry per switch so Back works. Inside Discord the iframe's URL is left alone,
  // as it always was: a changing iframe URL interacts badly with the Activity SDK's URL mapping
  // (see charts-tab), and there is no address bar or Back button there anyway.
  protected setActiveTab(tab: TabName, subView?: string | readonly string[]): void {
    const parts = [slugForTab(tab), ...(typeof subView === 'string' ? [subView] : subView ?? [])]
      .filter((part): part is string => !!part);
    if (tab !== this.activeTab()) {
      window.scrollTo({ top: 0, behavior: 'smooth' });
    }
    if (this.activity.isWebsite) {
      void this.router.navigate(['/', ...parts]);
    } else {
      this.discordLocation.set(parts);
    }
  }

  // The website-only tabs: never inside Discord, and only for the accounts they are for. A URL
  // naming one you can't open shows the dashboard instead. Every other tab is always openable.
  protected canOpenTab(tab: TabName): boolean {
    switch (tab) {
      case 'admin':
      case 'data-admin':
        return this.activity.isWebsite && !!this.activity.webSession()?.isSuperAdmin;
      default:
        return true;
    }
  }

  // A sub-view's default (DKP Sheet, Current events, Active auctions, the Sky board) is the
  // bare tab URL; anything else is a second segment.
  protected setDkpView(view: DkpView): void {
    this.setActiveTab('dkp', view === 'sheet' ? undefined : view);
  }

  protected setEventsView(view: 'current' | 'pending' | 'past'): void {
    this.setActiveTab('timed-events', view === 'current' ? undefined : view);
  }

  protected setAuctionsView(view: AuctionsView): void {
    this.setActiveTab('auctions', view === 'active' ? undefined : view);
  }

  protected setTreasuryView(view: TreasuryView): void {
    this.setActiveTab('treasury', view === 'balance' ? undefined : view);
  }

  protected setManagementView(view: ManagementView): void {
    this.setActiveTab('linkshell', view === 'roster' ? undefined : view);
  }

  protected setProfileView(view: ProfileView): void {
    this.setActiveTab('profile', view === 'jobs' ? undefined : view);
  }

  // Every Data Admin step is a new page (list → row → edit), so it starts at the top.
  protected openDataAdmin(path: readonly string[]): void {
    window.scrollTo({ top: 0 });
    // Back to the diagram from a table's records (the Back button or the Data Admin link): name
    // that table in the address as #table=<slug>, which the diagram reads to select it again.
    // Data Admin is website-only, so this is always a real URL.
    const leavingSlug = this.dataAdminPath()[0];
    if (path.length === 0 && leavingSlug && this.activity.isWebsite) {
      void this.router.navigate(['/', slugForTab('data-admin')], { fragment: `table=${leavingSlug}` });
      return;
    }
    this.setActiveTab('data-admin', path);
  }

  protected setChartsView(section: string): void {
    const slug = section.toLowerCase();
    this.setActiveTab('endgame', slug === chartsSectionFromSlug(null).toLowerCase() ? undefined : slug);
  }

  // Manual refresh: refreshes the overview AND the active tab's own data through
  // the single guarded path (spinner + error surface + in-flight dedupe), so it
  // can't jam and always refreshes what you're looking at.
  protected onRefreshClick(): void {
    void this.autoRefresh.refreshNow(this.refreshTarget(), true);
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

  protected readonly rankIcon = rankIcon;
  protected readonly ADMIN_BADGE = ADMIN_BADGE;

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

  // Every linkshell the member belongs to — drives the top-bar switcher.
  protected switchableLinkshells() {
    return this.activity.overview()?.linkshells ?? [];
  }

  // Switch the active (primary) linkshell from the top bar. setPrimaryLinkshell
  // persists it + refreshes the overview, so every tab follows the new primary.
  protected onSwitchLinkshell(value: string): void {
    const id = Number(value);
    if (!id || id === this.primaryLinkshell()?.id) { return; }
    void this.activity.setPrimaryLinkshell(id);
  }

  // "Claim your DKP" prompt: imported placeholders that match this user's name.
  protected claimCandidates(): ActivityClaimCandidate[] {
    return this.claim.dismissed() ? [] : this.claim.candidates();
  }

  protected onClaim(candidate: ActivityClaimCandidate): void {
    void this.claim.claim(candidate);
  }

  protected dismissClaim(): void {
    this.claim.dismiss();
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
    return primaryLinkshellSettings(this.activity.overview());
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
