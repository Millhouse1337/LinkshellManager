// Pure helper functions extracted from `activity-home.component.ts`.
//
// Every function in this file is a standalone utility — none reference
// component instance state. Bodies are byte-for-byte identical to the
// originals; only the declaration form changed (private/protected method
// → exported function).

import type {
  ActivityEventParticipant,
  ActivityStatusLedgerEntry,
  ActivityTodLootInput
} from '../discord/discord-activity.service';
import type { ActivityLinkshellSettings, ActivityOverview } from '../discord/discord-activity.types';

// ----- Linkshell type -----
//
// Lives here rather than on ActivityHomeComponent because three separate places need it now that
// attendance renders inside the Event System tab: the shell, that tab, and the refresh timer that
// decides whether to fetch window-event data at all.

export function primaryLinkshellSettings(
  overview: ActivityOverview | null | undefined
): ActivityLinkshellSettings | null {
  const primaryId = overview?.appUser?.primaryLinkshellId;
  if (primaryId == null) return null;
  return overview?.linkshells?.find(l => l.id === primaryId)?.settings ?? null;
}

export function breakSessionInfo(
  participant: ActivityEventParticipant,
  breakReturnId: number
): { sessionNumber: number; durationMs: number } | null {
  const sorted = [...participant.statusLedger].sort(
    (a, b) => new Date(a.occurredAt).getTime() - new Date(b.occurredAt).getTime()
  );
  let currentStart: ActivityStatusLedgerEntry | null = null;
  let sessionNumber = 0;
  for (const entry of sorted) {
    if (entry.actionType === 'BreakStart') {
      currentStart = entry;
      sessionNumber += 1;
    } else if (entry.actionType === 'BreakReturn' && currentStart) {
      if (entry.id === breakReturnId) {
        const durationMs = Math.max(
          0,
          new Date(entry.occurredAt).getTime() - new Date(currentStart.occurredAt).getTime()
        );
        return { sessionNumber, durationMs };
      }
      currentStart = null;
    }
  }
  return null;
}

export function formatBreakDuration(ms: number): string {
  if (!Number.isFinite(ms) || ms <= 0) {
    return '0s';
  }
  const totalSeconds = Math.floor(ms / 1000);
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;
  if (hours > 0) {
    return `${hours}h ${minutes}m`;
  }
  if (minutes > 0) {
    return `${minutes}m ${seconds}s`;
  }
  return `${seconds}s`;
}

export function createEmptyTodLootRow(): ActivityTodLootInput {
  return {
    itemName: '',
    itemWinner: '',
    winningDkpSpent: null
  };
}

export function toDateTimeLocalValue(date: Date): string {
  const pad = (value: number) => value.toString().padStart(2, '0');
  return `${date.getFullYear()}-${pad(date.getMonth() + 1)}-${pad(date.getDate())}T${pad(date.getHours())}:${pad(date.getMinutes())}:${pad(date.getSeconds())}`;
}

export function parseDate(value?: string | null): number | null {
  if (!value) {
    return null;
  }

  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? null : parsed.getTime();
}

export function parseLocalDateTime(value?: string | null): Date | null {
  if (!value) {
    return null;
  }

  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? null : parsed;
}

export function formatElapsed(totalMilliseconds: number): string {
  const totalSeconds = Math.max(0, Math.floor(totalMilliseconds / 1000));
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;

  return [hours, minutes, seconds].map(value => value.toString().padStart(2, '0')).join(':');
}

// ----- ToD tracker -----
//
// Shown wherever a ToD has no recorded Time of Death / repop: the camp ended without anyone
// seeing the mob die (the window closed, or another linkshell took it), so nothing was entered.
// Deliberately NOT "Unavailable" or "Ready" — a blank countdown used to render as a green
// "Ready", which read as "it's poppable now" when the truth is "we don't know".
export const TOD_NOT_ENTERED = 'Not entered';

// Milliseconds until a ToD's repop, or null when it has no repop to count down to.
// Distinct from "0 ms remaining": null means not-entered, 0 means the window is open.
export function todRemainingMs(repopTime: string | null | undefined, nowMs: number): number | null {
  const targetTime = parseDate(repopTime);
  if (targetTime === null) {
    return null;
  }

  return Math.max(0, targetTime - nowMs);
}

export function todCountdownLabel(repopTime: string | null | undefined, nowMs: number): string {
  const remaining = todRemainingMs(repopTime, nowMs);
  if (remaining === null) {
    return TOD_NOT_ENTERED;
  }

  return remaining <= 0 ? 'Ready' : formatElapsed(remaining);
}

// Only a real repop that has already arrived counts as ready — a not-entered ToD must not
// light up the green "window open" treatment.
export function isTodReady(repopTime: string | null | undefined, nowMs: number): boolean {
  return todRemainingMs(repopTime, nowMs) === 0;
}

// Sort key for "which ToD is this monster's most recent". A not-entered ToD has no `time`, but
// it's still the newest thing that happened to that monster, so fall back to when the row was
// written — otherwise the pop it superseded would keep showing as current with a stale countdown.
export function todSortKey(tod: { time?: string | null; timeStamp?: string | null }): number {
  return parseDate(tod.time) ?? parseDate(tod.timeStamp) ?? 0;
}

export function formatDkp(totalMilliseconds: number, dkpPerHour?: number | null): string {
  const rate = dkpPerHour ?? 0;
  return ((totalMilliseconds / 3600000) * rate).toFixed(2);
}

export function formatAlts(alt1?: string | null, alt2?: string | null): string {
  return [alt1, alt2]
    .filter((value): value is string => !!value && value.trim() !== '')
    .join(', ');
}

// Rank tier icon for the linkshell rank system, shown next to the rank label
// across rosters/headers. Falls back to the Member badge for custom roles.
//
// Deliberately NOT extended with an "admin" case: the app-wide admin override is
// additive, not a rank. It renders as a SEPARATE `🔧 ADMIN` chip beside whatever
// rank the linkshell actually gave the member.
export function rankIcon(rank?: string | null): string {
  switch ((rank ?? '').toLowerCase()) {
    case 'leader': return '👑';
    case 'officer': return '⭐';
    case 'trial': return '🌱';
    default: return '🛡️';
  }
}

// The app-wide admin override's own mark, shown IN ADDITION to the rank icon.
export const ADMIN_BADGE = '🔧 ADMIN';

// The coarse "can manage this linkshell" bar, in ONE place. Mirrors the server's
// Leader/Officer rank gate, plus the app-wide admin override.
//
// The membership lookup comes FIRST and returns false when absent — that is what
// scopes the override to linkshells the user actually belongs to, exactly as the
// server does. Never reorder these two checks, and never read
// `overview.adminOverrideActive` directly at a call site.
export function canManageLinkshellIn(
  overview: ActivityOverview | null | undefined,
  linkshellId: number | null | undefined,
  memberships?: readonly { id: number; rank?: string | null }[]
): boolean {
  if (linkshellId == null) return false;
  const list = memberships ?? overview?.linkshells ?? [];
  const membership = list.find(link => link.id === linkshellId);
  if (!membership) return false;
  if (overview?.adminOverrideActive === true) return true;
  const rank = (membership.rank ?? '').toLowerCase();
  return rank === 'leader' || rank === 'officer';
}

// Leader-tier gates (rank editing, removing members, transferring ownership).
// Same membership-first rule as above.
export function isLeaderTierIn(
  overview: ActivityOverview | null | undefined,
  linkshellId: number | null | undefined,
  memberships?: readonly { id: number; rank?: string | null }[]
): boolean {
  if (linkshellId == null) return false;
  const list = memberships ?? overview?.linkshells ?? [];
  const membership = list.find(link => link.id === linkshellId);
  if (!membership) return false;
  if (overview?.adminOverrideActive === true) return true;
  return (membership.rank ?? '').toLowerCase() === 'leader';
}

// Two-letter avatar initials from a character name (e.g. "Millhouse" -> "MI").
export function memberInitials(value?: string | null): string {
  const name = (value ?? '').trim();
  if (!name) return '??';
  const parts = name.split(/\s+/).filter(Boolean);
  if (parts.length === 1) return parts[0].slice(0, 2).toUpperCase();
  return (parts[0][0] + parts[1][0]).toUpperCase();
}

// Stable a–e palette bucket for a name, so a member's avatar color is consistent
// across every roster that renders them.
export function memberAvatarClass(name?: string | null): string {
  const trimmed = (name ?? '').trim();
  if (!trimmed) return 'a';
  let hash = 0;
  for (let i = 0; i < trimmed.length; i += 1) {
    hash = (hash * 31 + trimmed.charCodeAt(i)) >>> 0;
  }
  return ['a', 'b', 'c', 'd', 'e'][hash % 5];
}

// Tag color class for a member's Active/Inactive/Pending status.
export function memberStatusClass(status?: string | null): string {
  const normalized = (status ?? 'Active').toLowerCase();
  if (normalized === 'active') return 'success';
  if (normalized === 'pending') return 'warning';
  if (normalized === 'inactive') return 'danger';
  return 'default';
}
