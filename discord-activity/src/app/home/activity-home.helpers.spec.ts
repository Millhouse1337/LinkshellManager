import { describe, expect, it } from 'vitest';
import type { ActivityEventParticipant } from '../discord/discord-activity.service';
import type { ActivityMonsterSetup, ActivityOverview } from '../discord/discord-activity.types';
import {
  additionalSecondsLabel,
  breakSessionInfo,
  canEditLiveCampIn,
  canManageEventsIn,
  canManageLinkshellIn,
  canReviewSubmissionsIn,
  defaultDkpPoolId,
  dkpPoolOptionsFor,
  formatAlts,
  formatBreakDuration,
  formatDkp,
  formatElapsed,
  formatLocalClockTime,
  formatShortDuration,
  isLeaderTierIn,
  memberAvatarClass,
  memberInitials,
  memberStatusClass,
  mergedMonsterName,
  monsterSetupForTod,
  pairingCountdownLabel,
  parseDate,
  parseTodDurationMinutes,
  primaryLinkshellSettings,
  rankIcon,
  shiftLocalDateTime,
  todSortKey,
  todWindowState,
  zonedLocalInputToUtcMs
} from './activity-home.helpers';

// The computer's zone is New York (vitest.config.ts); profile zones below differ on purpose.

const MIN = 60_000;

function overview(partial: Record<string, unknown>): ActivityOverview {
  return partial as unknown as ActivityOverview;
}

function setup(partial: Partial<ActivityMonsterSetup>): ActivityMonsterSetup {
  return { monsterName: 'Fafnir/Nidhogg', windows: 7, cadenceMinutes: 10, cooldownMinutes: 1320, category: 'HNM', ...partial };
}

describe('shiftLocalDateTime', () => {
  it('adds seconds as plain clock arithmetic', () => {
    expect(shiftLocalDateTime('2026-03-01T13:00:00', 16)).toBe('2026-03-01T13:00:16');
    expect(shiftLocalDateTime('2026-03-01T13:00', 16)).toBe('2026-03-01T13:00:16');
    expect(shiftLocalDateTime('2026-03-01T13:00:16', -16)).toBe('2026-03-01T13:00:00');
    expect(shiftLocalDateTime('2026-12-31T23:59:50', 16)).toBe('2027-01-01T00:00:06');
  });

  it('is not bent by the COMPUTER zone springing forward', () => {
    // 02:00-02:59 on 8 Mar 2026 never happens in New York (the computer), but the form value is
    // the PROFILE zone's wall clock, so it must still just move on by 20 seconds.
    expect(shiftLocalDateTime('2026-03-08T01:59:50', 20)).toBe('2026-03-08T02:00:10');
  });

  it('is not bent by the COMPUTER zone falling back', () => {
    // 01:00-01:59 on 1 Nov 2026 happens twice in New York; an hour later is still 02:30.
    expect(shiftLocalDateTime('2026-11-01T01:30:00', 3600)).toBe('2026-11-01T02:30:00');
  });

  it('returns an unreadable value unchanged', () => {
    expect(shiftLocalDateTime('soon', 16)).toBe('soon');
  });
});

describe('formatLocalClockTime', () => {
  it('shows the wall clock as written, whatever the computer zone', () => {
    // Formatting text depends on the locale, so only the digits are checked.
    expect(formatLocalClockTime('2026-03-08T02:30:16')).toMatch(/2:30:16/);
    expect(formatLocalClockTime('later')).toBe('later');
  });
});

describe('zonedLocalInputToUtcMs', () => {
  it('reads the box in the PROFILE zone, not the computer zone', () => {
    expect(zonedLocalInputToUtcMs('2026-03-01T21:00', 'Asia/Tokyo')).toBe(Date.UTC(2026, 2, 1, 12, 0, 0));
    expect(zonedLocalInputToUtcMs('2026-03-01T21:00:15', 'Asia/Tokyo')).toBe(Date.UTC(2026, 2, 1, 12, 0, 15));
  });

  it('uses the profile zone offset on each side of its own daylight-saving change', () => {
    expect(zonedLocalInputToUtcMs('2026-03-28T12:00', 'Europe/London')).toBe(Date.UTC(2026, 2, 28, 12));
    expect(zonedLocalInputToUtcMs('2026-03-30T12:00', 'Europe/London')).toBe(Date.UTC(2026, 2, 30, 11));
  });

  it('moves a time that never happens (clocks go forward) on by the gap, like the server', () => {
    // 01:30 on 29 Mar 2026 does not exist in London; the server reads it as 02:30 BST = 01:30 UTC.
    expect(zonedLocalInputToUtcMs('2026-03-29T01:30', 'Europe/London')).toBe(Date.UTC(2026, 2, 29, 1, 30));
  });

  it('takes the first of a time that happens twice (clocks go back), like the server', () => {
    // 01:30 on 25 Oct 2026 happens twice in London; the first is 01:30 BST = 00:30 UTC.
    expect(zonedLocalInputToUtcMs('2026-10-25T01:30', 'Europe/London')).toBe(Date.UTC(2026, 9, 25, 0, 30));
  });

  it('reads an unknown zone as UTC and rejects unreadable text', () => {
    expect(zonedLocalInputToUtcMs('2026-03-01T21:00', 'Not/AZone')).toBe(Date.UTC(2026, 2, 1, 21));
    expect(zonedLocalInputToUtcMs('2026-03-01 21:00', 'Asia/Tokyo')).toBeNull();
    expect(zonedLocalInputToUtcMs(null, 'Asia/Tokyo')).toBeNull();
  });
});

describe('parseDate / todSortKey', () => {
  it('parses ISO text and rejects junk', () => {
    expect(parseDate('2026-03-01T12:00:00Z')).toBe(Date.UTC(2026, 2, 1, 12));
    expect(parseDate('junk')).toBeNull();
    expect(parseDate(null)).toBeNull();
  });

  it('sorts a not-entered ToD by when it was written', () => {
    expect(todSortKey({ time: '2026-03-01T12:00:00Z', timeStamp: '2026-03-02T00:00:00Z' })).toBe(Date.UTC(2026, 2, 1, 12));
    expect(todSortKey({ time: null, timeStamp: '2026-03-02T00:00:00Z' })).toBe(Date.UTC(2026, 2, 2));
    expect(todSortKey({})).toBe(0);
  });
});

describe('durations', () => {
  it('formatBreakDuration', () => {
    expect(formatBreakDuration(0)).toBe('0s');
    expect(formatBreakDuration(Number.NaN)).toBe('0s');
    expect(formatBreakDuration(59_000)).toBe('59s');
    expect(formatBreakDuration(61_000)).toBe('1m 1s');
    expect(formatBreakDuration(3_660_000)).toBe('1h 1m');
  });

  it('formatElapsed', () => {
    expect(formatElapsed(3_723_000)).toBe('01:02:03');
    expect(formatElapsed(-5)).toBe('00:00:00');
  });

  it('formatShortDuration', () => {
    expect(formatShortDuration(45 * MIN)).toBe('45m');
    expect(formatShortDuration(22 * 60 * MIN)).toBe('22h');
    expect(formatShortDuration(90 * MIN)).toBe('1h 30m');
    expect(formatShortDuration(-MIN)).toBe('0m');
  });

  it('additionalSecondsLabel', () => {
    expect(additionalSecondsLabel(1)).toBe('1 additional second');
    expect(additionalSecondsLabel(16)).toBe('16 additional seconds');
  });

  it('parseTodDurationMinutes reads stored labels', () => {
    expect(parseTodDurationMinutes('22 Hour', 'hours')).toBe(1320);
    expect(parseTodDurationMinutes('1 Hour 30 Min', 'hours')).toBe(90);
    expect(parseTodDurationMinutes('45 Min', 'mins')).toBe(45);
    // A bare number is hours for a cooldown and minutes for an interval.
    expect(parseTodDurationMinutes('21', 'hours')).toBe(1260);
    expect(parseTodDurationMinutes('10', 'mins')).toBe(10);
    expect(parseTodDurationMinutes('0', 'mins')).toBeNull();
    expect(parseTodDurationMinutes('', 'mins')).toBeNull();
    expect(parseTodDurationMinutes('soon', 'mins')).toBeNull();
  });
});

describe('breakSessionInfo', () => {
  const participant = {
    statusLedger: [
      { id: 4, actionType: 'BreakReturn', occurredAt: '2026-03-01T11:30:00Z', requiresVerification: false },
      { id: 1, actionType: 'BreakStart', occurredAt: '2026-03-01T10:00:00Z', requiresVerification: false },
      { id: 3, actionType: 'BreakStart', occurredAt: '2026-03-01T11:00:00Z', requiresVerification: false },
      { id: 2, actionType: 'BreakReturn', occurredAt: '2026-03-01T10:05:00Z', requiresVerification: false }
    ]
  } as unknown as ActivityEventParticipant;

  it('numbers the break and measures it, whatever order the ledger arrives in', () => {
    expect(breakSessionInfo(participant, 2)).toEqual({ sessionNumber: 1, durationMs: 5 * MIN });
    expect(breakSessionInfo(participant, 4)).toEqual({ sessionNumber: 2, durationMs: 30 * MIN });
    expect(breakSessionInfo(participant, 99)).toBeNull();
  });
});

describe('merge pairs and setups', () => {
  it('mergedMonsterName groups both halves under one name', () => {
    expect(mergedMonsterName('nidhogg')).toBe('Fafnir/Nidhogg');
    expect(mergedMonsterName(' King Behemoth ')).toBe('Behemoth/King Behemoth');
    expect(mergedMonsterName('Adamantoise/Aspidochelone')).toBe('Adamantoise/Aspidochelone');
    expect(mergedMonsterName('Kirin')).toBe('Kirin');
    expect(mergedMonsterName(null)).toBe('');
  });

  it('monsterSetupForTod finds the merged setup for either half', () => {
    const setups = [setup({}), setup({ monsterName: 'Kirin', windows: null, cadenceMinutes: null })];
    expect(monsterSetupForTod(setups, 'Nidhogg')?.monsterName).toBe('Fafnir/Nidhogg');
    expect(monsterSetupForTod(setups, 'kirin')?.monsterName).toBe('Kirin');
    expect(monsterSetupForTod(setups, 'Tiamat')).toBeNull();
    expect(monsterSetupForTod(null, 'Fafnir')).toBeNull();
  });
});

describe('todWindowState', () => {
  const repop = Date.UTC(2026, 2, 1, 12);
  const tod = { monsterName: 'Fafnir', time: '2026-02-28T14:00:00Z', repopTime: '2026-03-01T12:00:00Z', interval: '10 Min' };

  it('counts down, opens, then passes the last window', () => {
    // 7 windows 10 minutes apart: the last opens 60 minutes after the repop.
    expect(todWindowState(tod, setup({}), repop - MIN)).toMatchObject({ tone: 'pending', repopMs: repop, closeMs: repop + 60 * MIN });
    expect(todWindowState(tod, setup({}), repop + 30 * MIN).tone).toBe('open');
    expect(todWindowState(tod, setup({}), repop + 60 * MIN).tone).toBe('up');
  });

  it('falls back to the default 7 windows and the setup cadence', () => {
    const state = todWindowState({ ...tod, interval: null }, setup({ windows: null, cadenceMinutes: 10 }), repop);
    expect(state.closeMs).toBe(repop + 60 * MIN);
  });

  it('has no close for a monster without a window grid', () => {
    const state = todWindowState({ ...tod, monsterName: 'Kirin' }, null, repop + MIN);
    expect(state).toMatchObject({ tone: 'up', closeMs: null });
  });

  it('is unknown when no repop was recorded', () => {
    expect(todWindowState({ ...tod, repopTime: null }, setup({}), repop).tone).toBe('unknown');
  });
});

describe('small formatters', () => {
  it('formatDkp', () => {
    expect(formatDkp(90 * MIN, 2)).toBe('3.00');
    expect(formatDkp(90 * MIN, null)).toBe('0.00');
  });

  it('formatAlts', () => {
    expect(formatAlts('Alpha', ' ')).toBe('Alpha');
    expect(formatAlts('Alpha', 'Beta')).toBe('Alpha, Beta');
    expect(formatAlts(null, undefined)).toBe('');
  });

  it('rankIcon', () => {
    expect(rankIcon('Leader')).toBe('👑');
    expect(rankIcon('officer')).toBe('⭐');
    expect(rankIcon('Trial')).toBe('🌱');
    expect(rankIcon('Custom Role')).toBe('🛡️');
  });

  it('memberInitials', () => {
    expect(memberInitials('Millhouse')).toBe('MI');
    expect(memberInitials('john smith')).toBe('JS');
    expect(memberInitials('  ')).toBe('??');
  });

  it('memberAvatarClass is stable for a name', () => {
    expect(memberAvatarClass('Millhouse')).toBe(memberAvatarClass(' Millhouse '));
    expect(['a', 'b', 'c', 'd', 'e']).toContain(memberAvatarClass('Millhouse'));
    expect(memberAvatarClass('')).toBe('a');
  });

  it('memberStatusClass', () => {
    expect(memberStatusClass(null)).toBe('success');
    expect(memberStatusClass('Pending')).toBe('warning');
    expect(memberStatusClass('inactive')).toBe('danger');
    expect(memberStatusClass('Banned')).toBe('default');
  });
});

describe('permission gates', () => {
  const ov = overview({
    appUser: { primaryLinkshellId: 2 },
    linkshells: [
      { id: 1, rank: 'Member', permissions: { canModerateLiveEvent: true } },
      { id: 2, rank: 'Officer', permissions: {}, settings: { marker: 'primary' } },
      { id: 3, rank: 'leader', permissions: {} },
      { id: 4, rank: 'Member', permissions: { canManageEvents: true, canManageTods: true } }
    ]
  });

  it('primaryLinkshellSettings picks the primary linkshell', () => {
    expect(primaryLinkshellSettings(ov)).toEqual({ marker: 'primary' });
    expect(primaryLinkshellSettings(null)).toBeNull();
  });

  it('canManageLinkshellIn goes by rank, and only inside linkshells the user belongs to', () => {
    expect(canManageLinkshellIn(ov, 1)).toBe(false);
    expect(canManageLinkshellIn(ov, 2)).toBe(true);
    expect(canManageLinkshellIn(ov, 3)).toBe(true);
    expect(canManageLinkshellIn(ov, 99)).toBe(false);
    expect(canManageLinkshellIn(ov, null)).toBe(false);

    const admin = overview({ ...ov, adminOverrideActive: true });
    expect(canManageLinkshellIn(admin, 1)).toBe(true);
    expect(canManageLinkshellIn(admin, 99)).toBe(false);
  });

  it('canManageEventsIn goes by the permission, not the rank', () => {
    expect(canManageEventsIn(ov, 2)).toBe(false);
    expect(canManageEventsIn(ov, 4)).toBe(true);
    expect(canManageEventsIn(ov, 99)).toBe(false);
  });

  it('canEditLiveCampIn: Leader, Moderate live events or Manage events', () => {
    expect(canEditLiveCampIn(ov, 1)).toBe(true);
    expect(canEditLiveCampIn(ov, 2)).toBe(false);
    expect(canEditLiveCampIn(ov, 3)).toBe(true);
    expect(canEditLiveCampIn(ov, 4)).toBe(true);
    expect(canEditLiveCampIn(ov, 99)).toBe(false);
  });

  it('isLeaderTierIn and canReviewSubmissionsIn', () => {
    expect(isLeaderTierIn(ov, 3)).toBe(true);
    expect(isLeaderTierIn(ov, 2)).toBe(false);
    expect(canReviewSubmissionsIn(ov, 4)).toBe(true);
    expect(canReviewSubmissionsIn(ov, 3)).toBe(true);
    expect(canReviewSubmissionsIn(ov, 2)).toBe(false);
  });
});

describe('DKP pool picker', () => {
  const pools = [
    { id: 11, name: 'Sky', isDefault: false },
    { id: 10, name: 'General', isDefault: true }
  ];

  it('lists the primary linkshell pools when there is more than one', () => {
    const view = overview({ primaryLinkshell: { id: 5, dkpPools: pools } });
    expect(dkpPoolOptionsFor(view, 5).map(pool => pool.id)).toEqual([11, 10]);
  });

  it('hides the picker for a single pool, a missing field, or another linkshell', () => {
    expect(dkpPoolOptionsFor(overview({ primaryLinkshell: { id: 5, dkpPools: [pools[1]] } }), 5)).toEqual([]);
    expect(dkpPoolOptionsFor(overview({ primaryLinkshell: { id: 5 } }), 5)).toEqual([]);
    expect(dkpPoolOptionsFor(overview({ primaryLinkshell: { id: 5, dkpPools: pools } }), 6)).toEqual([]);
    expect(dkpPoolOptionsFor(null, 5)).toEqual([]);
  });

  it('starts on the default pool, or null when there is no picker', () => {
    expect(defaultDkpPoolId(pools)).toBe(10);
    expect(defaultDkpPoolId([])).toBeNull();
  });
});

describe('pairingCountdownLabel', () => {
  it('counts down minutes and seconds to the expiry', () => {
    const expires = 1_000_000 + 10 * 60_000;
    expect(pairingCountdownLabel(expires, 1_000_000)).toBe('10:00');
    expect(pairingCountdownLabel(expires, 1_000_000 + 61_000)).toBe('8:59');
  });

  it('says expired once the time has passed', () => {
    expect(pairingCountdownLabel(5_000, 5_000)).toBe('expired');
    expect(pairingCountdownLabel(5_000, 9_000)).toBe('expired');
  });
});
