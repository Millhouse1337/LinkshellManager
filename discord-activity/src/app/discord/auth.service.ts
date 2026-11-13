import { Injectable, computed, signal } from '@angular/core';

import type {
  ActivityOverview,
  DiscordSession,
  LocalActivityUser,
  WebSession
} from './discord-activity.types';
import { currentLinkshellIdOf, formatActionError } from './discord-activity.helpers';
import { readJsonResponse } from './http-response';
import { ResponseOrder } from './refresh-order';

/**
 * Shared auth + top-level state for the Activity facade.
 *
 * Holds session/local-user/overview signals plus the action error/message
 * channel that every per-domain service writes to. Per-domain services
 * inject this and call `currentAccessToken()` / `setActionError()` /
 * `setActionMessage()` instead of reaching back into DiscordActivityService
 * (which would cause a circular DI).
 *
 * `refreshOverview()` is owned here because it's invoked by ~all domains
 * after a mutation. It reads the access token from `session()` and re-fetches
 * the overview JSON; the fetch helper is inlined so this service has no
 * dependency on ActivityHttpClient (which would otherwise create a cycle).
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  readonly session = signal<DiscordSession | null>(null);
  readonly localUser = signal<LocalActivityUser | null>(null);
  readonly overview = signal<ActivityOverview | null>(null);
  readonly actionMessage = signal<string | null>(null);
  readonly actionError = signal<string | null>(null);

  // Discord guild (server) the Activity is currently launched in. Captured by
  // DiscordActivityService from the SDK and sent on every Activity request via
  // the X-Discord-Guild-Id header so the server can enforce per-linkshell guild
  // locks. Held here (not on DiscordActivityService) so ActivityHttpClient can
  // read it without a circular dependency. Null on the web (non-Discord) host.
  readonly discordGuildId = signal<string | null>(null);
  readonly discordGuildName = signal<string | null>(null);

  // Website (browser) host only: the cookie session from GET /api/web/session. Stays null
  // inside Discord. signedIn=false swaps the whole app for the sign-in screen (app.html).
  readonly webSession = signal<WebSession | null>(null);

  currentAccessToken(): string | undefined {
    return this.session()?.access_token;
  }

  // On the website a 401 means the cookie session ended (expired, or signed out in another
  // tab) — every Activity API 401 is "not signed in". Inside Discord this is a no-op.
  noteUnauthorized(): void {
    const session = this.webSession();
    if (session?.signedIn) {
      this.webSession.set({ ...session, signedIn: false });
    }
  }

  // Header sent on every Activity request to identify the launching guild.
  guildHeaders(): Record<string, string> {
    const guildId = this.discordGuildId();
    return guildId ? { 'X-Discord-Guild-Id': guildId } : {};
  }

  setActionError(message: string | null): void {
    this.actionError.set(message);
  }

  setActionMessage(message: string | null): void {
    this.actionMessage.set(message);
  }

  clearActionState(): void {
    this.actionError.set(null);
    this.actionMessage.set(null);
  }

  // ----- Background load errors -----
  //
  // Kept apart from actionError on purpose. actionError belongs to what the user just DID (a save,
  // a delete). Background loaders and the refresh timer used to write into it too, so a poll could
  // wipe out a real "Saving failed" message a second after it appeared, or put up "Loading ...
  // failed" that nobody asked for. Loaders now report here, keyed by what they load, and clear
  // their own key when they next succeed; the shell shows the first one as a separate notice.
  private readonly loadErrors = signal<Readonly<Record<string, string>>>({});
  readonly loadError = computed(() => Object.values(this.loadErrors())[0] ?? null);

  reportLoadError(key: string, error: unknown, fallback: string): void {
    const message = formatActionError(error, fallback);
    this.loadErrors.update(current => (current[key] === message ? current : { ...current, [key]: message }));
  }

  clearLoadError(key: string): void {
    this.loadErrors.update(current => {
      if (!(key in current)) return current;
      const next = { ...current };
      delete next[key];
      return next;
    });
  }

  // ----- Current linkshell -----
  //
  // THE linkshell the app is showing, as the server resolved it (overview.primaryLinkshell: the
  // saved choice when the user is still a member there, else their first membership), or 0 when
  // there is none. It used to be worked out a dozen different ways (some from the raw saved id,
  // some falling back to the first linkshell), and they could disagree right after a switch or
  // after leaving a linkshell. Read this one everywhere.
  readonly currentLinkshellId = computed(() => currentLinkshellIdOf(this.overview()));

  // Many callers refresh the overview at once (timer, change feed, every write), and the replies
  // can come back out of order. Only a reply newer than the one on screen is shown, so a slow,
  // older reply can never undo a change the user just made.
  private readonly overviewOrder = new ResponseOrder();

  async refreshOverview(): Promise<void> {
    const ticket = this.overviewOrder.begin();
    const overview = await this.fetchOverview(this.currentAccessToken());
    if (this.overviewOrder.accept(ticket)) {
      this.overview.set(overview);
    }
  }

  // For the few places that replace or patch the overview without fetching it (the reconnect
  // reset, a saved time zone). Any fetch already on the wire started before this change, so its
  // reply is dropped rather than allowed to undo it.
  setOverviewLocally(overview: ActivityOverview | null): void {
    this.overviewOrder.invalidate();
    this.overview.set(overview);
  }

  async fetchOverview(accessToken?: string): Promise<ActivityOverview> {
    return this.fetchActivityJson<ActivityOverview>('/api/activity/overview', accessToken);
  }

  // Its own fetch rather than ActivityHttpClient's, to avoid a circular dependency
  // (ActivityHttpClient depends on AuthService for the token).
  private async fetchActivityJson<T>(path: string, accessToken?: string): Promise<T> {
    const headers: Record<string, string> = { ...this.guildHeaders() };
    if (accessToken) {
      headers['Authorization'] = `Bearer ${accessToken}`;
    }

    const response = await fetch(path, {
      headers,
      cache: 'no-store',
      credentials: 'include'
    });

    // Shared with ActivityHttpClient, so an HTML error page never lands in the banner as markup.
    return readJsonResponse<T>(response, { onUnauthorized: () => this.noteUnauthorized() });
  }
}
