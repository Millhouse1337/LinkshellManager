import { DestroyRef, Injectable, inject, signal } from '@angular/core';

/**
 * One shared "now" for every countdown and "x minutes ago" in the app.
 *
 * Six screens used to run their own 1-second timer, each redrawing its whole template every
 * second, even while the tab or Discord window was hidden. This is the single timer: it ticks
 * once a second while the page is visible, stops while it is hidden, and catches up the moment
 * the page is shown again (so a countdown never shows a stale value after switching back).
 *
 * Read `now()` in a computed or template and it updates by itself.
 */
@Injectable({ providedIn: 'root' })
export class ClockService {
  private readonly nowSignal = signal(Date.now());
  readonly now = this.nowSignal.asReadonly();

  private timer: ReturnType<typeof setInterval> | null = null;

  constructor() {
    const onVisibility = () => (document.hidden ? this.stop() : this.start());
    document.addEventListener('visibilitychange', onVisibility);
    inject(DestroyRef).onDestroy(() => {
      document.removeEventListener('visibilitychange', onVisibility);
      this.stop();
    });
    if (!document.hidden) {
      this.start();
    }
  }

  private start(): void {
    this.nowSignal.set(Date.now());
    if (this.timer === null) {
      this.timer = setInterval(() => this.nowSignal.set(Date.now()), 1000);
    }
  }

  private stop(): void {
    if (this.timer !== null) {
      clearInterval(this.timer);
      this.timer = null;
    }
  }
}
