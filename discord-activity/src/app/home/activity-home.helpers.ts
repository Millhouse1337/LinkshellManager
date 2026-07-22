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
export function rankIcon(rank?: string | null): string {
  switch ((rank ?? '').toLowerCase()) {
    case 'leader': return '👑';
    case 'officer': return '⭐';
    case 'trial': return '🌱';
    default: return '🛡️';
  }
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
