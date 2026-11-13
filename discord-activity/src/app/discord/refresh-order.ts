// Two small helpers that keep the app's background reloads in order. They are plain classes with
// no Angular in them, so the unit tests (refresh-order.spec.ts) can drive them directly.
//
// Why they exist: the refresh timer, the Refresh button, the live change-feed and every write's
// own reload can all fetch the same data at nearly the same moment. Responses do not always come
// back in the order they were asked for, so without these a slow, OLDER reply could land last and
// quietly put old data back on screen (an officer adds loot, then it "vanishes" until the next
// refresh, and they add it again).

/**
 * Numbers each request and says whether its reply may be shown.
 *
 * A reply is shown only when:
 *  - it is newer than the reply already on screen (an older one arriving late is dropped), and
 *  - it is for the same thing the newest request asked for (the `key`, e.g. the linkshell id), so a
 *    reply for the linkshell the user just switched away from is dropped too.
 *
 * Usage:
 *   const ticket = order.begin(linkshellId);
 *   const result = await fetch(...);
 *   if (order.accept(ticket, linkshellId)) data.set(result);
 */
export class ResponseOrder {
  private started = 0;
  private applied = 0;
  private latestKey: string | number = '';

  /** Call right before sending a request. Returns its ticket number. */
  begin(key: string | number = ''): number {
    this.latestKey = key;
    this.started += 1;
    return this.started;
  }

  /** Call when the reply arrives. True means "show it" (and remembers it as the newest shown). */
  accept(ticket: number, key: string | number = ''): boolean {
    if (ticket <= this.applied) return false;
    if (key !== this.latestKey) return false;
    this.applied = ticket;
    return true;
  }

  /**
   * The data was replaced without a fetch (cleared on reconnect, or patched locally after a save),
   * so every request already on the wire is now older than what is on screen and must be dropped.
   */
  invalidate(): void {
    this.applied = this.started;
  }
}

/**
 * Runs an async job so that at most one copy runs at a time, and a request that arrives while one
 * is running is never lost.
 *
 * The running copy may have fetched its data BEFORE the change the new caller wants to see, so
 * joining it would hand that caller stale data. Instead the caller waits for ONE more run that
 * starts after the current one finishes. Any number of callers arriving meanwhile share that single
 * extra run, so a burst of change-feed events costs at most two runs, not one per event.
 */
export class RunOnceMore {
  private running: Promise<void> | null = null;
  private queued: Promise<void> | null = null;

  constructor(private readonly job: () => Promise<void>) {}

  run(): Promise<void> {
    // An extra run is already waiting: it starts after the current one, so it covers this caller.
    if (this.queued) return this.queued;
    if (!this.running) return this.start();

    const ignore = (): void => undefined;
    this.queued = this.running.then(ignore, ignore).then(() => {
      this.queued = null;
      return this.start();
    });
    return this.queued;
  }

  private start(): Promise<void> {
    const current: Promise<void> = this.job().finally(() => {
      if (this.running === current) this.running = null;
    });
    this.running = current;
    return current;
  }
}
