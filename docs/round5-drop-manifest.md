# Round 5 drop manifest ("update C": R5.6 + R5.7)

## Status: R5.6 DONE and R5.7 DONE in the repo (2026-11-16), nothing deployed yet

Live is `1adf6d6` ("update B" = plan R5.4 + R5.5). The owner ships R5.6 and R5.7 TOGETHER as
"update C", the same way Round 4 shipped deploy 4.

| Step | What it does | Migration |
|---|---|---|
| R5.6 (`2d67e85`, `34153d9`, `68612f1`, `5c36f97`) | Stops writing the 14 old `AspNetUsers` alt, job and craft columns and the roster name `AppUserLinkshells.CharacterName`. Removes the start-up "Characters mirror" check. | none |
| R5.7 (`ef733c4`, `3ebc387`, plus the runbook) | Drops the 15 columns (`Round5Contract`, id `20261116221633_Round5Contract`) and deletes the code that still named them. | `Round5Contract` |

`AspNetUsers.CharacterName` STAYS (owner decision 1). Every save still sets it to the account's
main (slot 0) `Characters` name. It is not in this file.

**Rolling back update C needs SQL first.** See "Rolling back update C" at the end.

## What R5.6 changed

Characters is the only written source of names, jobs and crafts (`CharacterWriter`, since R5.5).
Until R5.6 the save hook (`ApplicationDbContext.StampAccountsFromCharactersAsync`) also copied it
onto the 14 old account columns and the roster name, which the build before update B still read.
R5.6 stopped both copies (only `CharacterName` is still stamped) and removed the start-up check
(`Characters mirror: ...` / `Name copies: ...` in the log). Its SQL lives on in the tests
(`tests/LinkshellManager.PostgresTests/AccountNameMirrorCounts.cs`).

## What R5.7 dropped

None of these had a constraint or an index, so the golden file
(`Snapshots/SchemaCatalog.txt`) lost exactly these 15 `column` lines.

| Table | Column | Definition (update B) | Why it went |
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

No row is deleted or changed, and no DKP, gil, ledger, journal or loot table is touched. What the
dropped columns held was a copy of `Characters` (or out of date), so nothing is lost; the
pre-deploy backup still has it.

## What R5.7 deleted in the code

`git grep -n "ROUND5-DROP"` now finds nothing in the app (`Round5DropMarkerTests` checks it).

- `Models/AppUser.cs`: the 14 properties, the `ReadCharactersInstead` text and their `[Obsolete]`
  marks. `CharacterName` and `Characters` stay.
- `Models/AppUserLinkshell.cs`: `CharacterName`.
- `Data/ApplicationDbContext.AccountNames.cs`: the old-column list and its half of the "changed by
  hand" guard (the `CharacterName` guard stays). The new-account guard now takes only a new
  account's `CharacterName` as its main (slot 0) row, when the save brings no main row for it.
  Only tests still create accounts that way.
- `Data/CharacterSync.cs`: `SourceProperties` and `DesiredRows`. Only `NameNotBlankCheckSql` (the
  `CK_Characters_Name_NotBlank` check) is left.
- `Services/DataAdminPolicy.cs`: the roster name in `AppUserLinkshell`'s read-only columns.
- `LinkshellManagerDiscordApp.csproj`: `CS0618` as an error, and the test projects' `NoWarn` for it.
  Nothing is `[Obsolete]` any more; a new `[Obsolete]` use now shows as a normal warning, and the
  build must stay at 0 warnings.
- `tools/db-wipe-activity-keep-roster.sql`: the comment now says names, jobs and crafts live only
  in `Characters`.
- Kept on purpose: `CharacterWriter.MaxNameLength` (64). The rollback SQL below copies every name
  into the varchar(64) alt columns again, and update B keeps copying them on every save. It can go
  up to `Character.MaxNameLength` (256) one deploy after update C.

## Tests changed in R5.7

- Seeding: tests describe a member with the account's `CharacterName` (the main) and Characters rows
  for alts, jobs and crafts: `TestCharacters.Row`, `Add`, `AddAlts`, and `new AppUser { ... }
  .WithAlts(alt1, alt2)` (`tests/LinkshellManager.Tests/TestCharacters.cs`, now also compiled into
  the Postgres tests). About 60 files only lost their roster name (`CharacterName = ...` on
  `new AppUserLinkshell`) or switched alt and job seeds; what they check is unchanged.
- Updated because the behaviour they pinned is gone with the columns:
  - `CharacterSyncTests`: no `SourceProperties`/`DesiredRows` tests; now the account has no name,
    job or craft column but `CharacterName`, and the new-account guard takes the main only.
  - `CharacterWriterTests`, `AddonSelfNameBackfillTests`, `OutsideOnboardingRosterTests`,
    `PlaceholderClaimServiceTests`, `JoinRequestTests`, `ProfileRenameSignupTests`: the "old columns
    and roster names are not written" checks went; each still checks the account's main.
  - `AccountNameStampTests`: every writer path still checks `CharacterName` follows the main. The
    old-column and roster-name snapshots, the mirror refill runs and the refill drift test moved to
    `Round5ContractRoundTripTests` (the refill is now part of the rollback SQL).
  - `JsonbArrayColumnTests`: the jsonb read/write checks now use the `Characters` jsonb columns.
  - `DataAdminCatalogTests`, `DataAdminSchemaGraphTests`: their jsonb example is
    `Character.JobLevels`.
  - `CharactersBecomeSourceRoundTripTests`: the C# `DesiredRows` rules it compares the frozen SQL
    with are frozen in the test now (it reads the old columns with raw SQL).
  - `EndCampCharacterizationTests` snapshots (`Snapshots/EndCamp/*.txt`): regenerated; the only
    difference is the dropped columns.
  - `Round5DropMarkerTests`: no marker is left, and this file holds the rollback SQL word for word.
- Retired: `CharacterNameLookupParityTests`. Its "old way" resolver read the roster name and the old
  columns; it proved R5.4 changed no answer. See git history before R5.7.
- New: `Round5ContractRoundTripTests`. From `CharactersBecomeSource`: Up, Down, Up; then the
  rollback SQL three times (the second after drift made by hand); every column the update B model
  maps exists with its type; update B's start-up check (`AccountNameMirrorCounts`) counts 0; the
  build before update B's copy into `Characters` (`PreviousBuildCharacterCopy`) changes nothing;
  and the next update C start drops the columns again.

## Rolling back update C to update B

Update B (`1adf6d6`) still MAPS the 15 columns. EF names every mapped column when it loads a row,
so after `Round5Contract` it fails on every query that loads an account or a membership. A plain
folder swap is NOT enough: the columns must come back first. Update B also compares the old columns
and the roster names with `Characters` at start-up, and copies them on every save, so they come back
filled.

1. Stop the service (RUNBOOK "Rollback", step 1).
2. Take a backup (`sudo /usr/local/bin/lsm-backup.sh`).
3. Run the SQL below in `psql`, connected to the app's database. It is `Round5Contract.RollbackSql`
   word for word (`Round5DropMarkerTests` checks that), and `Round5ContractRoundTripTests` runs
   it. Safe to run twice.
4. Swap the folders (RUNBOOK "Rollback", steps 2-4).

What it does:

- puts the 15 columns back, with update B's types;
- fills them from `Characters` (the "mirror refill"): every account's old columns become an exact
  copy of its characters (NULL where a slot has no character), and every roster name becomes the
  account's main. One NOTICE says how many rows it wrote, for example
  `Round5Contract re-add: N account(s) refilled from Characters; M roster name(s) set to the main.`
  A second run says 0 and 0, plus Postgres's own harmless "column ... already exists, skipping"
  notices;
- forgets `Round5Contract`, so the next update C start runs the drops again.

After it, update B's start-up check prints no `Characters mirror` warning, and even the build
before update B would find nothing to copy. Still: never go back further than update B.

`CharactersBecomeSource` and the earlier Round 5 migrations need nothing here.

```sql
ALTER TABLE "AspNetUsers"
    ADD COLUMN IF NOT EXISTS "AltCharacterName1" character varying(64) NULL,
    ADD COLUMN IF NOT EXISTS "AltCharacterName2" character varying(64) NULL,
    ADD COLUMN IF NOT EXISTS "JobLevels" jsonb NULL,
    ADD COLUMN IF NOT EXISTS "StrongJobs" jsonb NULL,
    ADD COLUMN IF NOT EXISTS "MeritJobs" jsonb NULL,
    ADD COLUMN IF NOT EXISTS "CraftLevels" jsonb NULL,
    ADD COLUMN IF NOT EXISTS "Alt1JobLevels" jsonb NULL,
    ADD COLUMN IF NOT EXISTS "Alt1StrongJobs" jsonb NULL,
    ADD COLUMN IF NOT EXISTS "Alt1MeritJobs" jsonb NULL,
    ADD COLUMN IF NOT EXISTS "Alt1CraftLevels" jsonb NULL,
    ADD COLUMN IF NOT EXISTS "Alt2JobLevels" jsonb NULL,
    ADD COLUMN IF NOT EXISTS "Alt2StrongJobs" jsonb NULL,
    ADD COLUMN IF NOT EXISTS "Alt2MeritJobs" jsonb NULL,
    ADD COLUMN IF NOT EXISTS "Alt2CraftLevels" jsonb NULL;
ALTER TABLE "AppUserLinkshells" ADD COLUMN IF NOT EXISTS "CharacterName" text NULL;
DO $$
DECLARE
    accounts integer;
    rosters integer;
BEGIN
    UPDATE "AspNetUsers" u SET
        "CharacterName" = c.m_name,
        "JobLevels" = c.m_jl, "StrongJobs" = c.m_sj, "MeritJobs" = c.m_mj, "CraftLevels" = c.m_cl,
        "AltCharacterName1" = c.a1_name, "Alt1JobLevels" = c.a1_jl, "Alt1StrongJobs" = c.a1_sj,
        "Alt1MeritJobs" = c.a1_mj, "Alt1CraftLevels" = c.a1_cl,
        "AltCharacterName2" = c.a2_name, "Alt2JobLevels" = c.a2_jl, "Alt2StrongJobs" = c.a2_sj,
        "Alt2MeritJobs" = c.a2_mj, "Alt2CraftLevels" = c.a2_cl
    FROM (
        SELECT x."Id" AS id,
               m."Name" AS m_name, m."JobLevels" AS m_jl, m."StrongJobs" AS m_sj, m."MeritJobs" AS m_mj, m."CraftLevels" AS m_cl,
               a1."Name" AS a1_name, a1."JobLevels" AS a1_jl, a1."StrongJobs" AS a1_sj, a1."MeritJobs" AS a1_mj, a1."CraftLevels" AS a1_cl,
               a2."Name" AS a2_name, a2."JobLevels" AS a2_jl, a2."StrongJobs" AS a2_sj, a2."MeritJobs" AS a2_mj, a2."CraftLevels" AS a2_cl
        FROM "AspNetUsers" x
        LEFT JOIN "Characters" m  ON m."AppUserId"  = x."Id" AND m."Slot"  = 0
        LEFT JOIN "Characters" a1 ON a1."AppUserId" = x."Id" AND a1."Slot" = 1
        LEFT JOIN "Characters" a2 ON a2."AppUserId" = x."Id" AND a2."Slot" = 2
    ) c
    WHERE c.id = u."Id"
      AND (u."CharacterName", u."JobLevels", u."StrongJobs", u."MeritJobs", u."CraftLevels",
           u."AltCharacterName1"::text, u."Alt1JobLevels", u."Alt1StrongJobs", u."Alt1MeritJobs", u."Alt1CraftLevels",
           u."AltCharacterName2"::text, u."Alt2JobLevels", u."Alt2StrongJobs", u."Alt2MeritJobs", u."Alt2CraftLevels")
          IS DISTINCT FROM
          (c.m_name::text, c.m_jl, c.m_sj, c.m_mj, c.m_cl,
           c.a1_name::text, c.a1_jl, c.a1_sj, c.a1_mj, c.a1_cl,
           c.a2_name::text, c.a2_jl, c.a2_sj, c.a2_mj, c.a2_cl);
    GET DIAGNOSTICS accounts = ROW_COUNT;

    UPDATE "AppUserLinkshells" l SET "CharacterName" = u."CharacterName"
    FROM "AspNetUsers" u
    WHERE u."Id" = l."AppUserId" AND l."CharacterName" IS DISTINCT FROM u."CharacterName";
    GET DIAGNOSTICS rosters = ROW_COUNT;

    RAISE NOTICE 'Round5Contract re-add: % account(s) refilled from Characters; % roster name(s) set to the main.',
        accounts, rosters;
END $$;
DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261116221633_Round5Contract';
```
