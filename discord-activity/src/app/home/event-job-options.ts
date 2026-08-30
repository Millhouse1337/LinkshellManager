export const EVENT_MAIN_JOB_OPTIONS = [
  'Any Tank', 'Any Heal', 'Any Support', 'Any DPS',
  'WAR', 'MNK', 'WHM', 'BLM', 'RDM', 'THF', 'PLD', 'DRK',
  'BST', 'BRD', 'RNG', 'SAM', 'NIN', 'DRG', 'SMN',
  'BLU', 'COR', 'PUP'
] as const;

export const EVENT_SUB_JOB_OPTIONS = [
  'ANY',
  'WAR', 'MNK', 'WHM', 'BLM', 'RDM', 'THF', 'PLD', 'DRK',
  'BST', 'BRD', 'RNG', 'SAM', 'NIN', 'DRG', 'SMN',
  'BLU', 'COR', 'PUP'
] as const;

export const EVENT_JOB_TYPE_OPTIONS = ['Tank', 'Heal', 'Support', 'DPS'] as const;

// The 18 selectable jobs in the exact order of the backend's
// EventJobCatalog.MainJobOptions. The profile "My Jobs" editor binds level
// inputs to this list by index, matching the catalog-aligned jobLevels array
// the API sends/accepts (index 0 = WAR … 14 = SMN, 15 = BLU … 17 = PUP).
// The order is a contract — it tracks FFXI job ids (index i = job id i + 1), so
// only ever APPEND, in job-id order. Keep in sync with the backend list.
export const PROFILE_JOB_OPTIONS = [
  'WAR', 'MNK', 'WHM', 'BLM', 'RDM', 'THF', 'PLD', 'DRK',
  'BST', 'BRD', 'RNG', 'SAM', 'NIN', 'DRG', 'SMN',
  'BLU', 'COR', 'PUP'
] as const;

// The nine crafts, catalog-aligned with the API's craftLevels arrays
// (index 0 = Alchemy ... 8 = Fishing). Keep in sync with the backend CraftCatalog.
export const PROFILE_CRAFT_OPTIONS = [
  'Alchemy', 'Bonecraft', 'Clothcraft', 'Cooking', 'Goldsmithing',
  'Leathercraft', 'Smithing', 'Woodworking', 'Fishing'
] as const;

// The relic weapons each job can equip, catalog-aligned with PROFILE_JOB_OPTIONS
// (index 0 = WAR … 14 = SMN, 15 = BLU … 17 = PUP). When a job is marked as having
// its relic, the profile shows this list as a dropdown. The ToAU jobs (BLU/COR/PUP)
// have no relic weapon, so their entries are empty and the profile hides the relic
// block for them. Mirrors the backend JobRelicCatalog — keep in sync.
export const JOB_RELIC_OPTIONS: readonly (readonly string[])[] = [
  ['Bravura', 'Ragnarok'],              // WAR
  ['Spharai'],                          // MNK
  ['Mjollnir'],                         // WHM
  ['Claustrum'],                        // BLM
  ['Mandau', 'Excalibur'],              // RDM
  ['Mandau'],                           // THF
  ['Excalibur', 'Ragnarok', 'Aegis'],   // PLD
  ['Ragnarok', 'Apocalypse'],           // DRK
  ['Guttler'],                          // BST
  ['Mandau', 'Gjallarhorn'],            // BRD
  ['Yoichinoyumi', 'Annihilator'],      // RNG
  ['Amanomurakumo', 'Yoichinoyumi'],    // SAM
  ['Kikoku'],                           // NIN
  ['Gungnir'],                          // DRG
  ['Claustrum'],                        // SMN
  [],                                   // BLU — no relic
  [],                                   // COR — no relic
  []                                    // PUP — no relic
];
