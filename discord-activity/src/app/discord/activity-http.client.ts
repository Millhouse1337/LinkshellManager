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
    let parsedJson = false;

    if (responseText) {
      try {
        payload = JSON.parse(responseText);
        parsedJson = true;
      } catch {
        // Non-JSON body (HTML 502/login/SPA-fallback page) — do NOT stuff the
        // raw markup into the error; nonJsonMessage() produces a clean message.
        parsedJson = false;
      }
    }

    if (!response.ok) {
      const errorPayload = parsedJson ? (payload as { error?: unknown }) : null;
      const message =
        errorPayload && typeof errorPayload.error === 'string'
          ? errorPayload.error
          : this.nonJsonMessage(response, responseText);
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
      throw new Error(this.emptyBodyMessage(response.status));
    }

    let parsed: ActivityErrorBody | null = null;
    try {
      parsed = JSON.parse(responseText) as ActivityErrorBody;
    } catch {
      // not JSON — fall through to the friendly message below
    }
    if (parsed?.error) {
      throw new Error(parsed.error);
    }
    // ASP.NET ProblemDetails — what [ApiController] returns on its own when model
    // binding/validation rejects the body, BEFORE the action runs, so there is no
    // hand-written { error } to read. Left unhandled, a real "this field is required"
    // surfaced as the generic "unexpected response (status 400)", which reads like a
    // server fault and tells nobody which field was wrong.
    const problem = this.problemDetailsMessage(parsed);
    if (problem) {
      throw new Error(problem);
    }
    throw new Error(this.nonJsonMessage(response, responseText));
  }

  // Flattens an ASP.NET ProblemDetails body into one readable line: every field error
  // it carries, then its detail/title as a fallback. The field names are the server’s
  // own DTO property names, which is precisely what makes the message diagnosable.
  private problemDetailsMessage(body: ActivityErrorBody | null): string | null {
    if (!body) {
      return null;
    }

    const fieldErrors: string[] = [];
    for (const [field, messages] of Object.entries(body.errors ?? {})) {
      const text = (Array.isArray(messages) ? messages : [messages])
        .filter((message): message is string => typeof message === 'string' && message.trim().length > 0)
        .join(' ');
      if (!text) {
        continue;
      }
      // A body-level error is keyed by "" or "$" — no field name worth printing.
      fieldErrors.push(field && field !== '$' ? `${field}: ${text}` : text);
    }

    if (fieldErrors.length > 0) {
      return `The server rejected this request — ${fieldErrors.join('; ')}`;
    }

    // `detail` is the free-text half of ProblemDetails; `title` is the generic
    // "One or more validation errors occurred.", still better than saying nothing.
    const fallback = body.detail?.trim() || body.title?.trim();
    return fallback ? `The server rejected this request — ${fallback}` : null;
  }

  // ASP.NET's Forbid()/Challenge() return an empty body, so there's no JSON
  // { error } to surface. Map the common auth statuses to something actionable
  // instead of a bare "Activity request failed with status 403".
  private emptyBodyMessage(status: number): string {
    if (status === 403) {
      // Keep this identical to the server's generic 403 body (Program.cs
      // OnRedirectToAccessDenied) so the message reads the same whether it comes
      // from the response body or this empty-body fallback.
      return "You don't have permission to do that. If you think you should, ask a linkshell leader to update your role.";
    }
    if (status === 401) {
      return 'Your session may have expired — reload the Activity and try again.';
    }
    return `Activity request failed with status ${status}.`;
  }

  // Turns a non-JSON response (HTML login/access-denied page, SPA fallback, a
  // followed redirect) into a clear, actionable message instead of letting a
  // raw JSON.parse error or a dumped HTML page reach the UI.
  private nonJsonMessage(response: Response, text: string): string {
    // Gateway/availability errors come back as a Cloudflare/origin HTML page.
    // Report them as a transient server issue, not a session problem.
    if (response.status === 502 || response.status === 503 || response.status === 504) {
      return `The server is temporarily unavailable (HTTP ${response.status}). Please try again in a moment.`;
    }
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

// Every error-body shape this client knows how to read: the app’s own { error },
// plus the ProblemDetails ASP.NET emits for model-validation failures.
interface ActivityErrorBody {
  error?: string | null;
  title?: string | null;
  detail?: string | null;
  errors?: Record<string, string[] | string> | null;
}

interface ActivityAntiforgeryToken {
  headerName?: string | null;
  requestToken?: string | null;
}
