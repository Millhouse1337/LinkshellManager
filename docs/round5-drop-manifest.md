# Round 5 drop manifest ("update C": R5.6 + R5.7)

## Status: R5.6 DONE in the repo (2026-11-16), R5.7 next, nothing deployed yet

Live is `1adf6d6` ("update B" = plan R5.4 + R5.5). The owner ships R5.6 and R5.7 TOGETHER as
"update C", the same way Round 4 shipped deploy 4.

| Step | What it does | Migration |
|---|---|---|
| R5.6 (`2d67e85`, `34153d9`, `68612f1`, plus this file) | Stops writing the 14 old `AspNetUsers` alt, job and craft columns and the roster name `AppUserLinkshells.CharacterName`. Removes the start-up "Characters mirror" check. Everything is still mapped, with a `// ROUND5-DROP` marker. | none |
| R5.7 | Drops the 15 columns (`Round5Contract`) and deletes the marked code. | `Round5Contract` |

`AspNetUsers.CharacterName` STAYS (owner decision 1). Every save still sets it to the account's
main (slot 0) `Characters` name. It is not in this file.

## What R5.6 changed

Characters is the only written source of names, jobs and crafts (`CharacterWriter`, since R5.5).
Until R5.6 the save hook (`ApplicationDbContext.StampAccountsFromCharactersAsync`) also copied it
onto three places the build before update B still read. Now it copies only the main's name:

- **The 14 old account columns** (table below): no longer written. They keep what update B last
  copied there; an account made since has them all empty. A change made to them by hand is undone
  by the save, with a Warning (no app code can write them: `[Obsolete]` plus CS0618-as-error).
- **The roster name** `AppUserLinkshells.CharacterName`: no longer written. Old rows keep the
  account's main as update B stamped it; a membership made since has none, and a membership moved
  by a placeholder claim keeps its old roster name. Nothing has read it since R5.4: the roster
  shows the account's main.
- **The start-up check** (`Characters mirror: ...` / `Name copies: ...` in the log) is gone. Its
  SQL lives on in the tests (`tests/LinkshellManager.PostgresTests/AccountNameMirrorCounts.cs`).

Unchanged: names are still capped at 64 characters (`CharacterWriter.MaxNameLength`), because a
rollback to update B copies every name into the varchar(64) alt columns again.

Tests: `AccountNameStampTests` (replaces `MirrorParityTests`) runs every writer path (profile,
addon jobs main and alt, placeholder create and alt fill-in, claim, Discord sign-in, Data Admin,
stale contexts) and checks that `CharacterName` follows the main while the old columns and roster
names stay exactly as update B left them (empty for new rows). Each test then runs the mirror
refill below and checks the copies are exact again.

## Rolling back R5.6 alone (to update B)

A plain folder swap. Update B reads none of the 15 columns; it writes them again on its next save
of each account. Its start-up check will print `Characters mirror: N ...` with N above 0: expected
and harmless (see `deploy/RUNBOOK.md`).

### Mirror refill (only to go back further than update B)

Never roll back more than one build. If you ever must go back to a build before update B (it
READS the old columns and copies them into `Characters`), run this first, after a backup. It makes
the old columns and the roster names an exact copy of `Characters` again. Safe to run twice.
`AccountNameStampTests.MirrorRefillSql` is this SQL word for word.

```sql
UPDATE "AspNetUsers" u SET
  "CharacterName" = m."Name",
  "JobLevels" = m."JobLevels", "StrongJobs" = m."StrongJobs", "MeritJobs" = m."MeritJobs", "CraftLevels" = m."CraftLevels",
  "AltCharacterName1" = a1."Name", "Alt1JobLevels" = a1."JobLevels", "Alt1StrongJobs" = a1."StrongJobs",
  "Alt1MeritJobs" = a1."MeritJobs", "Alt1CraftLevels" = a1."CraftLevels",
  "AltCharacterName2" = a2."Name", "Alt2JobLevels" = a2."JobLevels", "Alt2StrongJobs" = a2."StrongJobs",
  "Alt2MeritJobs" = a2."MeritJobs", "Alt2CraftLevels" = a2."CraftLevels"
FROM "AspNetUsers" x
LEFT JOIN "Characters" m  ON m."AppUserId"  = x."Id" AND m."Slot"  = 0
LEFT JOIN "Characters" a1 ON a1."AppUserId" = x."Id" AND a1."Slot" = 1
LEFT JOIN "Characters" a2 ON a2."AppUserId" = x."Id" AND a2."Slot" = 2
WHERE x."Id" = u."Id";
UPDATE "AppUserLinkshells" l SET "CharacterName" = u."CharacterName"
  FROM "AspNetUsers" u WHERE u."Id" = l."AppUserId" AND l."CharacterName" IS DISTINCT FROM u."CharacterName";
```

## What R5.7 drops

None of these has a constraint or an index (checked in `Snapshots/SchemaCatalog.txt`), so the
golden file loses exactly these 15 `column` lines.

| Table | Column | Definition today | Why it goes |
|---|---|---|---|
| `AspNetUsers` | `AltCharacterName1` | `character varying(64)` NULL | Alt 1's name. Lives in `Characters` (slot 1). Unread since R5.4, unwritten since R5.6. |
| `AspNetUsers` | `AltCharacterName2` | `character varying(64)` NULL | Alt 2's name (`Characters` slot 2). Same. |
| `AspNetUsers` | `JobLevels` | `jsonb` NULL | The main's job levels (`Characters` slot 0). Same. |
| `AspNetUsers` | `StrongJobs` | `jsonb` NULL | The main's "strong" flags. Same. |
| `AspNetUsers` | `MeritJobs` | `jsonb` NULL | The main's merit notes. Same. |
| `AspNetUsers` | `CraftLevels` | `jsonb` NULL | The main's craft levels. Same. |
| `AspNetUsers` | `Alt1JobLevels` | `jsonb` NULL | Alt 1's job levels. Same. |
| `AspNetUsers` | `Alt1StrongJobs` | `jsonb` NULL | Alt 1's strong flags. Same. |
| `AspNetUsers` | `Alt1MeritJobs` | `jsonb` NULL | Alt 1's merit notes. Same. |
| `AspNetUsers` | `Alt1CraftLevels` | `jsonb` NULL | Alt 1's craft levels. Same. |
| `AspNetUsers` | `Alt2JobLevels` | `jsonb` NULL | Alt 2's job levels. Same. |
| `AspNetUsers` | `Alt2StrongJobs` | `jsonb` NULL | Alt 2's strong flags. Same. |
| `AspNetUsers` | `Alt2MeritJobs` | `jsonb` NULL | Alt 2's merit notes. Same. |
| `AspNetUsers` | `Alt2CraftLevels` | `jsonb` NULL | Alt 2's craft levels. Same. |
| `AppUserLinkshells` | `CharacterName` | `text` NULL | The roster name. The roster shows the account's main (`AspNetUsers.CharacterName`). Unread since R5.4, unwritten since R5.6. |

## Code to delete with them

Find every spot with (it must list exactly these 20 lines in `.cs` files;
`Round5DropMarkerTests` checks it):

```
git grep -n "ROUND5-DROP" -- "*.cs"
```

- `Models/AppUser.cs`: the 14 properties above (14 markers), the `ReadCharactersInstead`
  constant, and the class comment about the old copies. `CharacterName` and `Characters` stay.
- `Models/AppUserLinkshell.cs`: `CharacterName` (1 marker) and its comment.
- `Data/ApplicationDbContext.AccountNames.cs` (2 markers):
  - `OldAccountColumns` and the old-column half of `RevertCopiesChangedByHand` (keep the
    `CharacterName` guard);
  - `AdoptNewAccountsOldColumns`: a new account saved with names in its old columns. Only tests
    do that; once the columns are gone a test can only set `CharacterName`. Either delete it (and
    seed tests through `CharacterWriter.ForNewAccount` or `TestCharacters`) or keep a main-only
    version.
- `Data/CharacterSync.cs` (2 markers): `SourceProperties` and `DesiredRows` read the old columns.
  Delete both, or keep main-only versions if the adoption above stays. `NameNotBlankCheckSql`
  stays (it is the `CK_Characters_Name_NotBlank` check).
- `Services/DataAdminPolicy.cs` (1 marker): the roster name in `AppUserLinkshell`'s
  `ReadOnlyColumns`.
- `LinkshellManagerDiscordApp.csproj`: `CS0618` in `WarningsAsErrors` can stay (it guards any
  later `[Obsolete]`), or go once nothing is marked.
- `tools/db-wipe-activity-keep-roster.sql`: re-word the "Profile jobs live on the account
  (AspNetUsers) and in Characters" comment (comment only; it names no dropped column in SQL).
- `deploy/RUNBOOK.md`: the `Characters mirror: ...` row can go once update B can no longer be
  swapped in.
- NOT to delete: `CharacterWriter.MaxNameLength` (64). Keep it for now: the R5.7 rollback SQL
  re-adds varchar(64) alt columns and a rollback to update B mirrors names into them. It can be
  raised to `Character.MaxNameLength` (256) one deploy after R5.7.

Checked (2026-11-16, scratch build with the 15 properties deleted, not committed): the only app
code that names them is the code above. Raw SQL in the app names none of them.

## Tests to update in R5.7

These name dropped items on purpose. They will not compile, or will fail, after R5.7. The list is
what the compiler named in the scratch build above (delete the properties and build to see it
again), plus the raw SQL ones it cannot see.

Name the columns in raw SQL (will compile, then fail at run time):

- `PostgresTests/AccountNameMirrorCounts.cs`, `PostgresTests/PreviousBuildCharacterCopy.cs`:
  frozen SQL of update B's start-up check and the build before it. Used only by the tests below;
  delete them with those tests.
- `PostgresTests/AccountNameStampTests.cs`: the "unchanged" snapshot and the mirror refill. Keep
  the writer paths and the `CharacterName` checks; drop the old-column, roster-name and refill
  parts (or move the refill check into `Round5ContractRoundTripTests`, run after the rollback SQL).
- `PostgresTests/CharactersBecomeSourceRoundTripTests.cs`: migrates to named migrations (before
  R5.7), so it should keep passing. If it ever runs on the R5.7 model, pin it instead of deleting.
- `PostgresTests/JsonbArrayColumnTests.cs`: `MainJobs_AreWrittenAsJsonOnTheCharactersRow` checks
  the account columns stay empty; drop that query.

Seed members through the old columns (`AltCharacterName1/2`, `JobLevels`, `Alt1JobLevels`, ...
in `new AppUser { ... }`), relying on `AdoptNewAccountsOldColumns`, or set or read the roster name
(`new AppUserLinkshell { CharacterName = ... }`, `link.CharacterName`). Switch the seeds to
`CharacterWriter.ForNewAccount(...).SetNames(...)` / `SetJobs` (or a `TestCharacters` helper) and
drop the roster-name parts:

- `LinkshellManager.Tests`: `AbsentMemberEarnsNothingTests`, `AccountCharactersTests`,
  `AddonCharacterJobsTests`, `AddonContractPipelineTests`, `AddonLootPermissionTests`,
  `AddonSelfNameBackfillTests`, `ApiSignInPipelineTests`, `AuctionBidServiceTests`,
  `BreakReturnDenialTests`, `CampBonusGuardTests`, `CampDkpPoolTests`, `CampSpawnKeyLookupTests`,
  `CharacterNameValidatorTests`, `CharacterResolverCharacterizationTests`, `CharacterResolverTests`,
  `CharacterSyncTests`, `CharacterWriterTests`, `ChartCreditResolutionTests`,
  `ChartKeyItemGridTests`, `ClaimShieldOneCapturePerCampTests`, `ConcurrencyRetryTests`,
  `DataAdminCatalogTests`, `DataAdminEditorTests`, `DataAdminSchemaGraphTests` (uses
  `AppUser.JobLevels` as its jsonb example; use `Character.JobLevels`),
  `DiscordDkpSheetPublisherTests`, `DiscordInteractionHarness`, `DkpOverdraftTests`,
  `DkpPoolPickTests`, `DkpSheetServiceTests`, `EndEventOnePathTests`,
  `EventHistoryWindowArchiveTests`, `HnmCampCapturePricingTests`, `HnmCampPastEventArchiveTests`,
  `HybridEventLootTests`, `LootAfterEndCampTests`, `LootDeleteTests`,
  `LootEditThenEventDeleteTests`, `LootWinnerAccountTests`, `ManualLootServiceTests`,
  `OutsideOnboardingRosterTests`, `PastEventAuditCorrectionTests`,
  `PastEventCampArchiveReadOnlyTests`, `PastEventCorrectionPipelineTests`,
  `PastEventDkpCorrectionTests`, `PastEventDkpFromLedgerTests`, `PastEventRowsPinnedTests`,
  `PayNewlyRegisteredTests`, `PlaceholderClaimRequestServiceTests`, `PlaceholderClaimServiceTests`,
  `PlaceholderDiscordPromotionTests`, `PostedCardCaptureEditTests`, `TodCallers`,
  `TreasuryJournalWriterTests`, `UnregisteredCaptureMemberTests`,
  `WindowEventOneCreditPerAccountTests`, `WindowEventPostClaimTests`,
  `WindowsAttendedPerAccountTests`; and `TestCharacters` (uses `CharacterSync.DesiredRows`).
- `LinkshellManager.PostgresTests`: `AccountNameStampTests`, `AuctionBidRaceTests`,
  `AuctionCloseKeepsBidsTests`, `AuctionCloseRaceTests`, `CampCreditTests`,
  `CharacterNameLookupParityTests` (its "old way" resolver reads the roster name and the old
  columns; it proved R5.4 changed no answer, so it can be retired with a note),
  `CharacterNameValidatorPostgresTests`, `ConcurrencyTests`, `EndCampCharacterizationTests`,
  `EndCampLootRaceTests`, `EndEventThroughTheAppTests`, `JoinRequestTests`, `JsonbArrayColumnTests`,
  `KeyItemTickWithoutMembershipTests`, `LootAfterEndCampTests`, `MembershipRoleStampTests`,
  `PastEventCorrectionTests`, `PayNewlyRegisteredTests`, `PlaceholderClaimCampCreditTests`,
  `PlaceholderClaimMergeTests`, `PlaceholderClaimRequestTests`, `PostClaimTests`,
  `ProfileRenameSignupTests`, `WindowEventLedgerTests`.
- `PostgresTests/Snapshots/SchemaCatalog.txt`: regenerate with `UPDATE_SCHEMA_GOLDEN=1`; the diff
  must be exactly the 15 `column` lines above.
- `PostgresTests/Snapshots/EndCamp/*.txt`: some snapshots print account columns; regenerate only
  if their diff is only those columns.
- New: `Round5ContractRoundTripTests` (rows in both tables; Up, Down, Up; then the rollback SQL
  below twice, and every column the R5.6 model maps exists again).

Many of these only SEED the old columns (a member described the old way) and assert nothing about
them: switching the seed is enough. Do not weaken what they check.

## Rolling back R5.7 (update C) to update B

Update B (and the R5.6 build) still MAP the 15 columns. EF names every mapped column when it loads
a row, so after R5.7's drops those builds fail on every query that loads an account or a
membership. A plain swap is NOT enough: the columns must come back first.

The owner runs this in `psql` after a backup, BEFORE the folder swap (RUNBOOK "Rollback", step 1
stops the service first). R5.7 adds it to the migration as `Round5Contract.RollbackSql` and fills
in the real migration id; `Round5ContractRoundTripTests` runs it twice. Safe to run twice.

```sql
ALTER TABLE "AspNetUsers"
  ADD COLUMN IF NOT EXISTS "AltCharacterName1" character varying(64) NULL,
  ADD COLUMN IF NOT EXISTS "AltCharacterName2" character varying(64) NULL,
  ADD COLUMN IF NOT EXISTS "JobLevels" jsonb NULL, ADD COLUMN IF NOT EXISTS "StrongJobs" jsonb NULL,
  ADD COLUMN IF NOT EXISTS "MeritJobs" jsonb NULL, ADD COLUMN IF NOT EXISTS "CraftLevels" jsonb NULL,
  ADD COLUMN IF NOT EXISTS "Alt1JobLevels" jsonb NULL, ADD COLUMN IF NOT EXISTS "Alt1StrongJobs" jsonb NULL,
  ADD COLUMN IF NOT EXISTS "Alt1MeritJobs" jsonb NULL, ADD COLUMN IF NOT EXISTS "Alt1CraftLevels" jsonb NULL,
  ADD COLUMN IF NOT EXISTS "Alt2JobLevels" jsonb NULL, ADD COLUMN IF NOT EXISTS "Alt2StrongJobs" jsonb NULL,
  ADD COLUMN IF NOT EXISTS "Alt2MeritJobs" jsonb NULL, ADD COLUMN IF NOT EXISTS "Alt2CraftLevels" jsonb NULL;
ALTER TABLE "AppUserLinkshells" ADD COLUMN IF NOT EXISTS "CharacterName" text NULL;
DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId" = '<Round5Contract id, filled in by R5.7>';
```

- The two `ALTER TABLE` lines are what update B needs. The columns come back EMPTY; update B never
  reads them, so that is fine.
- Recommended after it: the mirror refill above, so the copies are exact again (update B's
  start-up check then prints 0, and its copies are right from the start).
- The `DELETE` forgets `Round5Contract`, so the next update C start runs the drops again. Update B
  does not know that id anyway.
- `CharactersBecomeSource` and the earlier Round 5 migrations need nothing here.
