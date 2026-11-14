-- ============================================================================
--  LSManager — wipe operational data, KEEP roster / settings / profile jobs /
--  party setups. Applies to ALL linkshells.
--
--  >>> TAKE A DATABASE BACKUP FIRST. THIS CANNOT BE UNDONE. <<<
--  (On the droplet: sudo /usr/local/bin/lsm-backup.sh — see deploy/RUNBOOK.md.)
--
--  Run with psql, naming the database you are connected to as "confirm".
--  The script refuses to run without it, or when the name does not match:
--     psql "<connection string>" -v ON_ERROR_STOP=1 -v confirm=<database name> \
--          -f tools/db-wipe-activity-keep-roster.sql
--
--  KEEPS:   Linkshells and their settings (LinkshellRoles, LinkshellChannelRoutes,
--           LinkshellMonsterTimings, LinkshellBanners, DkpPools, DkpPoolEventTypes,
--           HnmRecurringBoards), AddonApiTokens, AddonPairingCodes,
--           AppSettings, DiscordActivityUsers, AspNet* (users/identity),
--           PartySetups templates (+Alliances/Parties/Slots), Announcements,
--           Rules, Invites, Charts (Chart* tables), JobRatings,
--           LinkshellPresences, PlaceholderClaimRequests, and the
--           AppUserLinkshells ROSTER rows (incl. JobLevels / StrongJobs =
--           profile jobs).
--
--  WIPES:   Events (+ comments, roster snapshots and each event's own party
--           board copy), ToDs, Loot, Auctions, DKP ledger, Attendance,
--           Gil/treasury and Inventory (Items) — and zeroes the cached DKP
--           balance columns on the roster rows.
-- ============================================================================

-- Guard: stop unless -v confirm=<this database's name> was passed.
\if :{?confirm}
\else
  \echo 'Refusing to run: pass -v confirm=<database name> (the database you are connected to).'
  \quit
\endif
SELECT current_database() = :'confirm' AS confirm_matches \gset
\if :confirm_matches
\else
  \echo 'Refusing to run: confirm does not match the database you are connected to. Nothing changed.'
  \quit
\endif

BEGIN;

-- All operational tables except Events and Tods truncated together so their
-- inter-FKs are satisfied in one shot. No CASCADE: if any KEPT table referenced
-- one of these it would error here (safe) rather than silently wiping something
-- you wanted to keep.
--
-- Events and Tods are emptied with DELETE further down instead. A kept table
-- points at Events (PartySetups.OwnerEventId: an event's own copy of its party
-- board), and Events points at Tods. Postgres refuses to TRUNCATE a table that a
-- table outside the list points at, even when no row does, so putting them here
-- made the whole script fail.
TRUNCATE TABLE
    -- Events (the Events rows themselves are deleted below)
    "EventHistories",
    "EventComments",
    "EventWindowRosterSnapshots",
    "AppUserEvents",
    "AppUserEventHistories",
    "AppUserEventWindows",
    "AppUserEventStatusLedgers",
    "EventAttendanceWindows",
    "EventLootDetails",
    "EventPartySlotSignups",
    -- ToDs (the Tods rows themselves are deleted below) / window events / claim shields
    "WindowEvents",
    "WindowEventMemberDkps",
    "PendingTodSubmissions",
    "ClaimShieldCaptures",
    "ClaimShieldCaptureMembers",
    -- Auctions
    "Auctions",
    "AuctionItems",
    "AuctionHistories",
    "Bids",
    -- DKP ledger
    "DkpLedgerEntries",
    -- Attendance snapshots
    "AttendanceSnapshots",
    "AttendanceSnapshotEntries",
    -- Finances (gil/treasury) + inventory
    -- TRUNCATE takes the three treasury tables in one statement, so the FK ordering
    -- between them does not matter here (it does in DbWipe, which uses DELETE).
    "JournalEntryLines",
    "JournalEntries",
    "LedgerAccounts",
    "Items"
RESTART IDENTITY;

-- Now nothing that was wiped points at an event or a ToD any more. Deleting the
-- events also deletes each event's own party board copy (the reusable templates
-- stay); then the ToDs go. Ids start again from 1, like the TRUNCATE above.
DELETE FROM "Events";
DELETE FROM "Tods";
ALTER TABLE "Events" ALTER COLUMN "Id" RESTART WITH 1;
ALTER TABLE "Tods" ALTER COLUMN "Id" RESTART WITH 1;

-- The roster rows stay, but their cached DKP balance / seed watermarks are
-- reset to 0. JobLevels + StrongJobs (profile jobs) are intentionally left
-- untouched.
-- DkpPoolLedgerFromId must be reset alongside the seed watermark: this script TRUNCATEs
-- DkpLedgerEntries with RESTART IDENTITY, so new rows begin again at Id 1. A stale epoch would
-- silently exclude every one of them from pool attribution.
UPDATE "AppUserLinkshells"
SET "LinkshellDkp"        = 0,
    "SeededDkpEarned"     = 0,
    "SeededDkpSpent"      = 0,
    "DkpSeedLedgerId"     = 0,
    "DkpPoolLedgerFromId" = 0;

-- The HNM boards stay, but each remembers the last ToD it posted for. ToD ids start again from 1
-- above, so a remembered id would match a NEW ToD later and that board would skip posting it.
UPDATE "HnmRecurringBoards" SET "LastSourceTodId" = NULL;

-- Sanity check — every wiped table should read 0; roster should be unchanged.
SELECT
    (SELECT count(*) FROM "Events")            AS events,
    (SELECT count(*) FROM "Tods")              AS tods,
    (SELECT count(*) FROM "Auctions")          AS auctions,
    (SELECT count(*) FROM "DkpLedgerEntries")  AS dkp_ledger,
    (SELECT count(*) FROM "JournalEntries")    AS treasury_entries,
    (SELECT count(*) FROM "JournalEntryLines") AS treasury_halves,
    (SELECT count(*) FROM "LedgerAccounts")    AS treasury_categories,
    (SELECT count(*) FROM "Items")             AS items,
    (SELECT count(*) FROM "AppUserLinkshells") AS roster_rows_kept,
    (SELECT count(*) FROM "PartySetups")       AS party_setups_kept;

COMMIT;
