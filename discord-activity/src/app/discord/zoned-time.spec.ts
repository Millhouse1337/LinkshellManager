import { describe, expect, it } from 'vitest';
import { formatInZone, zonedDayKey, zonedFormatter } from './zoned-time';

// vitest.config.ts pins the computer's zone to New York; the profile zones here differ from it.
const reference = (timeZone: string, options: Intl.DateTimeFormatOptions, ms: number): string =>
  new Intl.DateTimeFormat(undefined, { ...options, timeZone }).format(ms);

describe('formatInZone', () => {
  const instant = Date.parse('2026-03-01T12:05:07Z'); // 21:05 in Tokyo, 07:05 in New York

  it('writes the time on the PROFILE zone clock, not the computer clock', () => {
    const tokyo = formatInZone('2026-03-01T12:05:07Z', 'Asia/Tokyo', 'time');
    expect(tokyo).toBe(reference('Asia/Tokyo', { hour: 'numeric', minute: '2-digit' }, instant));
    expect(tokyo).not.toBe(reference('America/New_York', { hour: 'numeric', minute: '2-digit' }, instant));
  });

  it('accepts an ISO string, epoch milliseconds or a Date alike', () => {
    const fromString = formatInZone('2026-03-01T12:05:07Z', 'Europe/London', 'dateTimeSeconds');
    expect(formatInZone(instant, 'Europe/London', 'dateTimeSeconds')).toBe(fromString);
    expect(formatInZone(new Date(instant), 'Europe/London', 'dateTimeSeconds')).toBe(fromString);
  });

  it('returns null for a missing or unreadable value', () => {
    expect(formatInZone(null, 'Asia/Tokyo', 'date')).toBeNull();
    expect(formatInZone(undefined, 'Asia/Tokyo', 'date')).toBeNull();
    expect(formatInZone('', 'Asia/Tokyo', 'date')).toBeNull();
    expect(formatInZone('not a date', 'Asia/Tokyo', 'date')).toBeNull();
  });

  it('reads an unknown zone as UTC instead of throwing', () => {
    expect(formatInZone(instant, 'Not/AZone', 'time')).toBe(formatInZone(instant, 'UTC', 'time'));
  });
});

describe('zonedFormatter', () => {
  it('builds each zone and style once', () => {
    expect(zonedFormatter('Asia/Tokyo', 'date')).toBe(zonedFormatter('Asia/Tokyo', 'date'));
    expect(zonedFormatter('Asia/Tokyo', 'date')).not.toBe(zonedFormatter('Europe/London', 'date'));
  });
});

describe('zonedDayKey', () => {
  it('names the day on the profile zone calendar', () => {
    // 20:00 UTC on 1 March is already 2 March in Tokyo, still 1 March in New York.
    expect(zonedDayKey('2026-03-01T20:00:00Z', 'Asia/Tokyo')).toBe('2026-03-02');
    expect(zonedDayKey('2026-03-01T20:00:00Z', 'America/New_York')).toBe('2026-03-01');
    expect(zonedDayKey(null, 'Asia/Tokyo')).toBeNull();
  });
});
