// Pure helpers behind DateTimeInputComponent: split a datetime-local value ("2026-11-05T07:28:29")
// into the boxes a touch device shows, and put the boxes back together.

export interface DateTimeParts {
  date: string;    // "yyyy-MM-dd", or '' when blank
  hour: string;    // "1".."12" as typed, 12-hour clock
  minute: string;
  second: string;
  pm: boolean;
}

const VALUE = /^(\d{4}-\d{2}-\d{2})T(\d{2}):(\d{2})(?::(\d{2}))?/;

const pad = (n: number): string => String(n).padStart(2, '0');

// A blank or unreadable value gives blank boxes (AM).
export function splitLocalDateTime(value: string | null | undefined): DateTimeParts {
  const match = VALUE.exec(value ?? '');
  if (!match) {
    return { date: '', hour: '', minute: '', second: '', pm: false };
  }
  const hour24 = Number(match[2]);
  return {
    date: match[1],
    hour: pad(hour24 % 12 === 0 ? 12 : hour24 % 12),
    minute: match[3],
    second: match[4] ?? '00',
    pm: hour24 >= 12,
  };
}

// '' when the date is blank (the field was cleared). null when the time can't be read yet -- no
// hour, or a number out of range -- so the caller keeps the last good value while someone types.
// A blank minute or second counts as 00.
export function joinLocalDateTime(parts: DateTimeParts): string | null {
  if (!parts.date) {
    return '';
  }
  const hour = readNumber(parts.hour, 1, 12);
  const minute = parts.minute === '' ? 0 : readNumber(parts.minute, 0, 59);
  const second = parts.second === '' ? 0 : readNumber(parts.second, 0, 59);
  if (hour === null || minute === null || second === null) {
    return null;
  }
  const hour24 = (hour % 12) + (parts.pm ? 12 : 0);
  return `${parts.date}T${pad(hour24)}:${pad(minute)}:${pad(second)}`;
}

function readNumber(text: string, min: number, max: number): number | null {
  if (!/^\d{1,2}$/.test(text)) {
    return null;
  }
  const n = Number(text);
  return n >= min && n <= max ? n : null;
}

// The phone/tablet browsers whose own date-time picker has no seconds: iOS and iPadOS (an iPad
// says "Macintosh" but has a touch screen), Android, and any other touch-only screen. A PC keeps
// the browser's picker, which shows seconds.
export function needsSecondsBoxes(): boolean {
  try {
    const ua = navigator.userAgent;
    const ios = /iPhone|iPad|iPod/i.test(ua) || (/Macintosh/i.test(ua) && navigator.maxTouchPoints > 1);
    const android = /Android/i.test(ua);
    const touchOnly = window.matchMedia?.('(pointer: coarse) and (hover: none)').matches ?? false;
    return ios || android || touchOnly;
  } catch {
    return false;
  }
}
