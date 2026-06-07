export const EVENT_MAIN_JOB_OPTIONS = [
  'Any Tank', 'Any Heal', 'Any Support', 'Any DPS',
  'WAR', 'MNK', 'WHM', 'BLM', 'RDM', 'THF', 'PLD', 'DRK',
  'BST', 'BRD', 'RNG', 'SAM', 'NIN', 'DRG', 'SMN'
] as const;

export const EVENT_SUB_JOB_OPTIONS = [
  "Player's Choice",
  'WAR', 'MNK', 'WHM', 'BLM', 'RDM', 'THF', 'PLD', 'DRK',
  'BST', 'BRD', 'RNG', 'SAM', 'NIN', 'DRG', 'SMN'
] as const;

export const EVENT_JOB_TYPE_OPTIONS = ['Tank', 'Heal', 'Support', 'DPS'] as const;

// The 15 classic jobs in the exact order of the backend's
// EventJobCatalog.MainJobOptions. The profile "My Jobs" editor binds level
// inputs to this list by index, matching the catalog-aligned jobLevels array
// the API sends/accepts (index 0 = WAR ... 14 = SMN). Keep in sync with the
// backend list.
export const PROFILE_JOB_OPTIONS = [
  'WAR', 'MNK', 'WHM', 'BLM', 'RDM', 'THF', 'PLD', 'DRK',
  'BST', 'BRD', 'RNG', 'SAM', 'NIN', 'DRG', 'SMN'
] as const;
