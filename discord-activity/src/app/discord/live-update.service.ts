import { Injectable, inject, signal } from '@angular/core';

import { ActivityHttpClient } from './activity-http.client';
import { AuthService } from './auth.service';
import { AutoRefreshService } from './auto-refresh.service';

interface ChangeFeedResponse {
  version: number;
  areas: string[];
}

// Live change feed for the Discord Activity (long-poll). One self-rescheduling
// loop holds GET /api/activity/changes?linkshellId&since; the server returns as
// soon as the linkshell's version advances (with the coarse areas that changed)
// or after a ~25s hold. On a real change we route through the same
// AutoRefreshService.refreshNow() the timer and manual button use, so every
// surface updates within ~1s of someone else's action instead of waiting for
// the next poll tick.
//
// This is an OPTIMIZATION layered over AutoRefreshService's timer: while the
// feed is healthy it reports connected() so the timer backs off to a slow
// safety-net cadence; if the feed errors we report disconnected and the timer
// resumes its normal 5s/25s polling, so updates never stop — they just slow.
@Injectable({ providedIn: 'root' })
export class LiveUpdateService {
  private readonly http = inject(ActivityHttpClient);
  private readonly auth = inject(AuthService);
  private readonly autoRefresh = inject(AutoRefreshService);

  // Backoff between failed polls. Short at first (transient blip), then long so
  // a feed that's genuinely unavailable (e.g. 403/blocked) doesn't hammer the
  // server — the timer carries correctness in the meantime.
  private readonly ERROR_BACKOFF_MS = 5_000;
  private readonly LONG_BACKOFF_MS = 30_000;

  readonly connected = signal(false);

  private started = false;
  private running = false;
  // Last-seen change version. -1 means "not yet primed" → the first response
  // just establishes the baseline (no refetch, since the page already loaded).
  private since = -1;
  private primed = false;
  private consecutiveErrors = 0;
  // Aborts the in-flight long-poll + any pending backoff when we stop or the
  // app is backgrounded, so we don't hold a connection open uselessly.
  private loopAbort: AbortController | null = null;

  // Called by activity-home on mount; stop() on destroy. Idempotent.
  start(): void {
    if (this.started) {
      return;
    }
    this.started = true;
    document.addEventListener('visibilitychange', this.onVisibility);
    this.kick();
  }

  stop(): void {
    if (!this.started) {
      return;
    }
    this.started = false;
    document.removeEventListener('visibilitychange', this.onVisibility);
    this.stopLoop();
    this.setConnected(false);
  }

  private readonly onVisibility = (): void => {
    if (document.visibilityState === 'visible') {
      // Resume: a fresh loop picks up from our last-seen version, so any change
      // that happened while hidden is caught on the first poll.
      this.kick();
    } else {
      // Pause while hidden (mirrors AutoRefreshService); release the held poll.
      this.stopLoop();
      this.setConnected(false);
    }
  };

  private kick(): void {
    if (!this.started || this.running) {
      return;
    }
    if (document.visibilityState !== 'visible') {
      return;
    }
    const controller = new AbortController();
    this.loopAbort = controller;
    void this.loop(controller.signal);
  }

  private stopLoop(): void {
    this.loopAbort?.abort();
    this.loopAbort = null;
  }

  private async loop(signal: AbortSignal): Promise<void> {
    this.running = true;
    try {
      while (this.started && !signal.aborted && document.visibilityState === 'visible') {
        const linkshellId = this.currentLinkshellId();
        if (!linkshellId) {
          // Overview not loaded yet — wait briefly and re-check (not an error).
          await this.delay(this.ERROR_BACKOFF_MS, signal);
          continue;
        }

        let res: ChangeFeedResponse;
        try {
          res = await this.http.fetchActivityJson<ChangeFeedResponse>(
            `/api/activity/changes?linkshellId=${linkshellId}&since=${this.since}`,
            undefined,
            signal
          );
        } catch {
          if (signal.aborted) {
            return;
          }
          this.consecutiveErrors++;
          this.setConnected(false);
          await this.delay(this.backoffMs(), signal);
          continue;
        }

        if (signal.aborted) {
          return;
        }

        this.consecutiveErrors = 0;
        this.setConnected(true);

        const previousVersion = this.since;
        // Server restarted → its in-memory version reset below ours. Re-baseline
        // and force a refresh so we don't sit on data that changed across the
        // restart window.
        const serverReset = res.version < previousVersion;
        this.since = res.version;

        if (!this.primed) {
          this.primed = true;
          continue;
        }

        if (serverReset || (res.areas?.length ?? 0) > 0) {
          await this.autoRefresh.refreshNow();
        }
      }
    } finally {
      this.running = false;
    }
  }

  private backoffMs(): number {
    return this.consecutiveErrors <= 3 ? this.ERROR_BACKOFF_MS : this.LONG_BACKOFF_MS;
  }

  // Same linkshell-resolution the auto-refresh timer uses.
  private currentLinkshellId(): number {
    const overview = this.auth.overview();
    return overview?.primaryLinkshell?.id ?? overview?.appUser?.primaryLinkshellId ?? 0;
  }

  private setConnected(connected: boolean): void {
    if (this.connected() !== connected) {
      this.connected.set(connected);
      this.autoRefresh.setLiveConnected(connected);
    }
  }

  // Promise timer that also settles immediately if the loop is aborted, so
  // stop()/background takes effect without waiting out the backoff.
  private delay(ms: number, signal: AbortSignal): Promise<void> {
    return new Promise<void>(resolve => {
      if (signal.aborted) {
        resolve();
        return;
      }
      const timer = setTimeout(() => {
        signal.removeEventListener('abort', onAbort);
        resolve();
      }, ms);
      const onAbort = (): void => {
        clearTimeout(timer);
        resolve();
      };
      signal.addEventListener('abort', onAbort, { once: true });
    });
  }
}
