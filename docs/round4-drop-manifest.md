# Round 4 drop manifest (deploy 4, "D10 part B")

## Status: PENDING

Deploy 3 (Round 4 D9 + "D10 part A") took every reader and writer away from the columns and
indexes below. Nothing was dropped: the model properties and `ApplicationDbContext` config were
kept, so the EF model still matches the database. Each kept item has a `// ROUND4-DROP: <reason>`
comment right above it.

Deploy 4 drops them with a migration, deletes the kept code, and adds the
`NormalizeNamesForSquash` migration (plan D10). The squash itself (D11) is a later deploy.
Find every spot with:

```
git grep -n "ROUND4-DROP"
```

Tip: to find every C# use of a property before you delete it, delete it and build. The compiler
lists every line that still names it. Raw SQL strings and names written as strings in tests are
the only places it can't see; they are listed below. (Deploy 3 checked this by marking each kept
property `[Obsolete]` in a scratch copy and building the whole solution: only the model, its
config and tests still named them.)

## Before deploy 4 (owner, ask first)

- Deploy 3 has been live and stable, and its post-deploy check passed.
- The deploy takes a `pg_dump` before migrating. Check it passed before going on. It is the only
  copy of what the dropped columns held.
- Optional read-only counts, so you know what is being thrown away:
  - `SELECT count(*) FROM "ChartMemberKeyItems" WHERE "MembershipId" IS NOT NULL;` (ticks made
    before deploy 3; nothing reads the number)
  - `SELECT count(*) FROM "AppUserLinkshells" WHERE "JobLevels" IS NOT NULL OR "StrongJobs" IS NOT NULL OR "MeritJobs" IS NOT NULL;`
    (old copies of jobs that now live on the account)

## The deploy 4 migrations

1. `Round4Contract`, made with `dotnet ef migrations add` after the kept code below is deleted.
   EF writes the `DropIndex` / `DropColumn` calls in a safe order.
   - `Down` re-adds the columns EMPTY (all are nullable, so no default is needed) and re-creates
     the indexes. Data comes back only from the dump.
   - No data rows are deleted or changed. No DKP, gil, ledger or loot table is touched.
2. `NormalizeNamesForSquash`: contents decided from the catalog diff (plan D10). Generate a
   model-only baseline in a throwaway branch, diff its catalog against
   `tests/LinkshellManager.PostgresTests/Snapshots/SchemaCatalog.txt`, and add a guarded rename
   for every difference. Re-generate the golden file afterwards (`UPDATE_SCHEMA_GOLDEN=1`, see
   `SchemaCatalogGoldenTests`); it must then equal the baseline's catalog.
   - Expect differences in the Postgres 18 NOT NULL constraint names (`<table>_<column>_not_null`,
     378 of them in the golden file): Postgres does not rename them when a migration renames a
     column or table, so the old chain can leave old names that a fresh Baseline would not make.
     They need the same guarded `ALTER TABLE ... RENAME CONSTRAINT`.
   - Also compare production's I3 output with the golden file (read-only, on the owner's machine;
     never commit it). A name only production has needs a guarded rename too.
3. Run both test suites; `MigrationsUpToDateTests` must pass with the new snapshot, and
   `SchemaCatalogGoldenTests` must pass after the golden file is regenerated and its diff reviewed
   (it should lose exactly the lines for the items below, plus any renames).

## What deploy 4 drops

| Table | Item | Definition today | Why it goes |
|---|---|---|---|
| `AppUserLinkshells` | column `JobLevels` | `jsonb` NULL | Old home of the main's job levels. Jobs live on the account (`AspNetUsers.JobLevels`, and `Characters`). Unread since AccountJobsAndKeyItemAccounts, unwritten since RosterSwitch. |
| `AppUserLinkshells` | column `StrongJobs` | `jsonb` NULL | Same, for the "strong" flags. |
| `AppUserLinkshells` | column `MeritJobs` | `jsonb` NULL | Same, for the merit notes. |
| `ChartMemberKeyItems` | column `MembershipId` | `integer` NULL | Ticks are matched by account (`AppUserId`) since RosterSwitch. Nullable since AddCharacters. Unwritten since deploy 3: new ticks leave it NULL, a placeholder claim no longer moves it, and the save hook that stamped `AppUserId` from it is gone. |
| `ChartMemberKeyItems` | index `IX_ChartMemberKeyItems_LinkshellId_Board_KeyItemName_Membershi~` | UNIQUE (`LinkshellId`, `Board`, `KeyItemName`, `MembershipId`) | Goes with the column. The name is cut at 63 characters by Postgres (the `~`); EF generates the right name. `IX_ChartMemberKeyItems_LinkshellId_Board_KeyItemName_AppUserId` (one tick per account) is the rule now. |
| `HnmRecurringBoards` | index `IX_HnmRecurringBoards_LinkshellId_MonsterName` | UNIQUE (`LinkshellId`, `MonsterName`) | Every board lookup uses `SpawnKey`, and `IX_HnmRecurringBoards_LinkshellId_SpawnKey` (unique) is stricter: one name always gives one spawn key. |
| `Tods` | index `IX_Tods_LinkshellId_MonsterName` | (`LinkshellId`, `MonsterName`) | No ToD query filters by name: they match on `SpawnKey` (`IX_Tods_LinkshellId_SpawnKey_Time`) or list a linkshell's rows, which `IX_Tods_LinkshellId_Time` covers. |

Grep done in deploy 3 for the two name indexes: no code compares `MonsterName` on `Tods` or
`HnmRecurringBoards` in a query (the only `MonsterName` filters are `!= null`, plus in-memory
grouping after the rows are loaded), no raw SQL outside the migrations names either table's
`MonsterName` index, and no code catches a unique violation by either index's name.

## Code to delete with them

- `Models/AppUserLinkshell.cs`: `JobLevels`, `StrongJobs`, `MeritJobs` and their comment.
- `Models/ChartMemberKeyItem.cs`: `MembershipId` and its comment.
- `Data/ApplicationDbContext.cs`:
  - the `ROUND4-DROP` `HasIndex` on (`LinkshellId`, `Board`, `KeyItemName`, `MembershipId`) in the
    `ChartMemberKeyItem` block;
  - the `ROUND4-DROP` `HasIndex` on (`LinkshellId`, `MonsterName`) in the `HnmRecurringBoard` block;
  - the `ROUND4-DROP` `HasIndex` on (`LinkshellId`, `MonsterName`) in the `Tod` block;
  - the note that replaced the old key-item account stamp hook (no marker; it just says the hook
    is gone).
- `Services/DataAdminPolicy.cs`: the whole `ChartMemberKeyItem` entry (it only makes
  `MembershipId` read-only). After that the table is plain full CRUD.
- Comments that mention the old column, to re-word: `Services/ChartKeyItemService.cs` (the new
  tick), `Services/UserMergeService.cs` (the `RepointAsync` header and the `ChartMemberKeyItems.AppUserId`
  column line).
- NOT to delete: `ChartKeyItemGridRow.MembershipId`, the `membershipId` argument of
  `ChartKeyItemService.SetAsync`, `CanSetKeyItemFor` and the Angular `membershipId` in the key-item
  request. Those are the ROSTER membership being ticked (who the officer clicked), not the column.
- `DbWipe/Program.cs` and `tools/db-wipe-activity-keep-roster.sql`: already clean (deploy 3 took
  the `"JobLevels" = NULL` out of the scrub). Nothing to change.

## Tests to update in deploy 4

These still name dropped items on purpose. They will not compile, or will fail, after deploy 4:

- `LinkshellManager.Tests/MainCharacterJobsTests.cs`: seeds `AppUserLinkshell.JobLevels` and
  asserts the old copies are left alone. Drop the membership seeds and those asserts.
- `LinkshellManager.Tests/DataAdminSchemaGraphTests.cs`: uses `AppUserLinkshell.JobLevels` as its
  jsonb/protected example. Use `AppUser.JobLevels` instead.
- `LinkshellManager.Tests/DataAdminPolicyTests.cs`: the `ChartMemberKeyItem` / `MembershipId`
  line in `DerivedTodColumns_AreShownButNeverEdited`.
- `LinkshellManager.Tests/KeyItemAccountStampTests.cs`: built around `MembershipId`; keep only
  "the caller's account is saved" and "no account is refused".
- `LinkshellManager.Tests/ChartKeyItemGridTests.cs`: the `Has` helper and two seeds set
  `MembershipId`; `SetAsync_TicksACellAndStampsWhoDidIt` asserts it is NULL.
- `LinkshellManager.Tests/PlaceholderClaimServiceTests.cs`: key-item seeds and the
  `MembershipId` asserts.
- `PostgresTests/PlaceholderClaimMergeTests.cs`: key-item seeds and the `MembershipId` asserts.
- `PostgresTests/KeyItemTickWithoutMembershipTests.cs`: asserts NULL `MembershipId` against the
  old index; keep the per-account part, drop the rest.
- `PostgresTests/Snapshots/SchemaCatalog.txt`: regenerate (see above).
- Round-trip tests that seed or check these items at OLDER migrations with raw SQL
  (`AccountJobsBackfillTests`, `AddCharactersRoundTripTests`, `RosterSwitchRoundTripTests`,
  `TodSwitchRoundTripTests`, `AddTodDurationsAndSpawnKeysRoundTripTests`): they migrate to named
  migrations, so they should keep passing. If one migrates to "latest" or checks the current
  schema, pin it to its own migration instead of deleting it.
- New: a round-trip test for `Round4Contract` (Up, Down, Up on its own database, with rows in all
  three tables), and a test that the rollback SQL below makes the deploy 3 model's columns exist
  again.

## Rolling back deploy 4 (to the deploy 3 build)

The deploy 3 build still MAPS the dropped columns (that is what kept its model matching the
database). EF names every mapped column when it loads a row, so after deploy 4's drops the deploy
3 build fails on every query that loads a membership or a key-item tick ("column does not exist").
A plain swap is NOT enough for this one deploy: the columns must come back first.

The swapped-in build does not run migrations backwards, so the owner runs deploy 4's `Down` by
hand (after a backup). The columns come back empty; deploy 3 never reads them, so that is fine:

```sql
ALTER TABLE "AppUserLinkshells"
    ADD COLUMN IF NOT EXISTS "JobLevels" jsonb NULL,
    ADD COLUMN IF NOT EXISTS "StrongJobs" jsonb NULL,
    ADD COLUMN IF NOT EXISTS "MeritJobs" jsonb NULL;
ALTER TABLE "ChartMemberKeyItems" ADD COLUMN IF NOT EXISTS "MembershipId" integer NULL;
```

The three indexes are optional for the deploy 3 build (EF never checks indexes at runtime), but
putting them back makes the schema match deploy 3 exactly:

```sql
CREATE UNIQUE INDEX IF NOT EXISTS "IX_ChartMemberKeyItems_LinkshellId_Board_KeyItemName_Membershi~"
    ON "ChartMemberKeyItems" ("LinkshellId", "Board", "KeyItemName", "MembershipId");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_HnmRecurringBoards_LinkshellId_MonsterName"
    ON "HnmRecurringBoards" ("LinkshellId", "MonsterName");
CREATE INDEX IF NOT EXISTS "IX_Tods_LinkshellId_MonsterName" ON "Tods" ("LinkshellId", "MonsterName");
```

(The board index cannot fail: the `SpawnKey` unique index already allows one board per name.)
Deploy 4's rows in `__EFMigrationsHistory` can stay: the deploy 3 build ignores ids it does not
know (`UnknownMigrationHistoryRowTests`). Renames from `NormalizeNamesForSquash` need no undo:
EF does not check constraint or index names at runtime.

Rolling back deploy 3 itself (to `a6a35a7`) needs no SQL for these items: that build reads ticks
by account, treats a NULL `MembershipId` as "no membership", and never reads the membership job
columns.
