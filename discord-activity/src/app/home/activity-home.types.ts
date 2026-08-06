// Shared types and source-of-truth constants for ActivityHomeComponent.
//
// Extracted from `activity-home.component.ts` purely for navigability — no
// behavior change. Constants kept `as const` so derived union types stay
// narrow.

export const TAB_NAMES = [
  'dashboard',
  'profile',
  'linkshell',
  // Gil + Items. Both used to hang off the bottom of the Management tab ('linkshell'), below the
  // roster — the two things a linkshell OWNS, filed under the tab about its PEOPLE.
  'treasury',
  // Attendance snapshots used to be their own 'window-events' tab; they now render as two sections
  // inside 'timed-events' (Event System), since snapshots only ever come from HNM activity.
  'timed-events',
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

// Which TIER a monster belongs to on the create-event form: the HNMs -- the three
// long-window wyrms plus the three NQ/HQ families -- against everything else, the NMs.
// Mirrors HnmConfig.IsHnmTierMonster, and is the same cut the in-game addon's preset list
// draws between "HNMS (6)" and "NMS (11)".
//
// NOT the same question as which monsters run a timed spawn cadence: the timed NMs
// (Capricious Cassie, Bune, Boroka, Roc) share the kings' 7 x 10-min band and are still NMs.
export function isHnmTierMonster(name: string | null | undefined): boolean {
  if (!name) { return false; }
  const hnmTier = new Set<string>([
    'tiamat', 'jormungand', 'vrtra',
    ...HNM_MERGE_PAIRS.flatMap(p => [p.base.toLowerCase(), p.stronger.toLowerCase()]),
  ]);
  return name.split('/')
    .map(segment => segment.trim().toLowerCase())
    .filter(Boolean)
    .some(segment => hnmTier.has(segment));
}

// Monsters that pop on a BUILT-IN SPAWN WINDOW GRID: the three long-window wyrms (25 windows
// at 60 min) and everything on the short band (7 windows at 10 min) -- the kings/dragons and
// the timed NMs that share it. Mirrors HnmConfig.DefaultWindowCadence's membership.
//
// This is what makes "popped on window N" a real question. A monster with no grid -- the Sky
// NMs, Shikigami Weapon, Bloodsucker, Xolotl, King Vinegarroon, King Arthro and the rest --
// has no window to have popped on, so asking invites a number that means nothing.
const SPAWN_WINDOW_CADENCE_MONSTERS: ReadonlySet<string> = new Set([
  'tiamat', 'jormungand', 'vrtra',
  'fafnir', 'nidhogg', 'behemoth', 'king behemoth', 'adamantoise', 'aspidochelone',
  'capricious cassie', 'bune', 'boroka', 'roc',
]);

// Tolerant of a combined "Base/Stronger" label, which is the form the pickers now offer.
export function hasSpawnWindowCadence(name: string | null | undefined): boolean {
  if (!name) { return false; }
  return name.split('/')
    .map(segment => segment.trim().toLowerCase())
    .filter(Boolean)
    .some(segment => SPAWN_WINDOW_CADENCE_MONSTERS.has(segment));
}

export const TOD_COOLDOWN_OPTIONS = ['5 Min', '2 Hour', '22 Hour', '71 Hour', '72 Hour', '84 Hour', 'Other'] as const;
export type TodCooldownOption = typeof TOD_COOLDOWN_OPTIONS[number];

export const TOD_INTERVAL_OPTIONS = ['10 Min', '1 Hour', 'Not specified'] as const;
export type TodIntervalOption = typeof TOD_INTERVAL_OPTIONS[number];

export const LONG_WINDOW_TOD_MONSTERS: ReadonlySet<string> = new Set([
  'Tiamat',
  'Jormungand',
  'Vrtra'
]);

// The eight Sky farm NMs. KEEP IN SYNC with HnmConfig.SkyFarmNmOrder — the C# side is the ordered
// source of truth (the Charts → Sky "Farm NMs" run is projected from it), and nothing can check this
// copy against it across the language boundary.
const TWO_HOUR_TOD_MONSTERS: ReadonlySet<string> = new Set([
  'Brigandish Blade',
  'Despot',
  'Faust',
  'Mother Globe',
  'Olla Grande',
  'Steam Cleaner',
  'Ullikummi',
  'Zipacna'
]);

const FIVE_MINUTE_TOD_MONSTERS: ReadonlySet<string> = new Set([
  'Byakko',
  'Genbu',
  'Kirin',
  'Seiryu',
  'Suzaku',
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
  'Jailer of Temperance'
]);

// Pop-only NMs: the four Sky Gods + Kirin and the Sea NMs (both already listed in
// FIVE_MINUTE_TOD_MONSTERS above), plus the HENMs. Every one of these spawns from a pop
// item instead of a repop timer, so a configurable cooldown/interval is meaningless for
// them — they're deliberately absent from TOD_BUILT_IN_MONSTER_GROUPS and can't be added
// back through "+ New Monster". The server strips any timing stored under one of these
// names out of the settings payload (ActivityDataController's ParseTodMonsterTimings), so
// rows saved before they were dropped don't come back as "custom" monsters in the picker.
export const POP_ONLY_TOD_MONSTERS: ReadonlySet<string> = new Set([
  ...FIVE_MINUTE_TOD_MONSTERS,
  'Mammet-9999',
  'Overlord Arthro',
  'Ruinous Rocs',
  'Sacred Scorpions',
  'Tonberry Sovereign',
  'Ultimega'
]);

export interface TodMonsterTiming {
  cooldown: string;
  interval: string;
}

// Mirrors ActivityDataController.GetDefaultTodCooldown/GetDefaultTodInterval.
// This is display-only metadata for the configuration picker; posting a ToD
// still resolves its cooldown on the server.
export function defaultTodMonsterTiming(monsterName: string): TodMonsterTiming {
  const name = monsterName.trim();
  if (name === 'Tiamat' || name === 'Jormungand' || name === 'Vrtra') return { cooldown: '84 Hour', interval: '1 Hour' };
  if (LONG_WINDOW_TOD_MONSTERS.has(name)) return { cooldown: '72 Hour', interval: '1 Hour' };
  // Bloodsucker runs a 71-hour cycle, unlike the other ground NMs (22-hour default below).
  if (name === 'Bloodsucker') return { cooldown: '71 Hour', interval: '10 Min' };
  if (FIVE_MINUTE_TOD_MONSTERS.has(name)) return { cooldown: '5 Min', interval: '10 Min' };
  if (TWO_HOUR_TOD_MONSTERS.has(name)) return { cooldown: '2 Hour', interval: '10 Min' };
  return { cooldown: '22 Hour', interval: '10 Min' };
}

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
  // Timed NMs on the kings/dragons' 7 x 10-min band -- mirror the same names in
  // HnmConfig.ShortWindowHnms. They run a windowed camp like an HNM does, so the donut and the
  // HNM-vs-generic split have to see them the way the server does.
  'Capricious Cassie',
  'Bune',
  'Boroka',
  'Roc',
  // Testing presets -- mirror HnmConfig.TestingHnms.
  'Goblin Furrier',
  'Goblin Shaman'
]);

// A stored monster name may be a COMBINED "Base/Stronger" label — an HNM board logs its
// ToD under the board's AssignedMonsterName verbatim, which from day 4 reads
// "Fafnir/Nidhogg" / "Behemoth/King Behemoth" / "Adamantoise/Aspidochelone". A raw
// HNM_NAMES.has() on that never matched, so the dashboard donut counted zero claims even
// with claimed HNM ToDs on the board. Match on either half, mirroring
// HnmConfig.MonsterSegments in Services/HnmConfig.cs.
export function isHnmMonsterName(name: string | null | undefined): boolean {
  if (!name) { return false; }
  return name.split('/')
    .map(segment => segment.trim())
    .filter(Boolean)
    .some(segment => HNM_NAMES.has(segment));
}

// The monsters that get a configurable ToD cooldown, grouped by category for display.
// Timed open-world spawns only: the HNMs (constants.lua's HNM_WINDOW_COUNTS), the Sky farm
// NMs (SKY_FARM_NMS minus the gods, which pop from the items those NMs drop) and the ground
// NMs (GROUND_NMS). Pop-only mobs — Sky Gods, Sea NMs, HENMs — are excluded on purpose; see
// POP_ONLY_TOD_MONSTERS. The addon still captures their kills; they just have no repop
// timer to configure.
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
    // KEEP IN SYNC with HnmConfig.SkyFarmNmOrder. Its C# twin
    // (LinkshellCustomizeViewModel.TodMonsterGroups) IS guarded, by
    // ChartBoardCatalogTests.TheTodPickersSkyGroup_IsTheHnmConfigFarmNmSet; this copy is not.
    label: 'Sky NMs',
    names: [
      'Brigandish Blade',
      'Despot',
      'Faust',
      'Mother Globe',
      'Olla Grande',
      'Steam Cleaner',
      'Ullikummi',
      'Zipacna',
    ]
  },
  // "Other NMs" stays last so the timed categories (HNMs / Sky) sit together
  // at the top and the catch-all group settles at the bottom of the picker.
  {
    label: 'Other NMs',
    names: [
      'Bloodsucker',
      'Boroka',
      'Bune',
      'Capricious Cassie',
      'King Arthro',
      'King Vinegarroon',
      'Roc',
      'Serket',
      'Shikigami Weapon',
      'Simurgh',
      'Xolotl',
    ]
  },
];

// The Log ToD picker draws from the exact monster catalog shown in ToD
// Cooldowns. Keep Other as the final free-text sentinel.
export const TOD_MONSTER_OPTIONS = [
  ...TOD_BUILT_IN_MONSTER_GROUPS.flatMap(group => group.names),
  'Other'
] as const;

export type TodMonsterOption = typeof TOD_MONSTER_OPTIONS[number];
