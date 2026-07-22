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
  'party-setup',
  'auctions',
  'dkp',
  'dkp-sheet',
  'loot',
  'endgame',
  'missions',
  'messages',
  'configurations',
  'permissions',
  'addon'
] as const;

export type TabName = typeof TAB_NAMES[number];

// Full curated ToD monster list — mirrors TodManagerViewModel.SupportedMonsters
// (the web Add ToD picker) plus the "Other" free-text sentinel, so the Activity
// Log ToD form offers the same monsters as the web app.
export const TOD_MONSTER_OPTIONS = [
  'Adamantoise',
  'Aspidochelone',
  'Behemoth',
  'Fafnir',
  'Jormungand',
  'King Behemoth',
  'Nidhogg',
  'Tiamat',
  'Vrtra',
  'Bloodsucker',
  'King Arthro',
  'King Vinegarroon',
  'Serket',
  'Shikigami Weapon',
  'Simurgh',
  'Xolotl',
  'Other'
] as const;

export type TodMonsterOption = typeof TOD_MONSTER_OPTIONS[number];

// HNM base -> stronger merge pairs (mirrors HnmConfig.MonsterMergePairs). On the
// create-event monster dropdown the two are offered as ONE entry: the base name below
// HNM_COMBINED_FROM_DAY, the combined "Base/Stronger" name at/above it. The chosen text is
// stored verbatim in Event.AssignedMonsterName (the server splits it on '/' for lookups).
export const HNM_MERGE_PAIRS: ReadonlyArray<{ base: string; stronger: string }> = [
  { base: 'Adamantoise', stronger: 'Aspidochelone' },
  { base: 'Behemoth', stronger: 'King Behemoth' },
  { base: 'Fafnir', stronger: 'Nidhogg' }
];

// From this day number onward, the SIGN-UP BOARD prints the combined "Base/Stronger" name;
// below it, only the base half (mirrors HnmConfig.CombinedFromDay). The board render is
// server-side — this is documented here for parity, not used client-side.
export const HNM_COMBINED_FROM_DAY = 4;

// Build the create-event monster dropdown options: each merge pair as ONE combined
// "Base/Stronger" entry (always), the stronger half dropped, other monsters intact.
// Mirrors HnmConfig.CombinedMonsterOptions.
export function combinedMonsterOptions(raw: readonly string[]): string[] {
  const strongers = new Set(HNM_MERGE_PAIRS.map(p => p.stronger.toLowerCase()));
  const byBase = new Map(HNM_MERGE_PAIRS.map(p => [p.base.toLowerCase(), p]));
  const out: string[] = [];
  for (const m of raw) {
    if (strongers.has(m.toLowerCase())) continue; // folded into the base entry
    const pair = byBase.get(m.toLowerCase());
    out.push(pair ? `${pair.base}/${pair.stronger}` : m);
  }
  return out;
}

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
