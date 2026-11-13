import type { DiscordRpcErrorLike } from './discord-activity.types';

// Forwards a `<input type="datetime-local">` value to the server **as a
// naive ISO string** (no `Z`, no offset). The server's
// TryConvertUserTimeZoneToUtc interprets naive input with the user's
// PROFILE timezone, which is the source of truth — the previous version
// of this function called `new Date(value).toISOString()`, which silently
// re-interpreted the input with the BROWSER zone before stamping `Z`.
// That short-circuited the server's profile-zone path and stored wrong
// UTC instants whenever browser and profile zones differed.
export function datetimeLocalToUtcIso(value?: string | null): string | null {
  if (!value) {
    return null;
  }
  const trimmed = value.trim();
  if (!trimmed) {
    return null;
  }
  // Reject anything that doesn't look like a datetime-local payload so we
  // don't ship malformed strings to the server. Accepts both the
  // minute-resolution form and the seconds-resolution form that the ToD
  // input emits.
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2})?$/.test(trimmed)) {
    return null;
  }
  return trimmed;
}

// The other direction: an instant (an ISO string from the server) written as the
// "YYYY-MM-DDTHH:mm[:ss]" text a datetime-local box shows, on the clock of `timeZone` (the
// viewer's PROFILE zone, not the computer's). Empty for a missing or unreadable value.
// DiscordActivityService.toViewerLocalInputValue calls this with the viewer's zone.
export function toLocalInputValueInZone(
  value: string | null | undefined,
  timeZone: string,
  includeSeconds = false
): string {
  if (!value) {
    return '';
  }

  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return '';
  }

  const parts = new Intl.DateTimeFormat('en-CA', {
    timeZone,
    year: 'numeric',
    month: '2-digit',
    day: '2-digit',
    hour: '2-digit',
    minute: '2-digit',
    ...(includeSeconds ? { second: '2-digit' as const } : {}),
    // h23, not hour12: false: Chromium (Discord's client) writes midnight as "24" with the
    // latter, which the input cannot show and the server cannot parse.
    hourCycle: 'h23'
  }).formatToParts(date);

  const lookup = (type: Intl.DateTimeFormatPartTypes): string =>
    parts.find(part => part.type === type)?.value ?? '';

  const hhmm = `${lookup('year')}-${lookup('month')}-${lookup('day')}T${lookup('hour')}:${lookup('minute')}`;
  return includeSeconds ? `${hhmm}:${lookup('second') || '00'}` : hhmm;
}

export async function withTimeout<T>(promise: Promise<T>, timeoutMs: number, message: string): Promise<T> {
  let timer: ReturnType<typeof setTimeout> | null = null;

  try {
    return await Promise.race([
      promise,
      new Promise<T>((_, reject) => {
        timer = setTimeout(() => reject(new Error(message)), timeoutMs);
      })
    ]);
  } finally {
    if (timer) {
      clearTimeout(timer);
    }
  }
}

export function formatError(error: unknown): string {
  if (isDiscordRpcError(error)) {
    const rpcMessage = error.data?.message ?? error.message ?? 'Discord RPC call failed.';
    const rpcCode = error.data?.code ?? error.code;
    const cmd = error.cmd ?? 'unknown';
    const details =
      rpcCode !== undefined
        ? `Discord ${cmd.toLowerCase()} failed (${rpcCode}): ${rpcMessage}`
        : `Discord ${cmd.toLowerCase()} failed: ${rpcMessage}`;

    return withDiscordHint(details, cmd);
  }

  if (error instanceof Error && error.message) {
    return withDiscordHint(error.message);
  }

  return 'An unknown error occurred while initializing the Discord Activity.';
}

export function isDiscordRpcError(error: unknown): error is DiscordRpcErrorLike {
  if (!error || typeof error !== 'object') {
    return false;
  }

  const candidate = error as DiscordRpcErrorLike;
  // A plain Error (what our own HTTP client throws, e.g. a server's refusal message) has a
  // `message` too, but none of the RPC fields. Without this check it was dressed up as
  // "Discord unknown failed: <message>" on screens that have nothing to do with Discord.
  // The SDK's own errors carry a `code`, so they still count.
  if (error instanceof Error
      && candidate.cmd === undefined
      && candidate.code === undefined
      && candidate.data === undefined) {
    return false;
  }
  return Boolean(candidate.cmd || candidate.message || candidate.data?.message);
}

export function withDiscordHint(message: string, command?: string): string {
  const normalized = message.toLowerCase();
  const isAuthorizeFailure =
    command?.toLowerCase() === 'authorize' ||
    normalized.includes('authorize') ||
    normalized.includes('authorization');

  if (!isAuthorizeFailure) {
    return message;
  }

  return `${message} Check Discord Developer Portal OAuth2 redirects for https://127.0.0.1 and confirm Activities URL Mapping points '/' at the current public host.`;
}

export function formatActionError(error: unknown, fallback: string): string {
  if (error instanceof Error && error.message) {
    return error.message;
  }

  return fallback;
}

export function resolveBrowserTimeZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
  } catch {
    return 'UTC';
  }
}
