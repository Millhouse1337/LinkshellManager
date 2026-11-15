# Round 4 drop manifest (deploy 4, "D10 part B")

## Status: DONE in the repo (2026-11-15), not deployed yet

Everything in "What deploy 4 drops" below is dropped by one migration. Kept as the record of what
went and why, in case a value ever has to be fished back out of the pre-deploy-4 `pg_dump`.

| Commit | Migration | What it does |
|---|---|---|
| `d76ecf2` | `20261115154156_Round4Contract` | Drops `AppUserLinkshells.JobLevels/StrongJobs/MeritJobs`, `ChartMemberKeyItems.MembershipId` and its unique index, `IX_HnmRecurringBoards_LinkshellId_MonsterName`, `IX_Tods_LinkshellId_MonsterName` |
| `7a17615` | `20261115161909_NormalizeNamesForSquash` | Validates the 7 NOT VALID checks; renames 13 NOT NULL constraints and 2 sequences to the names a model-built table gets (see "Squash prep" below). Changes no row or column. |

The commit after it added `tests/LinkshellManager.PostgresTests/Round4ContractRoundTripTests.cs`:
it fills a deploy 3 database with rows, runs the migration Up, Down and Up again, then runs the
rollback SQL (below) twice and checks that every column the deploy 3 build maps is back.

Changes from the plan below:

- The migration is raw SQL with `IF EXISTS` / `IF NOT EXISTS` (safe to run twice), not EF's
  `DropColumn` / `DropIndex` calls. `Down` runs `Round4Contract.ReAddSql`.
- The rollback SQL also deletes Round4Contract's row from `__EFMigrationsHistory`, so the next
  deploy 4 start runs the drops again (see "Rolling back deploy 4" below).
- `NormalizeNamesForSquash` (migration 2 below) also VALIDATEs the NOT VALID checks, not only
  renames. If an old row breaks a check, that check is left NOT VALID with a Postgres WARNING
  instead of stopping the app's start. The WARNING is not in the normal startup log, so check
  with the query under "Squash prep" below.
- `git grep -n "ROUND4-DROP"` now finds only this file.

What the dropped columns held on production (read-only counts, 2026-11-15): `ChartMemberKeyItems`
had 0 rows, so `MembershipId` held nothing. The membership job columns held only OLD copies of job
levels that `AccountJobsAndKeyItemAccounts` had already copied to `AspNetUsers` (252 memberships);
nothing has read them since deploy 2 or written them since `RosterSwitch`.

---

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
   - DONE: see "Squash prep (NormalizeNamesForSquash)" below for what the diffs found.
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

The swapped-in build does not run migrations backwards, so the owner runs this by hand in `psql`
(after a backup), BEFORE the folder swap (RUNBOOK "Rollback", step 1 stops the service first). It is
`Round4Contract.RollbackSql` word for word; `Round4ContractRoundTripTests` runs that constant,
twice, and checks every column the deploy 3 model maps is back. The columns come back EMPTY;
deploy 3 never reads them, so that is fine. Safe to run twice.

```sql
ALTER TABLE "AppUserLinkshells"
    ADD COLUMN IF NOT EXISTS "JobLevels" jsonb NULL,
    ADD COLUMN IF NOT EXISTS "StrongJobs" jsonb NULL,
    ADD COLUMN IF NOT EXISTS "MeritJobs" jsonb NULL;
ALTER TABLE "ChartMemberKeyItems" ADD COLUMN IF NOT EXISTS "MembershipId" integer NULL;
CREATE UNIQUE INDEX IF NOT EXISTS "IX_ChartMemberKeyItems_LinkshellId_Board_KeyItemName_Membershi~"
    ON "ChartMemberKeyItems" ("LinkshellId", "Board", "KeyItemName", "MembershipId");
CREATE UNIQUE INDEX IF NOT EXISTS "IX_HnmRecurringBoards_LinkshellId_MonsterName"
    ON "HnmRecurringBoards" ("LinkshellId", "MonsterName");
CREATE INDEX IF NOT EXISTS "IX_Tods_LinkshellId_MonsterName"
    ON "Tods" ("LinkshellId", "MonsterName");
DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261115154156_Round4Contract';
```

- The four `ADD COLUMN` lines are what the deploy 3 build needs. The three indexes make the
  schema match deploy 3 exactly; they cannot fail (`MembershipId` comes back all NULL, and the
  `SpawnKey` unique index already allows only one board per monster name).
- The `DELETE` forgets Round4Contract, so the next deploy 4 start runs its drops again. Without
  it EF would think the drops already ran, the columns would stay, and production's catalog would
  no longer match the golden file the squash (D11) needs. The deploy 3 build does not know that id
  anyway, so removing it changes nothing for that build.
- `NormalizeNamesForSquash` needs nothing in this SQL. Its history row stays: the deploy 3 build
  ignores ids it does not know (`UnknownMigrationHistoryRowTests`). Its validated checks and new
  names stay too: EF does not look at constraint or sequence names, or at whether a check is
  validated. On the next deploy 4 start EF runs only Round4Contract again, although the newer
  migration is already applied (`NormalizeNamesForSquashRoundTripTests` runs exactly this).

Rolling back deploy 3 itself (to `a6a35a7`) needs no SQL for these items: that build reads ticks
by account, treats a NULL `MembershipId` as "no membership", and never reads the membership job
columns.

## Squash prep (NormalizeNamesForSquash)

The squash (D11) needs three catalogs (pre-check I3) to be the same: production's, a fresh
database built from the migration chain (the golden file), and a fresh database built from the
model (what a Baseline migration makes). Two diffs were run before writing this migration:

- **Production (before deploy 4) vs the golden file:** the same, except that production had
  `CK_AuctionItems_OneParent` and `CK_EventAttendanceWindows_OneParent` validated by hand. No name
  differences.
- **Model-built vs the golden file** (a scratch test: `EnsureCreated` on an empty database, then
  the I3 query; not committed):
  - the 7 NOT VALID checks are fully checked in a model-built database;
  - 13 NOT NULL constraints and 2 identity sequences kept the names they had before a rename
    (`SkyPopItems_*` and `SkyPopItemCredits_*` on `ChartPopItems` / `ChartPopItemCredits`,
    `SkyPopItems_God_not_null`, `SkyPopItemCredits_SkyPopItemId_not_null`,
    `Linkshells_EnableRevenue_not_null`). Postgres 18 names these when a column is made and never
    renames them. Production has the same old names.

What the migration does (raw SQL, every step guarded, safe to run twice):

1. `ALTER TABLE ... VALIDATE CONSTRAINT` for each of the 7 checks that is still NOT VALID.
   Production had 0 rows breaking any of them (2026-11-15), and a NOT VALID check still refuses
   every new bad write, so that cannot have changed. VALIDATE only reads; reads and writes go on
   while it runs. If an old row does break one, that check stays NOT VALID and Postgres sends a
   WARNING naming it. That WARNING does NOT show in the normal startup log: Npgsql logs Postgres
   notices only at Debug level, and the app logs at Information. So after the deploy, check with
   this read-only query (it must return 0 rows; the I3 fingerprint matching the golden file
   proves the same):

   ```sql
   SELECT conrelid::regclass, conname FROM pg_constraint
   WHERE connamespace = 'public'::regnamespace AND contype = 'c' AND NOT convalidated;
   ```

   If it lists a check, fix the row and run `NormalizeNamesForSquash.ValidateChecksSql` by hand
   in psql (psql does show the WARNING or NOTICE).
2. `ALTER TABLE ... RENAME CONSTRAINT` / `ALTER SEQUENCE ... RENAME TO` for the 15 names, each
   only when the old name is there and the new one is not.

`Down` renames back. It does not un-validate: Postgres can only do that by dropping and re-adding
the check, and a validated check is the same rule.

Result: production's catalog with deploy 4's drops, the 5 validations it still needed and the 15
renames applied, is line for line the new golden file (checked on the owner's machine with the
read-only I3 output; that output is never committed). The model-built catalog now has the same
names as the golden file.

### Left for the squash (D11)

**DONE in the squash (deploy 5):** `Baseline.ChainOnlyDefaultsSql` sets all 54 defaults, and
`SchemaCatalogGoldenTests` passes against a Baseline-built database with this golden file unchanged.
See `docs/migration-squash.md`. Since the squash, the migration classes this file names (for
example `Round4Contract.RollbackSql`, `NormalizeNamesForSquash.ValidateChecksSql`) exist only in
git history. SQL quoted in this file still works by hand; for the rest, read it with
`git show 4b05ee2:Migrations/<file>.cs`.

The model-built catalog still differs from the golden file in one way: **54 column defaults**.
These were added by `AddColumn(defaultValue: ...)` so old rows got a value, but the model has no
`HasDefaultValue` for them, so a Baseline made from the model would not create them. Examples:
`Linkshells.WdDkpPerWindow DEFAULT 0.25`, `Linkshells.HnmAttendanceMode DEFAULT 'Standard'`,
`Tods.Hq DEFAULT false`, `Events.HnmWindowNumber DEFAULT 1`. EF always sends these columns on
insert, so the app does not need them, but raw-SQL inserts (tests, tools) may. D11 must either
keep them in the Baseline (raw SQL `ALTER COLUMN ... SET DEFAULT`) or drop them from production
first; it must not let the catalogs drift. To list them, repeat the model-built diff above. (The
`__EFMigrationsHistory` table is the only other difference; `EnsureCreated` does not make it.)
