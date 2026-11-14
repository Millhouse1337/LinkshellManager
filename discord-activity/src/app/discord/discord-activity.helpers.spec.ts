import { describe, expect, it, vi } from 'vitest';
import {
  DISCORD_SIGN_IN_CANCELLED_MESSAGE,
  currentLinkshellIdOf,
  datetimeLocalToUtcIso,
  dkpStepFor,
  dkpStepMessage,
  dkpStepOf,
  formatActionError,
  formatError,
  isDiscordRpcError,
  isOnDkpStep,
  offDkpStepMessage,
  tagDiscordCommand,
  toLocalInputValueInZone,
  withTimeout
} from './discord-activity.helpers';
import type { ActivityOverview } from './discord-activity.types';

// vitest.config.ts pins the computer's zone to New York. Every test here uses a PROFILE zone
// that differs from it, which is the case that used to go wrong.
describe('test setup', () => {
  it('runs with the computer zone set to New York', () => {
    expect(Intl.DateTimeFormat().resolvedOptions().timeZone).toBe('America/New_York');
  });
});

describe('toLocalInputValueInZone (DiscordActivityService.toViewerLocalInputValue)', () => {
  it('writes the instant on the PROFILE zone clock, not the computer clock', () => {
    // 12:00 UTC is 21:00 in Tokyo and 07:00 in New York (the computer).
    expect(toLocalInputValueInZone('2026-03-01T12:00:00Z', 'Asia/Tokyo')).toBe('2026-03-01T21:00');
  });

  it('keeps seconds only when asked', () => {
    expect(toLocalInputValueInZone('2026-03-01T12:34:56Z', 'Asia/Tokyo')).toBe('2026-03-01T21:34');
    expect(toLocalInputValueInZone('2026-03-01T12:34:56Z', 'Asia/Tokyo', true)).toBe('2026-03-01T21:34:56');
  });

  it('writes midnight as 00, never 24, and rolls the date', () => {
    expect(toLocalInputValueInZone('2026-03-01T15:00:00Z', 'Asia/Tokyo', true)).toBe('2026-03-02T00:00:00');
  });

  it('follows the profile zone across ITS daylight-saving change, not the computer one', () => {
    // London springs forward on 29 Mar 2026 at 01:00 UTC; New York already did on 8 Mar.
    expect(toLocalInputValueInZone('2026-03-15T12:00:00Z', 'Europe/London')).toBe('2026-03-15T12:00');
    expect(toLocalInputValueInZone('2026-03-29T00:30:00Z', 'Europe/London')).toBe('2026-03-29T00:30');
    expect(toLocalInputValueInZone('2026-03-29T01:30:00Z', 'Europe/London')).toBe('2026-03-29T02:30');
  });

  it('crosses the New York change when New York is the profile zone too', () => {
    expect(toLocalInputValueInZone('2026-03-08T06:59:59Z', 'America/New_York', true)).toBe('2026-03-08T01:59:59');
    expect(toLocalInputValueInZone('2026-03-08T07:00:00Z', 'America/New_York', true)).toBe('2026-03-08T03:00:00');
  });

  it('returns an empty string for a missing or unreadable value', () => {
    expect(toLocalInputValueInZone(null, 'Asia/Tokyo')).toBe('');
    expect(toLocalInputValueInZone(undefined, 'Asia/Tokyo')).toBe('');
    expect(toLocalInputValueInZone('', 'Asia/Tokyo')).toBe('');
    expect(toLocalInputValueInZone('not a date', 'Asia/Tokyo')).toBe('');
  });
});

describe('datetimeLocalToUtcIso', () => {
  it('passes a datetime-local value through unchanged, so the server reads it in the profile zone', () => {
    expect(datetimeLocalToUtcIso('2026-03-01T21:00')).toBe('2026-03-01T21:00');
    expect(datetimeLocalToUtcIso(' 2026-03-01T21:00:15 ')).toBe('2026-03-01T21:00:15');
  });

  it('rejects empty and malformed values', () => {
    expect(datetimeLocalToUtcIso(null)).toBeNull();
    expect(datetimeLocalToUtcIso('   ')).toBeNull();
    expect(datetimeLocalToUtcIso('2026-03-01T21:00:00Z')).toBeNull();
    expect(datetimeLocalToUtcIso('03/01/2026 9:00 PM')).toBeNull();
  });
});

describe('formatError', () => {
  it('shows an Error (our own HTTP client, a server refusal) as just its message', () => {
    expect(formatError(new Error('You do not have permission to edit this table.')))
      .toBe('You do not have permission to edit this table.');
    expect(isDiscordRpcError(new Error('x'))).toBe(false);
  });

  it('never calls an Error a Discord failure, even one carrying a code', () => {
    const sdkError = Object.assign(new Error('Socket closed'), { code: 1006 });
    expect(formatError(sdkError)).toBe('Socket closed');
    expect(formatError(new Error('Authorization header missing'))).toBe('Authorization header missing');
  });

  it('describes a rejected Discord command (a plain object with a numeric code)', () => {
    const error = { cmd: 'SUBSCRIBE', data: { code: 4006, message: 'Not allowed' } };
    expect(formatError(error)).toBe('Discord subscribe failed (4006): Not allowed');
    expect(formatError({ code: 4000, message: 'Bad payload' })).toBe('A Discord request failed (4000): Bad payload');
  });

  it('does not treat an object without a numeric code as a Discord error', () => {
    expect(isDiscordRpcError({ cmd: 'SUBSCRIBE', message: 'Nope' })).toBe(false);
    expect(formatError({ message: 'Lost' })).toBe('An unknown error occurred while loading LinkshellManager.');
  });

  it('shows members a friendly message for a cancelled sign-in and keeps the setup hint in the console', () => {
    const info = vi.spyOn(console, 'info').mockImplementation(() => undefined);
    try {
      const rejection = tagDiscordCommand({ code: 5000, message: 'User cancelled' }, 'AUTHORIZE');
      expect(formatError(rejection)).toBe(DISCORD_SIGN_IN_CANCELLED_MESSAGE);
      expect(DISCORD_SIGN_IN_CANCELLED_MESSAGE).not.toContain('Developer Portal');
      expect(String(info.mock.calls[0]?.[0])).toContain('Developer Portal');
    } finally {
      info.mockRestore();
    }
  });

  it('leaves an Error untouched when tagging a command', () => {
    const error = new Error('x');
    expect(tagDiscordCommand(error, 'AUTHORIZE')).toBe(error);
  });

  it('falls back to a generic message for anything else', () => {
    const generic = 'An unknown error occurred while loading LinkshellManager.';
    expect(formatError(undefined)).toBe(generic);
    expect(formatError('boom')).toBe(generic);
    expect(formatError(new Error(''))).toBe(generic);
  });
});

describe('formatActionError', () => {
  it('uses the error message, else the fallback', () => {
    expect(formatActionError(new Error('Server said no'), 'Save failed.')).toBe('Server said no');
    expect(formatActionError({ message: 'not an Error' }, 'Save failed.')).toBe('Save failed.');
  });
});

describe('withTimeout', () => {
  it('returns the result when the promise wins', async () => {
    await expect(withTimeout(Promise.resolve(7), 1000, 'too slow')).resolves.toBe(7);
  });

  it('rejects with the message when the timer wins', async () => {
    const never = new Promise<number>(() => undefined);
    await expect(withTimeout(never, 5, 'too slow')).rejects.toThrow('too slow');
  });
});

describe('currentLinkshellIdOf', () => {
  const overview = (partial: Record<string, unknown>) => partial as unknown as ActivityOverview;

  it('is the linkshell the server resolved, not the raw saved id', () => {
    // The saved id (7) is a linkshell the user has left; the server fell back to 3.
    expect(currentLinkshellIdOf(overview({ appUser: { primaryLinkshellId: 7 }, primaryLinkshell: { id: 3 } }))).toBe(3);
  });

  it('is 0 with no linkshell or no overview', () => {
    expect(currentLinkshellIdOf(overview({ appUser: { primaryLinkshellId: 7 }, primaryLinkshell: null }))).toBe(0);
    expect(currentLinkshellIdOf(null)).toBe(0);
  });
});

describe('DKP rounding grid', () => {
  it('maps the Configurations setting to a step', () => {
    expect(dkpStepFor('Quarter')).toBe(0.25);
    expect(dkpStepFor('Half')).toBe(0.5);
    expect(dkpStepFor('half')).toBe(0.5);
    // Anything else is the default, Quarter (as DkpRounding.StepFor on the server).
    expect(dkpStepFor(null)).toBe(0.25);
    expect(dkpStepFor('')).toBe(0.25);
  });

  it('reads one linkshell\'s step from the overview', () => {
    const overview = {
      linkshells: [
        { id: 1, settings: { dkpRoundingIncrement: 'Quarter' } },
        { id: 2, settings: { dkpRoundingIncrement: 'Half' } }
      ]
    } as unknown as ActivityOverview;
    expect(dkpStepOf(overview, 1)).toBe(0.25);
    expect(dkpStepOf(overview, 2)).toBe(0.5);
    expect(dkpStepOf(overview, 99)).toBe(0.25);
    expect(dkpStepOf(null, 2)).toBe(0.25);
  });

  it('accepts amounts on the Quarter grid and refuses the rest', () => {
    for (const value of [0, 0.25, 0.5, 0.75, 1, 2.25, 10.75, 100]) {
      expect(isOnDkpStep(value, 0.25)).toBe(true);
    }
    expect(isOnDkpStep(2.3, 0.25)).toBe(false);
    expect(isOnDkpStep(0.1, 0.25)).toBe(false);
    expect(isOnDkpStep(1.05, 0.25)).toBe(false);
  });

  it('accepts amounts on the Half grid and refuses quarters', () => {
    expect(isOnDkpStep(0.5, 0.5)).toBe(true);
    expect(isOnDkpStep(3, 0.5)).toBe(true);
    expect(isOnDkpStep(2.25, 0.25)).toBe(true);
    expect(isOnDkpStep(2.25, 0.5)).toBe(false);
    expect(isOnDkpStep(0.75, 0.5)).toBe(false);
  });

  it('allows negative amounts on the grid', () => {
    expect(isOnDkpStep(-5, 0.25)).toBe(true);
    expect(isOnDkpStep(-0.25, 0.25)).toBe(true);
    expect(isOnDkpStep(-1.5, 0.5)).toBe(true);
    expect(isOnDkpStep(-0.25, 0.5)).toBe(false);
    expect(isOnDkpStep(-2.3, 0.25)).toBe(false);
  });

  it('ignores floating-point noise', () => {
    // 0.7 + 0.05 is 0.7499999999999999, and 0.1 * 3 is 0.30000000000000004.
    expect(isOnDkpStep(0.7 + 0.05, 0.25)).toBe(true);
    expect(isOnDkpStep(0.1 * 5, 0.5)).toBe(true);
    expect(isOnDkpStep(0.1 + 0.2, 0.25)).toBe(false);
    expect(isOnDkpStep(0.1 * 3 + 0.45, 0.25)).toBe(true);
  });

  it('refuses values that are not numbers', () => {
    expect(isOnDkpStep(Number.NaN, 0.25)).toBe(false);
    expect(isOnDkpStep(Number.POSITIVE_INFINITY, 0.25)).toBe(false);
  });

  it('builds the message from the step', () => {
    expect(dkpStepMessage(0.25)).toBe('DKP must be in steps of 0.25 (set in Configurations).');
    expect(dkpStepMessage(0.5)).toBe('DKP must be in steps of 0.5 (set in Configurations).');
  });

  it('returns the message only when a value is off the grid', () => {
    expect(offDkpStepMessage(0.25, [1, 2.25, -0.75])).toBeNull();
    expect(offDkpStepMessage(0.25, [1, 2.3])).toBe(dkpStepMessage(0.25));
    expect(offDkpStepMessage(0.5, [2.25])).toBe(dkpStepMessage(0.5));
    // Typed into a text box: strings count the same as numbers.
    expect(offDkpStepMessage(0.5, ['1.5'])).toBeNull();
    expect(offDkpStepMessage(0.5, ['1.25'])).toBe(dkpStepMessage(0.5));
  });

  it('skips blank and non-numeric values (other checks own those)', () => {
    expect(offDkpStepMessage(0.25, [null, undefined, '', '  ', 'abc'])).toBeNull();
    expect(offDkpStepMessage(0.25, [])).toBeNull();
  });
});
