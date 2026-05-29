import { Injectable, inject, signal } from '@angular/core';

import { AuctionService } from './auction.service';
import { DiscordActivityService } from './discord-activity.service';
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
  private readonly windows = inject(WindowEventService);
  private readonly partySetup = inject(PartySetupService);
  private readonly dkpSheet = inject(DkpSheetService);
  private readonly auction = inject(AuctionService);

  private readonly NORMAL_MS = 25_000;
  private readonly FAST_MS = 5_000;

  private readonly activeTab = signal<TabName | null>(null);
  private timerId: ReturnType<typeof setTimeout> | null = null;
  private started = false;

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

  private async tick(): Promise<void> {
    const linkshellId =
      this.activity.overview()?.primaryLinkshell?.id ??
      this.activity.overview()?.appUser?.primaryLinkshellId ??
      0;
    if (!linkshellId) return;

    // Overview backs the identity bar, sidebar, dashboard, ToDs tab, events
    // tab, and a chunk of derived per-tab signals — refreshing it once per
    // tick covers all of those without per-tab plumbing.
    void this.activity.refreshOverview();

    // Tab-specific loaders that don't live on the overview payload. Skipped
    // intentionally for dashboard/profile/linkshell/tods/dkp/loot/etc. —
    // those are already powered by the overview refresh above.
    switch (this.activeTab()) {
      case 'window-events':
        void this.windows.load(linkshellId);
        break;
      case 'party-setup':
        void this.partySetup.loadList(linkshellId);
        break;
      case 'dkp-sheet':
        void this.dkpSheet.load(linkshellId);
        break;
      case 'auctions':
        void this.auction.loadAuctions(linkshellId);
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
