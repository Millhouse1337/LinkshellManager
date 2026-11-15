# Migration squash (Round 4, deploy 5 / D11)

## What changed

The 173 EF migrations, from `20260607213956_InitialCreate` to
`20261115161909_NormalizeNamesForSquash`, were replaced by one migration:
`Migrations/20261115190359_Baseline.cs`. The old files are still in git history. Commit
`4b05ee2` is the last build that has them.

Baseline builds exactly the schema the old chain built:

- the tables, keys, indexes, foreign keys and checks EF generates from the model;
- plus `Baseline.ChainOnlyDefaultsSql`: 54 column defaults the old chain added with
  `AddColumn(defaultValue: ...)` that the model does not declare (for example
  `Linkshells.WdDkpPerWindow DEFAULT 0.25`). EF always sends these columns on insert, so the app
  does not need them, but production has them.

The proof is `SchemaCatalogGoldenTests`. Its golden file, `Snapshots/SchemaCatalog.txt`, was
written from a database the old chain built. The squash did not change it, and a fresh
database built from Baseline matches it line for line. Production's catalog matched the same
file before the squash (read-only check, 2026-11-15).

## What happens on production (adoption)

Production already has every table, so it must never run Baseline's `Up`. Before
`Database.Migrate()`, `Program.cs` calls `MigrationBaseline.AdoptAsync` (`Data/MigrationBaseline.cs`).
In one transaction it does exactly one of these:

| The history table... | It does |
|---|---|
| does not exist (new, empty database, or no database yet) | nothing; `Migrate` builds it from Baseline |
| already has Baseline's row | nothing |
| has `20261115161909_NormalizeNamesForSquash` | adds Baseline's row (`INSERT ... ON CONFLICT DO NOTHING`) |
| has other rows, but not that one | stops start-up: "deploy commit 4b05ee2 first" |

**The old rows are kept.** Nothing is deleted, and no table, column or row changes. The start-up
log says `Migration squash: added 20261115190359_Baseline to the migration history (old rows kept).`
once, on the first start. After that, `__EFMigrationsHistory` has 174 rows.

If adoption ever fails to run, the same thing by hand is (after a backup):

```sql
INSERT INTO "__EFMigrationsHistory" ("MigrationId", "ProductVersion")
VALUES ('20261115190359_Baseline', '10.0.15') ON CONFLICT DO NOTHING;
```

Adoption only runs where the app migrates at start-up. If `Database:RunMigrationsOnStartup` is
ever set to `false`, run that INSERT before any `dotnet ef database update` against a database
the old chain built.

## Rollback

A plain folder swap (deploy/RUNBOOK.md, "Rollback"), no SQL. The build before the squash sees
every migration id it knows plus one it does not (Baseline), and EF ignores unknown ids.
`UnknownMigrationHistoryRowTests` proved that on that build. Deploying the squash build again
later finds Baseline's row and does nothing.

## Optional cleanup, much later

> **Only after a backup, and only when no rollback to a build from before the squash
> (commit 4b05ee2 or older) will ever be needed.**

The 173 old rows can then be deleted. Nothing reads them; this only tidies the table.

```sql
DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId" < '20261115190359_Baseline';
```

It compares with `<` on purpose (the plan said `<> Baseline`): migrations added after the squash
sort after Baseline and must stay.

## Adding a migration now

Nothing changes: change the model, then `dotnet ef migrations add <Name>`. The new migration
comes after Baseline. Never edit Baseline itself: production has already "applied" it, so an
edit would never reach production and the two would drift apart.

A local database built by the old chain needs the adoption INSERT above once (or drop it and run
`dotnet ef database update` on an empty database). `dotnet ef database update` does not run
`Program.cs`'s adoption step.

## Tests removed with the squash

Tests that migrated a database to a named old migration, or ran an old migration's SQL, cannot
run without those migrations. They were deleted and are in git history (commit `4b05ee2`):

- Round trips at a named migration (Up, Down, Up): `AccountJobsBackfillTests`,
  `AddCharactersRoundTripTests`, `AddJoinRequestDetailsRoundTripTests`,
  `AddTodDurationsAndSpawnKeysRoundTripTests`, `CharactersSwitchRoundTripTests`,
  `DropIdentityRoleTablesRoundTripTests`, `DropPartySetupTemplateSignupColumnsRoundTripTests`,
  `DropRedundantJobRatingsIndexRoundTripTests`, `JobRatingsForeignKeysRoundTripTests`,
  `MembershipRoleIdBackfillTests`, `NormalizeNamesForSquashRoundTripTests`,
  `OneCloseOneRouteOneInviteRoundTripTests`, `Phase4BMigrationRoundTripTests`,
  `RosterSwitchRoundTripTests`, `Round4ContractRoundTripTests`, `TodSwitchRoundTripTests`.
- One-off backfill or cleanup SQL from a migration: `JournalLineHolderBackfillTests`,
  `LootLedgerBackfillTests`, `LootLinkshellBackfillTests`, `LootWinnerBackfillTests`,
  `RecycledBoardRosterCleanupTests`, and the unit tests `ChartPopItemMigrationTests`.
- Single test methods that ran a migration's SQL, in files that stay: the fold / merge / cancel /
  "applies over an old bad row" tests in `CampCreditTests`, `JoinRequestTests`,
  `OneMembershipOneParticipationTests`, `OneParentConstraintTests` and `TreasuryOneReversalTests`;
  the "migration names the same monsters" tests in `MonsterTimingTests`,
  `HnmRecurringBoardMergeMigrationTests` and `AddonHnmBoardParityTests`.

Kept by moving what they need: the ToD SQL helpers moved to `Data/TodDerivedSql.cs` (so the parity
tests and `CharacterSync` keep them), the retired NM names are copied into
`CustomMonsterCatalogTests`, and `ClosedListCheckConstraintTests` now checks the five checks on the
Baseline database. New: `MigrationBaselineAdoptionTests` (production-shaped history, an older
history, a fresh database, an empty history table, no database yet, two instances starting at
once, and running adoption twice).
