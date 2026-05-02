// Pure helpers extracted from `activity-sidebar-panel.component.ts`.
// These functions have zero `this` references and are not bound from the
// component templates, so they live here to keep the component class lean.

const CURATED_TIME_ZONES = [
  'UTC',
  'America/New_York',
  'America/Chicago',
  'America/Denver',
  'America/Los_Angeles',
  'America/Phoenix',
  'America/Anchorage',
  'Pacific/Honolulu',
  'America/Toronto',
  'America/Vancouver',
  'America/Mexico_City',
  'America/Sao_Paulo',
  'America/Argentina/Buenos_Aires',
  'Europe/London',
  'Europe/Dublin',
  'Europe/Paris',
  'Europe/Berlin',
  'Europe/Madrid',
  'Europe/Rome',
  'Europe/Warsaw',
  'Europe/Helsinki',
  'Europe/Athens',
  'Europe/Istanbul',
  'Europe/Kyiv',
  'Africa/Johannesburg',
  'Asia/Dubai',
  'Asia/Kolkata',
  'Asia/Dhaka',
  'Asia/Bangkok',
  'Asia/Singapore',
  'Asia/Manila',
  'Asia/Hong_Kong',
  'Asia/Taipei',
  'Asia/Seoul',
  'Asia/Tokyo',
  'Australia/Perth',
  'Australia/Adelaide',
  'Australia/Sydney',
  'Pacific/Auckland'
] as const;

export const curatedTimeZones = CURATED_TIME_ZONES;

// Canonical event-type ordering for the DKP "earned by event type"
// breakdown. Anything not in this list (e.g. blank entries that get
// bucketed as "Unspecified", or future custom types) sorts to the bottom
// in alphabetical order.
export const DKP_EVENT_TYPE_ORDER: readonly string[] = [
  'Sky', 'Sea', 'Dynamis', 'Limbus',
  'HNM', 'HENM', 'NM', 'BCNM', 'KSNM',
  'Other'
];

export function resolveBrowserTimeZone(): string {
  try {
    return Intl.DateTimeFormat().resolvedOptions().timeZone || 'UTC';
  } catch {
    return 'UTC';
  }
}

export function resolveTimeZoneOptions(currentProfileTimeZone: string | null | undefined, browserTimeZone: string): string[] {
  const intlWithSupportedValues = Intl as typeof Intl & {
    supportedValuesOf?: (key: string) => string[];
  };

  const seedValues = [
    currentProfileTimeZone,
    browserTimeZone,
    ...CURATED_TIME_ZONES
  ].filter((value): value is string => Boolean(value && value.trim().length > 0));

  if (typeof intlWithSupportedValues.supportedValuesOf === 'function') {
    return Array.from(
      new Set([
        ...seedValues,
        ...intlWithSupportedValues.supportedValuesOf('timeZone')
      ])
    );
  }

  return Array.from(new Set(seedValues));
}

export function parseDate(value?: string | null): number | null {
  if (!value) {
    return null;
  }

  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? null : parsed.getTime();
}

export function formatElapsed(totalMilliseconds: number): string {
  const totalSeconds = Math.max(0, Math.floor(totalMilliseconds / 1000));
  const hours = Math.floor(totalSeconds / 3600);
  const minutes = Math.floor((totalSeconds % 3600) / 60);
  const seconds = totalSeconds % 60;

  return `${hours.toString().padStart(2, '0')}:${minutes.toString().padStart(2, '0')}:${seconds.toString().padStart(2, '0')}`;
}

export function roleBadgeClass(rank?: string | null): string {
  switch ((rank ?? 'Member').toLowerCase()) {
    case 'leader':
      return 'role-pill role-pill--leader';
    case 'officer':
      return 'role-pill role-pill--officer';
    default:
      return 'role-pill role-pill--member';
  }
}

export function auctionRowSpan(auction: { items: { id: number }[] }): number {
  return Math.max(1, auction.items.length);
}
