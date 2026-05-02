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

  async fetchActivityJson<T>(path: string, accessToken?: string): Promise<T> {
    const headers: Record<string, string> = {};
    const token = accessToken ?? this.auth.currentAccessToken();
    if (token) {
      headers['Authorization'] = `Bearer ${token}`;
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

  async postActivityAction(path: string, body?: unknown): Promise<void> {
    await this.postActivityJson(path, body);
  }

  async postActivityJson<T = void>(path: string, body?: unknown): Promise<T> {
    const headers: Record<string, string> = {
      'Content-Type': 'application/json'
    };

    const accessToken = this.auth.currentAccessToken();
    if (accessToken) {
      headers['Authorization'] = `Bearer ${accessToken}`;
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

      return JSON.parse(responseText) as T;
    }

    const responseText = await response.text();
    if (!responseText) {
      throw new Error(`Activity request failed with status ${response.status}.`);
    }

    try {
      const payload = JSON.parse(responseText) as { error?: string };
      throw new Error(payload.error || `Activity request failed with status ${response.status}.`);
    } catch {
      throw new Error(responseText);
    }
  }
}
