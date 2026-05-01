// Shared types and source-of-truth constants for ActivityHomeComponent.
//
// Extracted from `activity-home.component.ts` purely for navigability — no
// behavior change. Constants kept `as const` so derived union types stay
// narrow.

export const TAB_NAMES = [
  'dashboard',
  'profile',
  'linkshell',
  'events',
  'tods',
  'auctions',
  'dkp',
  'endgame',
  'hnm',
  'missions',
  'messages',
  'configurations'
] as const;

export type TabName = typeof TAB_NAMES[number];

export const TOD_MONSTER_OPTIONS = [
  'Fafnir',
  'Nidhogg',
  'Behemoth',
  'King Behemoth',
  'Adamantoise',
  'Aspidochelone',
  'Tiamat',
  'Jormungand',
  'Vrtra',
  'King Arthro',
  'Simurgh',
  'Other'
] as const;

export type TodMonsterOption = typeof TOD_MONSTER_OPTIONS[number];

export const TOD_COOLDOWN_OPTIONS = ['22 Hour', '72 Hour', 'Other'] as const;
export type TodCooldownOption = typeof TOD_COOLDOWN_OPTIONS[number];

export const TOD_INTERVAL_OPTIONS = ['10 Min', '1 Hour', 'Not specified'] as const;
export type TodIntervalOption = typeof TOD_INTERVAL_OPTIONS[number];

export const LONG_WINDOW_TOD_MONSTERS: ReadonlySet<string> = new Set([
  'Tiamat',
  'Jormungand',
  'Vrtra'
]);
