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
