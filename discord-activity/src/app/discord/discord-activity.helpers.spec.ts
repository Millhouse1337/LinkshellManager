import { describe, expect, it, vi } from 'vitest';
import {
  DISCORD_SIGN_IN_CANCELLED_MESSAGE,
  datetimeLocalToUtcIso,
  formatActionError,
  formatError,
  isDiscordRpcError,
  tagDiscordCommand,
  toLocalInputValueInZone,
  withTimeout
} from './discord-activity.helpers';

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
