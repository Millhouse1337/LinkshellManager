// Pure helper functions extracted from `activity-home.component.ts`.
//
// Every function in this file is a standalone utility — none reference
// component instance state. Bodies are byte-for-byte identical to the
// originals; only the declaration form changed (private/protected method
// → exported function).

import type {
  ActivityEventParticipant,
  ActivityStatusLedgerEntry
} from '../discord/discord-activity.service';
import type { ActivityLinkshellSettings, ActivityMonsterSetup, ActivityOverview } from '../discord/discord-activity.types';
import { hasSpawnWindowCadence } from './activity-home.types';

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

// "16 additional seconds": how every form words a monster's Additional Seconds (Monster Setups).
export function additionalSecondsLabel(seconds: number): string {
  return seconds === 1 ? '1 additional second' : `${seconds} additional seconds`;
}

// A form's "YYYY-MM-DDTHH:mm:ss" value is the viewer's wall clock, and the server is what converts
// it. So these two read it as if it were UTC: plain clock arithmetic and formatting, which a
// daylight-saving change in the browser's own zone cannot bend.
export function shiftLocalDateTime(value: string, seconds: number): string {
  const clock = Date.parse(`${value}Z`);
  return Number.isNaN(clock) ? value : new Date(clock + seconds * 1000).toISOString().slice(0, 19);
}

// The clock time alone, for reading beside the box: "1:00:16 PM".
export function formatLocalClockTime(value: string): string {
  const clock = Date.parse(`${value}Z`);
  return Number.isNaN(clock)
    ? value
    : new Intl.DateTimeFormat(undefined, { timeZone: 'UTC', timeStyle: 'medium' }).format(clock);
}

export function parseDate(value?: string | null): number | null {
  if (!value) {
    return null;
  }

  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? null : parsed.getTime();
}

// The instant a form's "YYYY-MM-DDTHH:mm[:ss]" box means when read in `timeZone` (the viewer's
// PROFILE zone), as epoch milliseconds. This is what the server does with the same text when the
// form is saved, so a preview built on it matches what gets stored. `new Date(value)` must not be
// used for this: it reads the text in the COMPUTER's zone, which is wrong whenever the two differ.
//
// Daylight-saving edges follow the server (NodaTime AtLeniently): a time that happens twice
// (clocks go back) is the first one, and a time that never happens (clocks go forward) moves on
// by the size of the gap. An unknown zone is read as UTC, as the server does. Null if unreadable.
export function zonedLocalInputToUtcMs(value: string | null | undefined, timeZone: string): number | null {
  const trimmed = (value ?? '').trim();
  if (!/^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(?::\d{2})?$/.test(trimmed)) {
    return null;
  }
  // The wall clock read as if it were UTC. The real instant is this minus the zone's offset.
  const wall = Date.parse(`${trimmed.length === 16 ? `${trimmed}:00` : trimmed}Z`);
  if (Number.isNaN(wall)) {
    return null;
  }

  let format: Intl.DateTimeFormat;
  try {
    format = new Intl.DateTimeFormat('en-US', {
      timeZone: timeZone || 'UTC',
      hourCycle: 'h23',
      year: 'numeric', month: '2-digit', day: '2-digit',
      hour: '2-digit', minute: '2-digit', second: '2-digit'
    });
  } catch {
    return wall;
  }
  // How far the zone's clock is ahead of UTC at `instant`, in ms.
  const offsetAt = (instant: number): number => {
    const parts = format.formatToParts(instant);
    const get = (type: Intl.DateTimeFormatPartTypes): number =>
      Number(parts.find(part => part.type === type)?.value ?? 0);
    const asUtc = Date.UTC(get('year'), get('month') - 1, get('day'), get('hour') % 24, get('minute'), get('second'));
    return asUtc - Math.floor(instant / 1000) * 1000;
  };

  // The offsets a day either side surround any daylight-saving change near this time. An offset
  // fits if the zone really has it at the instant it gives.
  const before = offsetAt(wall - 86_400_000);
  const after = offsetAt(wall + 86_400_000);
  const fits = [...new Set([before, after])].filter(offset => offsetAt(wall - offset) === offset);
  if (fits.length === 0) {
    return wall - before;               // In the gap: read with the offset from before it.
  }
  return wall - Math.max(...fits);     // Twice over: the larger offset is the earlier instant.
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

// Sort key for "which ToD is this monster's most recent". A not-entered ToD has no `time`, but
// it's still the newest thing that happened to that monster, so fall back to when the row was
// written — otherwise the pop it superseded would keep showing as current with a stale countdown.
export function todSortKey(tod: { time?: string | null; timeStamp?: string | null }): number {
  return parseDate(tod.time) ?? parseDate(tod.timeStamp) ?? 0;
}

// Windows to assume when a gridded monster's setup carries no window count (the short band's 7).
export const DEFAULT_POP_WINDOWS = 7;

// Reads a stored "22 Hour" / "45 Min" / "1 Hour 30 Min" label as minutes. A bare number means
// hours for a cooldown and minutes for an interval, matching the server's TodDurationFormat.
export function parseTodDurationMinutes(label: string | null | undefined, bareUnit: 'hours' | 'mins'): number | null {
  const text = (label ?? '').trim();
  if (!text) return null;

  const bare = /^\s*(\d+(?:\.\d+)?)\s*$/.exec(text);
  if (bare) {
    const amount = parseFloat(bare[1]);
    return amount > 0 ? Math.round(bareUnit === 'hours' ? amount * 60 : amount) : null;
  }

  const pair = /^\s*(?:(\d+(?:\.\d+)?)\s*(?:hours?|hrs?|h)\b)?\s*(?:(\d+(?:\.\d+)?)\s*(?:minutes?|mins?|m)\b)?\s*$/i.exec(text);
  if (!pair || (!pair[1] && !pair[2])) return null;
  const total = (pair[1] ? parseFloat(pair[1]) * 60 : 0) + (pair[2] ? parseFloat(pair[2]) : 0);
  return total > 0 ? Math.round(total) : null;
}

// "22h", "1h 30m", "45m" — the tracker's short durations.
export function formatShortDuration(totalMilliseconds: number): string {
  const totalMinutes = Math.max(0, Math.round(totalMilliseconds / 60_000));
  const hours = Math.floor(totalMinutes / 60);
  const minutes = totalMinutes % 60;
  if (hours === 0) return `${minutes}m`;
  return minutes === 0 ? `${hours}h` : `${hours}h ${minutes}m`;
}

// The linkshell's setup for a ToD's monster. Setups are stored under the merged "Base/Stronger"
// name while a ToD keeps the exact half that died, so both sides are compared merged.
export function monsterSetupForTod(
  setups: readonly ActivityMonsterSetup[] | null | undefined,
  monsterName: string | null | undefined
): ActivityMonsterSetup | null {
  const wanted = mergedMonsterName(monsterName).toLowerCase();
  if (!wanted || !setups) return null;
  return setups.find(setup => mergedMonsterName(setup.monsterName).toLowerCase() === wanted) ?? null;
}

// Where a tracked ToD is in its cycle:
//   pending — counting down to the repop
//   open    — inside the spawn window (repop has passed, the last window hasn't opened yet)
//   up      — past the last window, or past the repop for a monster with no window grid
//   unknown — no repop was recorded ("Not entered")
export type TodWindowTone = 'pending' | 'open' | 'up' | 'unknown';

export interface TodWindowState {
  tone: TodWindowTone;
  todMs: number | null;
  repopMs: number | null;
  // When the LAST window opens: the point the Log ToD form's countdown flips from "Open" to
  // "Passed". Null for a monster with no window grid (Sky gods, untimed NMs) — no close to show.
  closeMs: number | null;
}

export function todWindowState(
  tod: { monsterName?: string | null; time?: string | null; repopTime?: string | null; interval?: string | null },
  setup: ActivityMonsterSetup | null,
  nowMs: number
): TodWindowState {
  const todMs = parseDate(tod.time);
  const repopMs = parseDate(tod.repopTime);
  if (repopMs === null) {
    return { tone: 'unknown', todMs, repopMs, closeMs: null };
  }

  let closeMs: number | null = null;
  if (hasSpawnWindowCadence(tod.monsterName)) {
    // Same window count as the form's tiles; the ToD's own logged interval before the setup's.
    const windows = (setup?.windows ?? 0) > 0 ? setup!.windows! : DEFAULT_POP_WINDOWS;
    const interval = parseTodDurationMinutes(tod.interval, 'mins') ?? setup?.cadenceMinutes ?? null;
    if (interval && interval > 0 && windows > 1) {
      closeMs = repopMs + (windows - 1) * interval * 60_000;
    }
  }

  const tone: TodWindowTone = nowMs < repopMs
    ? 'pending'
    : closeMs !== null && nowMs < closeMs ? 'open' : 'up';
  return { tone, todMs, repopMs, closeMs };
}

// Base monster -> stronger counterpart. Mirrors HnmConfig.MonsterMergePairs on the server and
// constants.MONSTER_MERGE_PAIRS in the addon. The two halves share ONE spawn: killing Nidhogg
// means the next pop is Fafnir.
const MONSTER_MERGE_PAIRS: readonly (readonly [string, string])[] = [
  ['Adamantoise', 'Aspidochelone'],
  ['Behemoth', 'King Behemoth'],
  ['Fafnir', 'Nidhogg']
];

// The name a spawn is GROUPED and LABELLED under: the combined "Base/Stronger" form for either
// half (or for the combined label itself), and the trimmed name unchanged for anything else.
//
// The ToD feed returns the newest ToD per monster NAME, and a merge pair has two names, so one
// dragon arrives as two rows -- which rendered as a "Fafnir/Nidhogg" card sitting on top of a
// "Fafnir" card, each with its own countdown to a different repop. Both describe the same spawn
// at different points in its cycle, and a camper reading them cannot tell which clock is real.
//
// Grouping on this collapses them; labelling from it stops the survivor being captioned with
// whichever half happened to win. It is DISPLAY ONLY -- a ToD is stored under the exact half that
// died, so anything writing back to the server keeps sending tod.monsterName.
export function mergedMonsterName(name?: string | null): string {
  const trimmed = (name ?? '').trim();
  if (!trimmed) {
    return '';
  }
  const lower = trimmed.toLowerCase();
  for (const [base, stronger] of MONSTER_MERGE_PAIRS) {
    const combined = `${base}/${stronger}`;
    if (lower === base.toLowerCase()
        || lower === stronger.toLowerCase()
        || lower === combined.toLowerCase()) {
      return combined;
    }
  }
  return trimmed;
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

// The "manage events" PERMISSION, not the rank — the gate the server's event
// lifecycle endpoints actually apply (ActivityDataController: CanManageEvents).
// Use this, not canManageLinkshellIn(), for anything that ends up calling one of
// them; gating a button on rank while the endpoint checks a permission is what let
// a custom role with Manage events on call End Camp but never see its button.
//
// Same membership-first rule as above, and no adminOverrideActive read is needed:
// the server already sends `permissions` all-true when the override is carried.
export function canManageEventsIn(
  overview: ActivityOverview | null | undefined,
  linkshellId: number | null | undefined
): boolean {
  if (linkshellId == null) return false;
  const membership = (overview?.linkshells ?? []).find(link => link.id === linkshellId);
  if (!membership) return false;
  return membership.permissions?.canManageEvents === true;
}

// Editing a live camp's pay and attendance: window DKP, the open / close / kill bonus, the
// closing tick, Misc posts and their DKP, deleting a window, a capture or an attendee.
// Mirrors the server's LinkshellPermissions.EditsLiveCamp — Moderate live events OR Manage
// events, and the Leader always — which every one of those routes checks. These buttons used
// to follow the Leader/Officer rank instead, so a custom role saw none of them while the
// server would have let it, and an Officer whose role lost both flags saw buttons that 403.
//
// Same membership-first rule as above; the override arrives as all-true `permissions`.
export function canEditLiveCampIn(
  overview: ActivityOverview | null | undefined,
  linkshellId: number | null | undefined
): boolean {
  if (linkshellId == null) return false;
  const membership = (overview?.linkshells ?? []).find(link => link.id === linkshellId);
  if (!membership) return false;
  if (isLeaderRankName(membership.rank)) return true;
  const permissions = membership.permissions;
  return permissions?.canModerateLiveEvent === true || permissions?.canManageEvents === true;
}

// True for the Leader rank name. Same test the server uses (LinkshellRanks.IsLeader:
// "Leader", any letter case), so a custom role spelled "leader" counts too.
export function isLeaderRankName(rank: string | null | undefined): boolean {
  return (rank ?? '').toLowerCase() === 'leader';
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

// May review members' ToD submissions (ToDs tab → ToD Submissions Needing Approval): the
// CanManageTods permission the server checks, or the Leader, whom the server's CanAsync always lets
// through. isLeaderTierIn covers the admin override.
export function canReviewSubmissionsIn(
  overview: ActivityOverview | null | undefined,
  linkshellId: number | null | undefined
): boolean {
  if (linkshellId == null) return false;
  const permissions = (overview?.linkshells ?? []).find(link => link.id === linkshellId)?.permissions;
  return permissions?.canManageTods === true || isLeaderTierIn(overview, linkshellId);
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
