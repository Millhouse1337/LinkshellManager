import { Injectable, signal } from '@angular/core';

import type {
  ActivityOverview,
  DiscordSession,
  LocalActivityUser
} from './discord-activity.types';

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

  currentAccessToken(): string | undefined {
    return this.session()?.access_token;
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

  async refreshOverview(): Promise<void> {
    const accessToken = this.currentAccessToken();
    this.overview.set(await this.fetchOverview(accessToken));
  }

  async fetchOverview(accessToken?: string): Promise<ActivityOverview> {
    return this.fetchActivityJson<ActivityOverview>('/api/activity/overview', accessToken);
  }

  // Inlined fetch helper, kept private to avoid a circular dependency on
  // ActivityHttpClient (which itself depends on AuthService for the token).
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

    const responseText = await response.text();
    let payload: unknown = {};

    if (responseText) {
      try {
        payload = JSON.parse(responseText);
      } catch {
        payload = { error: responseText };
      }
    }

    if (!response.ok) {
      const errorPayload = payload as { error?: unknown };
      const message =
        typeof errorPayload.error === 'string'
          ? errorPayload.error
          : `Loading linkshell activity data failed with status ${response.status}.`;
      throw new Error(message);
    }

    return payload as T;
  }
}
