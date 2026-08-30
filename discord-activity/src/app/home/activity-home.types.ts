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

// Whether a monster is one of the three NQ/HQ families -- either half on its own, or the
// combined "Base/Stronger" label a board stores. Mirrors HnmConfig.HasHqVariant, and is the
// predicate behind every question only a merge pair can answer: the ToD form's HQ toggle, and
// the Day box on both the ToD form and the create-event form. Day counts a POP CYCLE, and only
// these three have one (HnmConfig.HnmDayCycles) -- asking Tiamat for a day invites a number
// nothing can interpret, which the board would then print.
export function hasHqVariant(name: string | null | undefined): boolean {
  if (!name) { return false; }
  const halves = name.split('/').map(part => part.trim().toLowerCase()).filter(Boolean);
  return HNM_MERGE_PAIRS.some(pair =>
    halves.includes(pair.base.toLowerCase()) || halves.includes(pair.stronger.toLowerCase()));
}

// (combinedMonsterOptions lived here: it folded a raw per-half monster list into the merged
// "Base/Stronger" dropdown entries. The pickers read the linkshell's monster setups now, and those
// rows are STORED merged, so there is nothing left to fold client-side. The server still merges
// when it seeds a catalog — HnmConfig.CombinedMonsterOptions.)

// Which TIER a monster belongs to on the create-event form: the HNMs -- the six long-window
// monsters plus the three NQ/HQ families -- against everything else, the NMs.
// Mirrors HnmConfig.IsHnmTierMonster, and is the same cut the in-game addon's preset list
// draws between "HNMS" and "NMS".
//
// NOT the same question as which monsters run a timed spawn cadence: the timed NMs
// (Capricious Cassie, Bune, Boroka, Roc) share the kings' 7 x 10-min band and are still NMs.
export function isHnmTierMonster(name: string | null | undefined): boolean {
  if (!name) { return false; }
  const hnmTier = new Set<string>([
    'tiamat', 'jormungand', 'vrtra',
    'cerberus', 'hydra', 'khimaira',
    ...HNM_MERGE_PAIRS.flatMap(p => [p.base.toLowerCase(), p.stronger.toLowerCase()]),
  ]);
  return name.split('/')
    .map(segment => segment.trim().toLowerCase())
    .filter(Boolean)
    .some(segment => hnmTier.has(segment));
}

// Monsters that pop on a BUILT-IN SPAWN WINDOW GRID: the six long-window monsters (25 windows
// at 60 min) and everything on the short band (7 windows at 10 min) -- the kings/dragons and
// the timed NMs that share it. Mirrors HnmConfig.DefaultWindowCadence's membership.
//
// This is what makes "popped on window N" a real question. A monster with no grid -- the Sky
// NMs, Shikigami Weapon, Bloodsucker, Xolotl, King Vinegarroon, King Arthro and the rest --
// has no window to have popped on, so asking invites a number that means nothing.
const SPAWN_WINDOW_CADENCE_MONSTERS: ReadonlySet<string> = new Set([
  'tiamat', 'jormungand', 'vrtra',
  'cerberus', 'hydra', 'khimaira',
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

// (TOD_COOLDOWN_OPTIONS / TOD_INTERVAL_OPTIONS lived here: fixed preset lists for the ToD form's
// two duration dropdowns. Both fields take a number + a unit now, because each monster carries its
// own configured cooldown and cadence and a curated list can only express the handful of durations
// someone happened to think of.)
//
// (LONG_WINDOW_TOD_MONSTERS / TWO_HOUR_TOD_MONSTERS / FIVE_MINUTE_TOD_MONSTERS /
// defaultTodMonsterTiming lived here too: hand-copied mirrors of the server's default cooldown
// table. The overview payload now carries every linkshell's complete monster catalog — including
// the built-in defaults for a linkshell that has never configured anything — so there is nothing
// left for a client-side copy to answer, and one more place for the numbers to drift is exactly
// what this change set out to remove.)

// Pop-only NMs: the four Sky Gods + Kirin, the Sea NMs, and the HENMs. Every one spawns from a pop
// item instead of a repop timer, so a configurable cooldown is meaningless for them — they are
// deliberately absent from the seeded monster catalog and cannot be added back as a custom monster.
// The server rejects one too (MonsterTimingEditor); this copy just makes the error immediate.
export const POP_ONLY_TOD_MONSTERS: ReadonlySet<string> = new Set([
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
  'Jailer of Temperance',
  'Mammet-9999',
  'Overlord Arthro',
  'Ruinous Rocs',
  'Sacred Scorpions',
  'Tonberry Sovereign',
  'Ultimega'
]);

// (HNM_NAMES / isHnmMonsterName lived here: a hand-kept mirror of HnmConfig's true-HNM name
// sets, used only by the dashboard "HNM Claims" donut to pick claims out of the overview's ToD
// tail. The donut is aggregated server-side now — HnmClaimStatsService counts every claimed ToD
// and ships the finished slices — so the client no longer needs a second copy of the list to
// drift against.)

// The monsters that get a configurable ToD cooldown, grouped by category for display.
// Timed open-world spawns only: the HNMs (constants.lua's HNM_WINDOW_COUNTS), the Sky farm
// NMs (SKY_FARM_NMS minus the gods, which pop from the items those NMs drop) and the ground
// NMs (GROUND_NMS). Pop-only mobs — Sky Gods, Sea NMs, HENMs — are excluded on purpose; see
// POP_ONLY_TOD_MONSTERS. The addon still captures their kills; they just have no repop
// timer to configure.
// (TodMonsterGroup / TOD_BUILT_IN_MONSTER_GROUPS / TOD_MONSTER_OPTIONS lived here: the client's
// copy of the monster catalog and its HNMs / Sky NMs / Other NMs grouping. Both the Monster Setups
// editor and the ToD picker read the linkshell's own rows off the overview now, so the copy would
// only be a second answer to a question the server already answers.)
