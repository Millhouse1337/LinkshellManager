-- ============================================================================
--  LSManager — wipe operational data, KEEP roster / settings / profile jobs /
--  party setups. Applies to ALL linkshells.
--
--  >>> TAKE A DATABASE SNAPSHOT FIRST. THIS CANNOT BE UNDONE. <<<
--  (DigitalOcean managed Postgres: create an on-demand backup / fork before
--   running.)
--
--  Run against the PRODUCTION managed database, e.g.:
--     psql "postgresql://doadmin:****@db-host:25060/defaultdb?sslmode=require" \
--          -v ON_ERROR_STOP=1 -f db-wipe-activity-keep-roster.sql
--
--  KEEPS:   Linkshells, LinkshellRoles, LinkshellDiscordChannels/Webhooks,
--           LinkshellChannelRoutes, AddonApiTokens, AddonPairingCodes,
--           AppSettings, DiscordActivityUsers, AspNet* (users/identity),
--           PartySetups (+Alliances/Parties/Slots), Announcements, Rules,
--           Invites, Notifications, and the AppUserLinkshells ROSTER rows
--           (incl. JobLevels / StrongJobs = profile jobs).
--
--  WIPES:   Events, ToDs, Loot, Auctions, DKP ledger, Attendance, Gil/treasury
--           (RevenueEntries) and Inventory (Items) — and zeroes the cached DKP
--           balance columns on the roster rows.
-- ============================================================================

BEGIN;

-- All operational tables truncated together so their inter-FKs are satisfied
-- in one shot. No CASCADE: if any KEPT table referenced one of these it would
-- error here (safe) rather than silently wiping something you wanted to keep.
TRUNCATE TABLE
    -- Events
    "Events",
    "EventHistories",
    "AppUserEvents",
    "AppUserEventHistories",
    "AppUserEventWindows",
    "AppUserEventStatusLedgers",
    "EventAttendanceWindows",
    "EventLootDetails",
    "EventPartySlotSignups",
    -- ToDs / window events / claim shields (PHASE4B-DROP: remove the TodLootDetails line once that
    -- table is dropped; no code uses it)
    "Tods",
    "TodLootDetails",
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
    -- RevenueEntries is the pre-conversion shape and LedgerPeriods the removed lock feature; no
    -- code uses either (PHASE4B-DROP: remove both lines here once the tables are dropped). The
    -- other three treasury tables are the live ones. TRUNCATE takes them all in one statement, so the FK
    -- ordering between them does not matter here (it does in DbWipe, which uses DELETE).
    "RevenueEntries",
    "JournalEntryLines",
    "JournalEntries",
    "LedgerAccounts",
    "LedgerPeriods",
    "Items"
RESTART IDENTITY;

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

-- Sanity check — every wiped table should read 0; roster should be unchanged.
SELECT
    (SELECT count(*) FROM "Events")            AS events,
    (SELECT count(*) FROM "Tods")              AS tods,
    (SELECT count(*) FROM "Auctions")          AS auctions,
    (SELECT count(*) FROM "DkpLedgerEntries")  AS dkp_ledger,
    -- PHASE4B-DROP: remove with the RevenueEntries table.
    (SELECT count(*) FROM "RevenueEntries")    AS revenue_legacy,
    (SELECT count(*) FROM "JournalEntries")    AS treasury_entries,
    (SELECT count(*) FROM "JournalEntryLines") AS treasury_halves,
    (SELECT count(*) FROM "LedgerAccounts")    AS treasury_categories,
    (SELECT count(*) FROM "Items")             AS items,
    (SELECT count(*) FROM "AppUserLinkshells") AS roster_rows_kept,
    (SELECT count(*) FROM "PartySetups")       AS party_setups_kept;

COMMIT;
