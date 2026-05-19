// Shared types and source-of-truth constants for ActivityHomeComponent.
//
// Extracted from `activity-home.component.ts` purely for navigability — no
// behavior change. Constants kept `as const` so derived union types stay
// narrow.

export const TAB_NAMES = [
  'dashboard',
  'profile',
  'linkshell',
  'timed-events',
  'window-events',
  'tods',
  'auctions',
  'dkp',
  'loot',
  'endgame',
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

export const TOD_COOLDOWN_OPTIONS = ['5 Min', '2 Hour', '22 Hour', '72 Hour', 'Other'] as const;
export type TodCooldownOption = typeof TOD_COOLDOWN_OPTIONS[number];

export const TOD_INTERVAL_OPTIONS = ['10 Min', '1 Hour', 'Not specified'] as const;
export type TodIntervalOption = typeof TOD_INTERVAL_OPTIONS[number];

export const LONG_WINDOW_TOD_MONSTERS: ReadonlySet<string> = new Set([
  'Tiamat',
  'Jormungand',
  'Vrtra'
]);

// True HNMs only (not Sky farm pops, ground NMs, HENMs, or Sea NMs).
// Used by the dashboard "HNM Claims" donut so the chart isn't dominated by
// unrelated kills like Genbu / Mother Globe / etc.
// Mirrors Services/HnmConfig.cs (LongWindow + Short Window + Testing) so the
// activity tabs filter the same monsters out of the generic ToD / Event
// views that the server pushes into the dedicated HNM section.
export const HNM_NAMES: ReadonlySet<string> = new Set([
  'Fafnir',
  'Nidhogg',
  'Behemoth',
  'King Behemoth',
  'Adamantoise',
  'Aspidochelone',
  'Tiamat',
  'Jormungand',
  'Vrtra',
  'Bahamut',
  // Testing presets -- mirror HnmConfig.TestingHnms.
  'Goblin Furrier',
  'Goblin Shaman'
]);

// All built-in monster names the addon's parser knows about, grouped by
// category for display. Mirrors the per-table constants in att/constants.lua
// (HNM_WINDOW_COUNTS, SKY_FARM_NMS, GROUND_NMS, HENMS, SEA_NMS_GROUPS).
// Sea Tier 1 / Pop-item names are kept ordered as a single Sea group since
// the addon also pools them in the Settings panel.
export interface TodMonsterGroup {
  label: string;
  names: readonly string[];
}

export const TOD_BUILT_IN_MONSTER_GROUPS: readonly TodMonsterGroup[] = [
  {
    label: 'HNMs',
    names: [
      'Adamantoise',
      'Aspidochelone',
      'Behemoth',
      'Fafnir',
      'Jormungand',
      'King Behemoth',
      'Nidhogg',
      'Tiamat',
      'Vrtra',
    ]
  },
  {
    label: 'Sky NMs',
    names: [
      'Brigandish Blade',
      'Byakko',
      'Despot',
      'Faust',
      'Genbu',
      'Kirin',
      'Mother Globe',
      'Olla Grande',
      'Seiryu',
      'Steam Cleaner',
      'Suzaku',
      'Ullikummi',
      'Zipacna',
    ]
  },
  {
    label: 'Sea NMs',
    names: [
      'Absolute Virtue',
      "Ix'aern (Dark Knight)",
      "Ix'aern (Dragoon)",
      "Ix'aern (Monk)",
      'Jailer of Faith',
      'Jailer of Fortitude',
      'Jailer of Hope',
      'Jailer of Justice',
      'Jailer of Love',
      'Jailer of Prudence',
      'Jailer of Temperance',
    ]
  },
  {
    label: 'HENMs',
    names: [
      'Mammet-9999',
      'Overlord Arthro',
      'Ruinous Rocs',
      'Sacred Scorpions',
      'Tonberry Sovereign',
      'Ultimega',
    ]
  },
  // "Other NMs" stays last so the canonical categories (HNMs / Sky / Sea /
  // HENMs) sit together at the top and the catch-all group settles at the
  // bottom of the picker.
  {
    label: 'Other NMs',
    names: [
      'Bloodsucker',
      'King Arthro',
      'King Vinegarroon',
      'Serket',
      'Shikigami Weapon',
      'Simurgh',
      'Xolotl',
    ]
  },
];
