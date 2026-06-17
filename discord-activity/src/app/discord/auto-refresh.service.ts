import { Injectable, inject, signal } from '@angular/core';

import { AuctionService } from './auction.service';
import { AuthService } from './auth.service';
import { DiscordActivityService } from './discord-activity.service';
import { formatActionError } from './discord-activity.helpers';
import { DkpSheetService } from './dkp-sheet.service';
import { PartySetupService } from './party-setup.service';
import { WindowEventService } from './window-event.service';
import type { TabName } from '../home/activity-home.types';

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

  private readonly NORMAL_MS = 25_000;
  private readonly FAST_MS = 5_000;
  // When the live change-feed is connected the timer is only a safety net, so
  // it ticks slowly; if the feed drops we revert to NORMAL_MS / FAST_MS.
  private readonly SAFETY_MS = 60_000;

  private readonly activeTab = signal<TabName | null>(null);
  // Set by LiveUpdateService so the timer can back off while push is healthy.
  private readonly liveConnected = signal(false);
  // Dedupes overlapping refreshes (timer + manual button + change-feed) into a
  // single in-flight fetch so a click during a tick never double-loads.
  private refreshInFlight: Promise<void> | null = null;
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
      return (this.activity.overview()?.activeEvents ?? []).some(e => !!e.commencementStartTime);
    }
    if (tab === 'window-events') {
      // Open (unposted) events are the live surface — closed events are stable
      // history and don't need fast cadence.
      return (this.windows.data()?.openEvents ?? []).length > 0;
    }
    return false;
  }

  private tick(): Promise<void> {
    // Background tick: no spinner, errors stay silent (next tick retries).
    return this.refreshNow(this.activeTab());
  }

  // The single refresh path used by the timer, the manual button, and the live
  // change-feed. Refreshes the overview AND the active tab's own data, deduping
  // overlapping calls. `showBusy` drives the header spinner + surfaces errors —
  // used only by the manual button so background refreshes stay quiet.
  async refreshNow(tab: TabName | null = this.activeTab(), showBusy = false): Promise<void> {
    if (showBusy) {
      this.activity.busyRefresh.set(true);
      this.auth.setActionError(null);
    }
    try {
      this.refreshInFlight ??= this.doRefresh(tab);
      await this.refreshInFlight;
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
      const linkshellId =
        this.activity.overview()?.primaryLinkshell?.id ??
        this.activity.overview()?.appUser?.primaryLinkshellId ??
        0;
      if (!linkshellId) return;

      // Overview backs the identity bar, sidebar, dashboard, ToDs/events tabs,
      // and a chunk of derived per-tab signals; refreshing it covers all of
      // those. Then load the active tab's own data on top.
      await this.activity.refreshOverview();
      await this.loadForTab(tab, linkshellId);
    } finally {
      this.refreshInFlight = null;
    }
  }

  // Tab-specific loaders that don't live on the overview payload. The other
  // tabs (dashboard/profile/linkshell/tods/dkp/loot) are powered by the
  // overview refresh, so they need nothing extra here.
  private async loadForTab(tab: TabName | null, linkshellId: number): Promise<void> {
    switch (tab) {
      case 'window-events':
        await this.windows.load(linkshellId);
        break;
      case 'party-setup':
        await this.partySetup.loadList(linkshellId);
        break;
      case 'dkp-sheet':
        await this.dkpSheet.load(linkshellId);
        break;
      case 'auctions':
        await this.auction.loadAuctions(linkshellId);
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
