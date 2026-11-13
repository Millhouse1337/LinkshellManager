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

// What members see when Discord sign-in (the authorize step) does not finish: most often they
// pressed Cancel on Discord's prompt. The setup hint for the developer goes to the console.
export const DISCORD_SIGN_IN_CANCELLED_MESSAGE =
  'Discord sign-in was cancelled or did not finish. Press Reconnect to Discord to try again.';

const DISCORD_SETUP_HINT =
  "If sign-in keeps failing, check the Discord Developer Portal: OAuth2 redirects must include https://127.0.0.1 and Activities URL Mapping must point '/' at the current public host.";

// One readable line for any error the app catches: startup, sign-out, Data Admin and the rest.
export function formatError(error: unknown): string {
  // Errors first. Our own HTTP client throws a plain Error whose message is the server's
  // refusal; it has nothing to do with Discord and is shown as it is.
  if (error instanceof Error) {
    return error.message || 'An unknown error occurred while loading LinkshellManager.';
  }

  // A rejected Discord SDK command: a plain object with a numeric code.
  if (isDiscordRpcError(error)) {
    if (isAuthorizeCommand(error.cmd)) {
      console.info(DISCORD_SETUP_HINT, error);
      return DISCORD_SIGN_IN_CANCELLED_MESSAGE;
    }
    const rpcMessage = error.data?.message ?? error.message ?? 'No details.';
    const rpcCode = error.data?.code ?? error.code;
    const action = error.cmd ? `Discord ${error.cmd.toLowerCase()}` : 'A Discord request';
    return `${action} failed (${rpcCode}): ${rpcMessage}`;
  }

  return 'An unknown error occurred while loading LinkshellManager.';
}

// The shape the Discord SDK rejects a command with: a plain object (not an Error) carrying a
// numeric code, its own or under `data`.
export function isDiscordRpcError(error: unknown): error is DiscordRpcErrorLike {
  if (!error || typeof error !== 'object' || error instanceof Error) {
    return false;
  }
  const candidate = error as DiscordRpcErrorLike;
  return typeof candidate.code === 'number' || typeof candidate.data?.code === 'number';
}

// The SDK's rejection does not say which command failed. The caller knows, so it tags the
// rejection on its way through (used for authorize, so a cancelled sign-in reads as one).
export function tagDiscordCommand(error: unknown, cmd: string): unknown {
  if (error && typeof error === 'object' && !(error instanceof Error)) {
    return { ...(error as object), cmd };
  }
  return error;
}

function isAuthorizeCommand(cmd: string | undefined): boolean {
  return (cmd ?? '').toLowerCase() === 'authorize';
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
