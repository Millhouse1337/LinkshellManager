import { Injectable, inject, signal } from '@angular/core';

import { AuctionService } from './auction.service';
import { AuthService } from './auth.service';
import { DiscordActivityService } from './discord-activity.service';
import { formatActionError } from './discord-activity.helpers';
import { DkpSheetService } from './dkp-sheet.service';
import { PartySetupService } from './party-setup.service';
import { PendingSubmissionsService } from './pending-submissions.service';
import { RunOnceMore } from './refresh-order';
import { TreasuryService } from './treasury.service';
import { WindowEventService } from './window-event.service';
import type { TabName } from '../home/activity-home.types';
import { canReviewSubmissionsIn } from '../home/activity-home.helpers';

// Centralized visibility-aware polling that keeps every tab "feeling live"
// without per-feature plumbing. One self-rescheduling timer:
//
//   * Pauses entirely when `document.visibilityState === 'hidden'` (Discord
//     iframe loses focus, browser tab is backgrounded, voice-channel switch).
//   * Resumes immediately on `visible` AND fires a catch-up tick so the user
//     sees fresh data right when they look at the app.
//   * Adjusts cadence per tick: 5s when the active tab shows live activity
//     (open window event, started auction, commenced timed event), 25s
//     otherwise. Recomputed on every tick via setTimeout self-reschedule so
//     a live event starting mid-session promotes the cadence within one tick.
//
// Loaders are dispatched from a single switch on `activeTab` to avoid a
// service-of-services registration dance. Refreshing the overview every tick
// covers the dashboard + identity bar + sidebar + the dozen surfaces that
// derive from it without needing per-tab loaders.
@Injectable({ providedIn: 'root' })
export class AutoRefreshService {
  private readonly activity = inject(DiscordActivityService);
  private readonly auth = inject(AuthService);
  private readonly windows = inject(WindowEventService);
  private readonly partySetup = inject(PartySetupService);
  private readonly dkpSheet = inject(DkpSheetService);
  private readonly auction = inject(AuctionService);
  private readonly treasury = inject(TreasuryService);
  private readonly pendingSubmissions = inject(PendingSubmissionsService);

  private readonly NORMAL_MS = 25_000;
  private readonly FAST_MS = 5_000;
  // When the live change-feed is connected the timer is only a safety net, so
  // it ticks slowly; if the feed drops we revert to NORMAL_MS / FAST_MS.
  private readonly SAFETY_MS = 60_000;

  private readonly activeTab = signal<TabName | null>(null);
  // Set by LiveUpdateService so the timer can back off while push is healthy.
  private readonly liveConnected = signal(false);
  // Bumps after every refresh (timer / manual button / live change-feed). Components
  // that own data NOT on the overview (e.g. the per-event party board detail) can read
  // this in an effect to reload themselves whenever the app refreshes.
  readonly refreshSignal = signal(0);
  // Overlapping refreshes (timer + manual button + change-feed) never run side by side. A request
  // that arrives mid-refresh does NOT join it: that refresh may have fetched before the change the
  // caller wants to see. It waits for one more refresh after the current one instead, and every
  // request arriving in the meantime shares that same extra run.
  private readonly refreshRunner = new RunOnceMore(() => this.doRefresh(this.nextRefreshTab));
  // The tab the next refresh loads, so the extra run uses the newest caller's tab (the user may
  // have switched tabs while the previous refresh was running).
  private nextRefreshTab: TabName | null = null;
  private timerId: ReturnType<typeof setTimeout> | null = null;
  private started = false;

  setLiveConnected(connected: boolean): void {
    this.liveConnected.set(connected);
  }

  // Activity-home calls this once on mount + stop() on destroy. Idempotent so
  // hot-reload during dev doesn't stack listeners.
  start(): void {
    if (this.started) return;
    this.started = true;
    document.addEventListener('visibilitychange', this.onVisibility);
    this.schedule();
  }

  stop(): void {
    if (!this.started) return;
    this.started = false;
    document.removeEventListener('visibilitychange', this.onVisibility);
    this.clearTimer();
  }

  // Called by activity-home whenever the user switches tabs. Triggers an
  // immediate refresh of the new tab so the user doesn't sit on stale data
  // for up to a full interval after switching.
  setActiveTab(tab: TabName): void {
    this.activeTab.set(tab);
    if (document.visibilityState === 'visible') {
      void this.tick();
      this.schedule();
    }
  }

  private readonly onVisibility = (): void => {
    if (document.visibilityState === 'visible') {
      void this.tick();
      this.schedule();
    } else {
      this.clearTimer();
    }
  };

  // setTimeout self-reschedule (not setInterval) so the next tick always
  // reads the *current* cadence — a window event opening mid-session bumps
  // the next tick down from 25s to 5s on the very next iteration.
  private schedule(): void {
    this.clearTimer();
    if (document.visibilityState !== 'visible') return;
    this.timerId = setTimeout(() => {
      void this.tick();
      this.schedule();
    }, this.intervalForCurrentState());
  }

  private intervalForCurrentState(): number {
    // Live feed healthy → the timer is just a correctness backstop.
    if (this.liveConnected()) return this.SAFETY_MS;
    return this.shouldUseFast() ? this.FAST_MS : this.NORMAL_MS;
  }

  // Fast-poll triggers — kept narrow on purpose. Polling everything at 5s
  // would be wasteful; only surfaces where a 25s lag actively breaks UX.
  private shouldUseFast(): boolean {
    const tab = this.activeTab();
    if (tab === 'auctions') {
      return this.activity.auctions().some(a => (a.status ?? '').toLowerCase() === 'live');
    }
    if (tab === 'timed-events') {
      // A commenced camp is a ticking clock, so it earns the fast cadence. An OPEN attendance event
      // deliberately does NOT: it's a pending-review queue that sits open for hours or days until an
      // officer posts it, so counting it here would pin the app's busiest tab at 5s permanently for
      // any linkshell with one lingering unposted roster. New snapshots still arrive via the change
      // feed within about a second either way.
      return (this.activity.overview()?.activeEvents ?? []).some(e => !!e.commencementStartTime);
    }
    return false;
  }

  private tick(): Promise<void> {
    // Background tick: no spinner, errors stay silent (next tick retries).
    return this.refreshNow(this.activeTab());
  }

  // The single refresh path used by the timer, the manual button, and the live
  // change-feed. Refreshes the overview AND the active tab's own data, folding
  // overlapping calls together. `showBusy` drives the header spinner + surfaces errors —
  // used only by the manual button so background refreshes stay quiet.
  async refreshNow(tab: TabName | null = this.activeTab(), showBusy = false): Promise<void> {
    if (showBusy) {
      this.activity.busyRefresh.set(true);
      this.auth.setActionError(null);
      this.auth.clearAllLoadErrors();
    }
    try {
      this.nextRefreshTab = tab;
      await this.refreshRunner.run();
      // Manual (button) refresh only — confirm it visibly via the success banner so
      // the user can see the page actually reloaded. The silent background timer
      // (showBusy=false) never flashes this.
      if (showBusy) {
        this.auth.setActionMessage('Page refreshed.');
      }
    } catch (error) {
      if (showBusy) {
        this.auth.setActionError(formatActionError(error, 'Refresh failed — try again.'));
      }
    } finally {
      if (showBusy) this.activity.busyRefresh.set(false);
    }
  }

  private async doRefresh(tab: TabName | null): Promise<void> {
    try {
      // No overview at all means start-up failed (the error banner is showing and its Reconnect
      // button reloads everything), so there is nothing for the timer to refresh.
      if (!this.activity.overview()) return;

      // Overview backs the identity bar, sidebar, dashboard, ToDs/events tabs,
      // and a chunk of derived per-tab signals; refreshing it covers all of
      // those. Always refresh it, even with no linkshell yet: a new member on
      // the Onboarding screen is waiting for exactly this (an invite an officer
      // just sent, an approved join request) to show up.
      await this.activity.refreshOverview();

      // Read the linkshell from the FRESH overview (it may have just changed),
      // then load the active tab's own data on top. Those loaders are all
      // per-linkshell, so they are the only part skipped without one.
      const linkshellId = this.activity.currentLinkshellId();
      if (!linkshellId) return;
      await this.loadForTab(tab, linkshellId);
    } finally {
      // Let board-detail panels (and anything else off the overview) reload.
      this.refreshSignal.update(v => v + 1);
    }
  }

  // Tab-specific loaders that don't live on the overview payload. The other
  // tabs (dashboard/profile/linkshell/tods/dkp/loot) are powered by the
  // overview refresh, so they need nothing extra here.
  private async loadForTab(tab: TabName | null, linkshellId: number): Promise<void> {
    switch (tab) {
      case 'timed-events':
        // Camps ride the overview refresh above, but the attendance sections embedded in this tab
        // have their own endpoint.
        await this.windows.loadIfStale(linkshellId);
        break;
      case 'party-setup':
        await this.partySetup.loadList(linkshellId);
        break;
      case 'tods':
        // The ToDs ride the overview, but two things on this tab do not: the party setup list (a
        // ToD row whose monster has a setup offers the sign-up panel) and the ToD Submissions
        // Needing Approval section, which has its own endpoint that only reviewers may read. The
        // tab itself only loads them when the linkshell changes, so the timer keeps them fresh.
        await this.partySetup.loadList(linkshellId);
        if (canReviewSubmissionsIn(this.activity.overview(), linkshellId)) {
          await this.pendingSubmissions.load(linkshellId);
        }
        break;
      case 'dkp-sheet':
        await this.dkpSheet.load(linkshellId);
        break;
      case 'auctions':
        await this.auction.loadAuctions(linkshellId);
        break;
      case 'treasury':
        // Gil reads from its own paged endpoint, not the overview payload, so it has to be refreshed
        // explicitly. Items still ride the overview. `reload` repeats whatever page, search and filter
        // the officer is currently looking at rather than snapping them back to page one.
        await this.treasury.reload();
        break;
      default:
        break;
    }
  }

  private clearTimer(): void {
    if (this.timerId !== null) {
      clearTimeout(this.timerId);
      this.timerId = null;
    }
  }
}
