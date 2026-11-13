# Phase 4B drop manifest

## Status: DONE (2026-11-12)

Everything below was dropped in Phase 4B. Kept as the record of what went and why, in case a
value ever has to be fished back out of the pre-4B `pg_dump`.

| Commit | Migration | What it dropped |
|---|---|---|
| `cf9d8ec` | `20261113033818_DropDeadTables` | The 7 tables, plus `Items.RevenueEntryId`, `JournalEntries.LegacyRevenueEntryId`, `DkpLedgerEntries.SourceTodLootDetailId` |
| `329c67e` | `20261113035228_DropDeadEventColumns` | Event, history, attendance and window columns; deletes the old `Verify` status rows first |
| `22fdce4` | `20261113040702_DropDeadAccountAuctionChartColumns` | Account, auction, chart, Claim Shield and presence columns |
| `187014e` | `20261113043005_DropDeadLedgerLinkshellColumns` | Ledger, treasury, linkshell, ToD, rule and addon-token columns |

The sweep commit after them removed the last marker, updated the docs and added
`tests/LinkshellManager.PostgresTests/Phase4BMigrationRoundTripTests.cs`: it fills a pre-4B
database with rows, then runs the four migrations Up, Down and Up again.

Changes from the plan below, decided after the production counts on 2026-11-12:

- `Linkshells.Status`, `Linkshells.TodMonsterTimings` and `PartySetupSlots.Label` were dropped
  too (every linkshell was 'Active', none still needed the old timings, no slot had a label). The
  "NOT in 4B" first bullet at the end no longer applies.
- No separate `UPDATE` cleared the old Google login: dropping the columns removed it.
- Production had no `LinkshellDiscordWebhooks` rows, so there were no Discord webhooks to delete.
- The drops are four migrations (one per step), not one.
- `git grep -n "PHASE4B-DROP"` now finds only this file.

---

Phase 4A (commits `97bf7c2` .. this file's commit) took every reader and writer away from the
unused tables and columns below. Nothing was dropped: the entity classes, `DbSet`s, model
properties and `ApplicationDbContext` config were kept so the EF model still matches the
database. Each kept item has a `// PHASE4B-DROP: <reason>` comment right above it.

Phase 4B is the second deploy: one migration that drops everything listed here, plus deleting the
kept code. Find every spot with:

```
git grep -n "PHASE4B-DROP"
```

Tip: to find every C# use of a property before you delete it, delete it and build. The compiler
lists every line that still names it. Raw SQL strings (DbWipe, the wipe script) and names written
as strings in tests are the only places the compiler can't see; they are listed below.

## Before 4B (owner, ask first)

- The deploy takes a `pg_dump` before migrating. Check it passed before going on.
- After that dump: set `Linkshells.GoogleOAuthRefreshTokenEnc` and `GoogleOAuthUserEmail` to `NULL`
  on production (old Google tokens and emails should not sit in the live database any longer).
- Ask the officers to delete the old Discord webhooks that `LinkshellDiscordWebhooks` points at
  (in each Discord server: Server Settings -> Integrations -> Webhooks). Dropping the table does
  not remove them from Discord.
- Optional read-only counts, so you know what is being thrown away:
  - `SELECT COUNT(*) FROM "AppUserEventStatusLedgers" WHERE "ActionType" = 'Verify';`
  - `SELECT COUNT(*) FROM "Events" WHERE "DiscordExtraMessageIds" IS NOT NULL;` (old wide board
    messages still in Discord; nothing will clean them up after 4B)
  - `SELECT COUNT(*) FROM "Events" WHERE "CreationSource" IS NOT NULL;`
  - Row counts of the seven tables below (all expected to be 0 or old data only).

## The 4B migration

- One migration, made with `dotnet ef migrations add` after the code below is deleted. EF writes
  the `DropForeignKey` / `DropIndex` / `DropColumn` / `DropTable` calls in a safe order.
- First statement, before the column drops: delete the old status-ledger rows nothing ever read:
  `DELETE FROM "AppUserEventStatusLedgers" WHERE "ActionType" = 'Verify';`
  (They are the only rows that set `Source` and `EventAttendanceWindowId`.)
- `Down` re-creates the tables and columns empty, like
  `Migrations/20261110033905_DropPendingTodSubmissions.cs`. Data comes back only from the dump.
  Columns that are NOT NULL (marked "NOT NULL" below) need a default in `Down`; EF adds one.
- Drop `IX_JournalEntries_LegacyRevenueEntryId` (unique, filtered) and the column
  `JournalEntries.LegacyRevenueEntryId` in the same migration as the `RevenueEntries` table.
- Drop `Items.RevenueEntryId` with `RevenueEntries` (plain int, no foreign key).
- Drop `DkpLedgerEntries.SourceTodLootDetailId` and `IX_DkpLedgerEntries_SourceTodLootDetailId`
  with `TodLootDetails` (plain int, no foreign key).
- Keep `JournalEntries.LegacyEntryType` / `LegacyCategory` (deliberate history, batch K).
- Run both test suites; `MigrationsUpToDateTests` must pass with the new snapshot.

## Tables to drop (7)

Dropping a table also drops its own indexes and foreign keys.

| Table | Why it goes | Its foreign keys |
|---|---|---|
| `ChartBossProgresses` | The boss progress note was never built into any screen. | `FK_ChartBossProgresses_Linkshells_LinkshellId` |
| `Notifications` | No feature reads or writes it. | `FK_Notifications_AspNetUsers_AppUserId` |
| `LinkshellDiscordWebhooks` | Replaced by `LinkshellChannelRoutes`; nothing posts to these. | `FK_LinkshellDiscordWebhooks_Linkshells_LinkshellId` |
| `LinkshellDiscordChannels` | Replaced by `LinkshellChannelRoutes`; the one-time backfill service is gone. | `FK_LinkshellDiscordChannels_Linkshells_LinkshellId` |
| `RevenueEntries` | The old gil list, converted to `JournalEntries` long ago. | `FK_RevenueEntries_Linkshells_LinkshellId` |
| `LedgerPeriods` | The treasury "lock older entries" feature was removed (it never had a button). | `FK_LedgerPeriods_Linkshells_LinkshellId` |
| `TodLootDetails` | A ToD carries no loot; the last production row was moved to `EventLootDetails` on 2026-11-09. | `FK_TodLootDetails_Tods_TodId`, `FK_TodLootDetails_DkpPools_DkpPoolId` |

Code to delete with them:

- Entity classes: `Models/ChartBossProgress.cs`, `Notification.cs`, `LinkshellDiscordWebhook.cs`,
  `LinkshellDiscordChannel.cs` (also `DiscordChannelPurposes` in the same file, only Data Admin's
  dropdown uses it), `RevenueEntry.cs`, `LedgerPeriod.cs`, `TodLootDetail.cs`.
- Their `DbSet`s and `builder.Entity<...>` blocks in `Data/ApplicationDbContext.cs`, and the
  `Tod -> TodLootDetails` `HasMany` block in the `Tod` config.
- Navigations: `AppUser.Notifications`, `Linkshell.DiscordWebhooks`, `Tod.TodLootDetails`.
- `Services/DataAdminPolicy.cs`: the `LedgerPeriod` and `TodLootDetail` read-only entries, the
  `LinkshellDiscordWebhook` entry (hides the URL), the `LinkshellDiscordChannel` Purpose dropdown,
  the `Notification` label entry, the `RevenueEntry` synced-copy entry.
- `Services/DataAdminDefaults.cs`: `"NotificationType"` in `LabelGuessOrder`.
- `Services/UserMergeService.cs`: the `Notifications`, `TodLootDetails`, `RevenueEntries`,
  `LedgerPeriods` (two) and `ChartBossProgresses` lines.
- `DbWipe/Program.cs`: `"TodLootDetails"`, `"Notifications"`, `"RevenueEntries"`, `"LedgerPeriods"`
  in the wipe list; the `TodLootDetails`, `RevenueEntries` and `LedgerPeriods` scrub steps; the
  "Lock rows" and "Legacy revenue rows" treasury counts; the `LedgerPeriods` and
  `RevenueEntries (legacy)` scrub-treasury steps, and `"RevenueEntryId" = NULL` / `OR
  "RevenueEntryId" IS NOT NULL` in the "Items (un-mark sold)" step. Update the header comments
  ("any lock, and the dormant legacy revenue rows").
- `db-wipe-activity-keep-roster.sql`: the `"TodLootDetails"`, `"RevenueEntries"` and
  `"LedgerPeriods"` lines, the `revenue_legacy` sanity count, and the three dropped tables named
  in the KEEPS header.
- Do NOT delete `Services/RevenueEntryConversionMap.cs` or its tests: the old migration
  `20260730045439_ConvertRevenueEntriesToJournal` calls it, and that migration still runs (before
  the drop) on every fresh database.

## Columns to drop, by table

"NOT NULL" = the column has no `?`; nothing writes it now, so new rows get the C# default.

### AddonApiTokens
- `Label` - nothing ever sent a device name. Also take it out of the Data Admin
  `EditableAllowList` and `LabelColumns` for `AddonApiToken`.

### AddonPairingCodes
- `Label` - always null; nothing sent one.
- `ConsumedTokenId` - written on redeem before, never read.

### Announcements
- `LinkshellName` - a copy of the linkshell's name nothing reads. Also remove the Data Admin
  `SyncedCopies` entry for `Announcement`.

### AppUserEventHistories
- `StartTime` - write-only copy (the event has its own).
- `IsQuickJoin` (NOT NULL bool) - write-only copy.
- `IsVerified` - only the removed hidden history panel read it.
- `Proctor` - write-only copy.

### AppUserEvents
- `EndTime` - never written or read.
- `EventDkp` - DKP lives on the history row and the ledger.
- `Proctor` - nothing showed it.
- `PauseTime` - break start times live in the BreakStart ledger rows.

### AppUserEventStatusLedgers (delete the `Verify` rows first, see above)
- `AppUserId` + `FK_AppUserEventStatusLedgers_AspNetUsers_AppUserId` +
  `IX_AppUserEventStatusLedgers_AppUserId` - the row's `AppUserEvent` already names the member.
  Also remove the navigation `AppUser` and the `UserMergeService` line.
- `VerifiedBy`, `DeniedBy` - nothing showed them.
- `Source` - only the removed `Verify` rows set it.
- `EventAttendanceWindowId` + `FK_AppUserEventStatusLedgers_EventAttendanceWindows_EventAtten~`
  (name cut at 63 characters by Postgres) + `IX_AppUserEventStatusLedgers_EventAttendanceWindowId` -
  only the removed `Verify` rows set it. Also remove the navigation `EventAttendanceWindow`.

### AspNetUsers
- `PrimaryLinkshellName` - the name is joined from `Linkshells` now.
- `ProfileImage` - the Discord avatar download that filled it is gone; nothing read it.

### AttendanceSnapshots
- `UtcOffset` - no longer stored. The addon still SENDS `utcOffset`; the request record keeps the
  field so it is accepted and ignored. Do not remove it from the request.
- `CreatedAtUtc` (NOT NULL) - `CapturedAtUtc` dates the row.
- `AttInputAppendedAt` - Google Sheets leftover.

### AuctionHistories
- `DkpPoolId` + `FK_AuctionHistories_DkpPools_DkpPoolId` + `IX_AuctionHistories_DkpPoolId` -
  each winner's ledger row is pinned to the pool. Also delete the `AuctionHistories` repoint in
  `Services/DkpPoolEditor.cs` (it only exists because this FK refuses a pool delete).
- `DiscordChannelId` - never written or read.

### AuctionItems
- `EndingBidDkp` - the results card reads `CurrentHighestBid`.
- `StartTime`, `EndTime` - copies of the auction's own times.

### ChartMemberKeyItems
- `CharacterName` (NOT NULL) - the grid takes names off the roster.

### ChartPopItemCredits
- `LinkshellId` (NOT NULL int, no FK), `MembershipId` + `IX_ChartPopItemCredits_LinkshellId_MembershipId`
  - farmers are matched by name.
- `Detail` - never written by any client.

### ChartPopItems
- `HeldByMembershipId` - the app always sent null.

### ChartWishlistRequests
- `RequestedByMembershipId` - ownership is `RequestedByAppUserId`.
- `UpdatedAt` (NOT NULL) - never read.

### ClaimShieldCaptures
- `MemberCount`, `MatchedCount` (both NOT NULL) - counted from the member rows.

### DiscordActivityUsers
- `Discriminator` (NOT NULL, default "0") - Discord retired discriminators.
- `LastSeenAtUtc` (NOT NULL) - was stamped on every request, never read.

### DkpLedgerEntries
- `EventStartTime`, `EventEndTime` - `OccurredAt` dates the row.
- `SourceTodLootDetailId` + `IX_DkpLedgerEntries_SourceTodLootDetailId` - goes with `TodLootDetails`.
- `SourceAuctionHistoryId` + `FK_DkpLedgerEntries_AuctionHistories_SourceAuctionHistoryId` +
  `IX_DkpLedgerEntries_SourceAuctionHistoryId` - only fed Google Sheets. Also remove the
  navigation `SourceAuctionHistory`.
- `AttInputRowNumber` + `IX_DkpLedgerEntries_AttInputRowNumber` - Google Sheets row number.
- `SheetAppendedAt` - Google Sheets stamp.
- KEEP `SourceEventLootDetailId` (loot refunds need it).

### EventAttendanceWindows
- `AttInputAppendedAt` - Google Sheets leftover.

### EventHistories
- `StartDate` - `StartTime` holds it.
- `EventDkp` - Past Events totals the ledger.
- `Details` - only the removed hidden history panel showed it.

### Events
- `EventDkp` - never written.
- `CreationSource` - its writer (the old addon create endpoint) is gone.
- `AttInputAppendedAt` - Google Sheets leftover.
- `WdAwaitingProcessingSince` - never written.
- `DiscordExtraMessageIds` - old full-width boards only; the clean-up code is gone.

### EventWindowRosterSnapshots
- `AppUserId` - never read (`CharacterName` is what shows). Also remove the `UserMergeService` line.

### Items
- `LinkshellName` - a copy nothing reads. Also remove the Data Admin `SyncedCopies` entry for `Item`.
- `RevenueEntryId` - goes with `RevenueEntries`.

### JournalEntries
- `SourceAuctionItemId` - never read (the memo names the item).
- `SourceAuctionHistoryId` - never written or read.
- `LegacyRevenueEntryId` + `IX_JournalEntries_LegacyRevenueEntryId` - goes with `RevenueEntries`.

### JournalEntryLines
- `LineMemo` - always null.

### LedgerAccounts
- `Description` - no screen shows it.
- `IsPostable` (NOT NULL, default true) - nothing checked it.
- `IsSystem` (NOT NULL) - nothing reads it.
- `SortOrder` (NOT NULL) + `IX_LedgerAccounts_LinkshellId_SortOrder` - categories list by
  `AccountNumber`.
- KEEP `IsCash` and `IsActive` (the one-cash-account index uses `IsCash`).

### Linkshells
- Google Sheets leftovers: `GoogleSpreadsheetId`, `GoogleSheetAppCreated` (NOT NULL bool),
  `DkpTemplateTabName`, `SheetTemplateSyncEnabled` (NOT NULL bool), `GoogleOAuthRefreshTokenEnc`,
  `GoogleOAuthUserEmail`, `GoogleOAuthConnectedAt`. Also remove the Data Admin `ProtectedColumns`
  entry that hides the token.
- Replaced by channel routes: `DkpExportDiscordChannelId`, `DkpExportDiscordChannelName`,
  `DkpSheetDiscordMessageId`.

### LinkshellPresences
- `AppUserId` - the Lobby uses its own roster check. Also remove the `UserMergeService` line.
- `ReportedByCharacterName` - never read.

### Rules
- `LinkshellName` - a copy nothing reads. Also remove the Data Admin `SyncedCopies` entry for
  `Rule`. Once the Rule, Announcement, Item and RevenueEntry entries are all gone,
  `linkshellNameCopy` has no users; check whether the `SyncedCopies` feature has any left.

### Tods
- `TotalClaims` - a 0/1 copy of `Claim`.
- `TotalTods` - always 1.

### WindowEvents
- `ClosedAtUtc` - `Status` says the card is closed.
- `Notes` - never written or read.
- `FirstAttInputRowNumber`, `AttInputRowCount` - Google Sheets leftovers.

## Tests to update in 4B

These still name dropped items on purpose (Data Admin tests are generic over the model and test
the special cases above; the others seed old data). They will not compile, or will fail, after 4B:

- `DataAdminCatalogTests`: `LinkshellDiscordWebhook.Url`, `Linkshell.GoogleOAuthRefreshTokenEnc`,
  `AppUser.ProfileImage`.
- `DataAdminPolicyTests`: `LedgerPeriod` in the ledger list, the `TodLootDetail` read-only check,
  the `LinkshellDiscordChannel` Purpose dropdown, `AppUser.ProfileImage` protected.
- `DataAdminRelationsTests`: `Rule.LinkshellName` seeds, `AppUser.ProfileImage`.
- `DataAdminEditorTests`: `Rule.LinkshellName` (the `SyncedCopies` tests need another table with a
  copied column, or go), `"LastSeenAtUtc"` (string).
- `DataAdminCascadePreviewTests`, `DataAdminQueryServiceTests`: `Rule.LinkshellName` seeds.
- `DataAdminSchemaGraphTests`: `"LedgerPeriod.LinkshellId"` and `"LedgerPeriod"` (strings).
- `PostgresTests/PlaceholderClaimMergeTests`: the `Notifications` seed row.
- `PostgresTests/OneMembershipOneParticipationTests`: runs the OLD migration SQL
  `OneMembershipPerLinkshell.MergeDuplicateMembershipsSql` on today's schema. That SQL moves
  `ChartPopItems.HeldByMembershipId`, `ChartPopItemCredits.MembershipId` and
  `ChartWishlistRequests.RequestedByMembershipId`, so it will fail once they are dropped. Drop those
  seeds/asserts (and `ChartMemberKeyItem.CharacterName`, `ChartPopItemCredit.LinkshellId`) and stop
  running that SQL against the current schema. The migration itself is history: leave it alone.

## NOT in 4B

- Still have readers, wait on production counts: `Linkshells.Status`, `Linkshells.TodMonsterTimings`,
  `PartySetupSlots.Label`.
- Kept and now used: `LinkshellChannelRoutes.ChannelName` (fallback label when Discord can't list
  the channels).
- Template sign-up columns on `PartySetupSlots` (`SignedUpAtUtc`, `SignedUpAppUserId`, ...): Phase 5.3.
- `AspNetRoles` / `AspNetUserRoles` / `AspNetRoleClaims`: Phase 7.
- Batch U (use instead of drop, Phase 5.4) and Batch K (keep: audit stamps and deliberate copies)
  in the plan.
- The addon's `tod/{id}/loot` endpoint: it stays, and files the loot under the camp's event.
