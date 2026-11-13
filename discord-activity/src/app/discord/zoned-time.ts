// Every date or time the app shows is written on the clock of the viewer's PROFILE time zone
// (DiscordActivityService.viewerTimeZone), never the computer's. Angular's `| date` pipe and
// Date.toLocale*String() use the computer's zone, so a screen that mixed them could show one
// event at two different times. Use the `zonedTime` pipe in templates and formatInZone in code
// (lint fails on `| date` and on toLocaleDateString/toLocaleTimeString).
//
// Building an Intl.DateTimeFormat is slow next to using one, and the app formats hundreds of
// times per second while countdowns run, so each (zone, style) formatter is built once and kept.

export type ZonedTimeStyle =
  | 'dateTime'            // Mar 1, 2026, 9:05 PM
  | 'dateTimeSeconds'     // Mar 1, 2026, 9:05:07 PM
  | 'date'                // Mar 1, 2026
  | 'short'               // 3/1/26, 9:05 PM
  | 'time'                // 9:05 PM
  | 'timeSeconds'         // 9:05:07 PM
  | 'monthDayTime'        // Mar 1, 9:05 PM
  | 'monthDayTimeSeconds' // Mar 1, 9:05:07 PM
  | 'weekdayTime';        // Sun 9:05 PM

const STYLES: Record<ZonedTimeStyle, Intl.DateTimeFormatOptions> = {
  dateTime: { dateStyle: 'medium', timeStyle: 'short' },
  dateTimeSeconds: { dateStyle: 'medium', timeStyle: 'medium' },
  date: { dateStyle: 'medium' },
  short: { dateStyle: 'short', timeStyle: 'short' },
  time: { hour: 'numeric', minute: '2-digit' },
  timeSeconds: { timeStyle: 'medium' },
  monthDayTime: { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit' },
  monthDayTimeSeconds: { month: 'short', day: 'numeric', hour: 'numeric', minute: '2-digit', second: '2-digit' },
  weekdayTime: { weekday: 'short', hour: 'numeric', minute: '2-digit' }
};

const formatters = new Map<string, Intl.DateTimeFormat>();

// The cached formatter for a zone and style. An unknown zone (a stale saved value) falls back to
// UTC rather than throwing, as the server does.
export function zonedFormatter(timeZone: string, style: ZonedTimeStyle): Intl.DateTimeFormat {
  const zone = timeZone || 'UTC';
  const key = `${zone}|${style}`;
  let formatter = formatters.get(key);
  if (!formatter) {
    try {
      formatter = new Intl.DateTimeFormat(undefined, { ...STYLES[style], timeZone: zone });
    } catch {
      formatter = new Intl.DateTimeFormat(undefined, { ...STYLES[style], timeZone: 'UTC' });
    }
    formatters.set(key, formatter);
  }
  return formatter;
}

// An instant (ISO string from the server, epoch ms, or Date) as text in `timeZone`.
// Null for a missing or unreadable value.
export function formatInZone(
  value: string | number | Date | null | undefined,
  timeZone: string,
  style: ZonedTimeStyle
): string | null {
  const ms = toEpochMs(value);
  return ms === null ? null : zonedFormatter(timeZone, style).format(ms);
}

// "YYYY-MM-DD" of the calendar day the instant falls on in `timeZone`. Used to group rows by day
// and to ask "is this today?" on the viewer's clock rather than the computer's.
export function zonedDayKey(value: string | number | Date | null | undefined, timeZone: string): string | null {
  const ms = toEpochMs(value);
  if (ms === null) {
    return null;
  }
  const key = `${timeZone || 'UTC'}|dayKey`;
  let formatter = formatters.get(key);
  if (!formatter) {
    const options: Intl.DateTimeFormatOptions = { year: 'numeric', month: '2-digit', day: '2-digit' };
    try {
      formatter = new Intl.DateTimeFormat('en-CA', { ...options, timeZone: timeZone || 'UTC' });
    } catch {
      formatter = new Intl.DateTimeFormat('en-CA', { ...options, timeZone: 'UTC' });
    }
    formatters.set(key, formatter);
  }
  const parts = formatter.formatToParts(ms);
  const get = (type: Intl.DateTimeFormatPartTypes): string => parts.find(part => part.type === type)?.value ?? '';
  return `${get('year')}-${get('month')}-${get('day')}`;
}

function toEpochMs(value: string | number | Date | null | undefined): number | null {
  if (value === null || value === undefined || value === '') {
    return null;
  }
  const ms = value instanceof Date ? value.getTime() : typeof value === 'number' ? value : new Date(value).getTime();
  return Number.isNaN(ms) ? null : ms;
}
