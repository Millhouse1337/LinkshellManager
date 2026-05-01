import type { DiscordRpcErrorLike } from './discord-activity.types';

export function datetimeLocalToUtcIso(value?: string | null): string | null {
  if (!value) {
    return null;
  }
  const trimmed = value.trim();
  if (!trimmed) {
    return null;
  }
  const parsed = new Date(trimmed);
  if (Number.isNaN(parsed.getTime())) {
    return null;
  }
  return parsed.toISOString();
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
  if (error instanceof Error && error.message) {
    return withDiscordHint(error.message);
  }

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
