import { Injectable, inject } from '@angular/core';

import { AuthService } from './auth.service';

/**
 * Shared HTTP plumbing for the per-domain Activity services.
 *
 * Bytes copied from the original DiscordActivityService private helpers:
 * - postActivityAction
 * - postActivityJson
 * - fetchActivityJson
 *
 * The access token is sourced from AuthService so per-domain services don't
 * have to thread it through every call site.
 */
@Injectable({ providedIn: 'root' })
export class ActivityHttpClient {
  private readonly auth = inject(AuthService);
  private antiforgeryTokenPromise: Promise<ActivityAntiforgeryToken | null> | null = null;

  async fetchActivityJson<T>(path: string, accessToken?: string, signal?: AbortSignal): Promise<T> {
    const headers: Record<string, string> = { ...this.auth.guildHeaders() };
    const token = accessToken ?? this.auth.currentAccessToken();
    if (token) {
      headers['Authorization'] = `Bearer ${token}`;
    }

    const response = await fetch(path, {
      headers,
      cache: 'no-store',
      credentials: 'include',
      signal
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

  async postActivityAction(path: string, body?: unknown): Promise<void> {
    await this.postActivityJson(path, body);
  }

  async postActivityJson<T = void>(path: string, body?: unknown): Promise<T> {
    const headers: Record<string, string> = {
      'Content-Type': 'application/json',
      ...this.auth.guildHeaders()
    };

    const accessToken = this.auth.currentAccessToken();
    if (accessToken) {
      headers['Authorization'] = `Bearer ${accessToken}`;
    } else {
      const antiforgery = await this.getAntiforgeryToken();
      if (antiforgery?.headerName && antiforgery.requestToken) {
        headers[antiforgery.headerName] = antiforgery.requestToken;
      }
    }

    const response = await fetch(path, {
      method: 'POST',
      headers,
      cache: 'no-store',
      credentials: 'include',
      body: body ? JSON.stringify(body) : undefined
    });

    if (response.ok) {
      if (response.status === 204) {
        return undefined as T;
      }

      const responseText = await response.text();
      if (!responseText) {
        return undefined as T;
      }

      try {
        return JSON.parse(responseText) as T;
      } catch {
        // 2xx but not JSON — almost always an auth redirect followed to an HTML
        // page, or the SPA fallback. Surface a clear message, never a raw
        // "Unexpected token '<'".
        throw new Error(this.nonJsonMessage(response, responseText));
      }
    }

    const responseText = await response.text();
    if (!responseText) {
      throw new Error(`Activity request failed with status ${response.status}.`);
    }

    let parsed: { error?: string } | null = null;
    try {
      parsed = JSON.parse(responseText) as { error?: string };
    } catch {
      // not JSON — fall through to the friendly message below
    }
    if (parsed?.error) {
      throw new Error(parsed.error);
    }
    throw new Error(this.nonJsonMessage(response, responseText));
  }

  // Turns a non-JSON response (HTML login/access-denied page, SPA fallback, a
  // followed redirect) into a clear, actionable message instead of letting a
  // raw JSON.parse error or a dumped HTML page reach the UI.
  private nonJsonMessage(response: Response, text: string): string {
    const looksHtml = /^\s*</.test(text ?? '');
    if (response.redirected || looksHtml) {
      return 'Your session may have expired — reload the Activity and try again.';
    }
    return `The server returned an unexpected response (status ${response.status}).`;
  }

  private async getAntiforgeryToken(): Promise<ActivityAntiforgeryToken | null> {
    this.antiforgeryTokenPromise ??= this.fetchAntiforgeryToken();
    return this.antiforgeryTokenPromise;
  }

  private async fetchAntiforgeryToken(): Promise<ActivityAntiforgeryToken | null> {
    const response = await fetch('/api/activity/antiforgery', {
      cache: 'no-store',
      credentials: 'include'
    });

    if (!response.ok) {
      return null;
    }

    return (await response.json()) as ActivityAntiforgeryToken;
  }
}

interface ActivityAntiforgeryToken {
  headerName?: string | null;
  requestToken?: string | null;
}
