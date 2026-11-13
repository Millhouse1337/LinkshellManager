// Reading a server reply, shared by every fetch in the app (ActivityHttpClient, AuthService's
// overview fetch, and the Discord sign-in calls in DiscordActivityService).
//
// A plain module on purpose, not an injected service: AuthService cannot inject
// ActivityHttpClient (that one injects AuthService), so a service here would bring back the
// circular dependency the three old copies were written to avoid.
//
// The one rule all three now share: a reply that is not JSON (an HTML error page, a login page,
// the SPA fallback) never reaches the banner as raw markup. It becomes a short, readable message.

// Every error-body shape the app knows how to read: its own { error }, plus the ProblemDetails
// ASP.NET emits when model validation rejects a request.
export interface ActivityErrorBody {
  error?: string | null;
  correlationId?: string | null;
  title?: string | null;
  detail?: string | null;
  errors?: Record<string, string[] | string> | null;
}

export interface ReadJsonOptions {
  // Called on a 401, so the website can drop to its sign-in screen.
  onUnauthorized?: () => void;
}

// Parses a GET reply. Throws an Error with a readable message when the server refused.
export async function readJsonResponse<T>(response: Response, options: ReadJsonOptions = {}): Promise<T> {
  const text = await response.text();
  const parsed = tryParseJson(text);

  if (!response.ok) {
    if (response.status === 401) {
      options.onUnauthorized?.();
    }
    const body = parsed.ok ? (parsed.value as ActivityErrorBody | null) : null;
    const message = body && typeof body.error === 'string'
      ? withCorrelationId(body)
      : nonJsonMessage(response.status, response.redirected, text);
    throw new Error(message);
  }

  if (!text) {
    return {} as T;
  }
  if (!parsed.ok) {
    // 2xx but not JSON: almost always a followed login redirect or the SPA fallback page.
    throw new Error(nonJsonMessage(response.status, response.redirected, text));
  }
  return parsed.value as T;
}

export function tryParseJson(text: string): { ok: true; value: unknown } | { ok: false } {
  if (!text) {
    return { ok: false };
  }
  try {
    return { ok: true, value: JSON.parse(text) };
  } catch {
    return { ok: false };
  }
}

// The server's crash reply (ApiExceptionHandler) carries a correlationId: the same id sits in the
// server log next to the real exception, so a screenshot of the message can be matched to it.
export function withCorrelationId(body: ActivityErrorBody): string {
  const message = body.error ?? '';
  const id = typeof body.correlationId === 'string' ? body.correlationId.trim() : '';
  return id ? `${message} (Error id: ${id})` : message;
}

// A reply that is not the app's own JSON, turned into something a member can act on.
export function nonJsonMessage(status: number, redirected: boolean, text: string): string {
  // Gateway errors come back as a Cloudflare/origin HTML page: a short outage, not a login problem.
  if (status === 502 || status === 503 || status === 504) {
    return `The server is temporarily unavailable (HTTP ${status}). Please try again in a moment.`;
  }
  // A crash is not a login problem either. Calling it "session expired" sends people to reload
  // and retry a save that may have half-run.
  if (status >= 500) {
    return `Something went wrong on the server (HTTP ${status}).`;
  }
  if (redirected || /^\s*</.test(text ?? '')) {
    return 'Your session may have expired — reload the Activity and try again.';
  }
  return `The server returned an unexpected response (status ${status}).`;
}
