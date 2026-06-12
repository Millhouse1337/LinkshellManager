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
    -- ToDs / window events / claim shields
    "Tods",
    "TodLootDetails",
    "WindowEvents",
    "WindowEventMemberDkps",
    "PendingTodSubmissions",
    "PendingTodLootSubmissions",
    "ClaimShieldCaptures",
    "ClaimShieldCaptureMembers",
    -- Auctions
    "Auctions",
    "AuctionItems",
    "AuctionHistories",
    "Bids",
    -- DKP ledger
    "DkpLedgerEntries",
    -- Attendance snapshots + pending submissions
    "AttendanceSnapshots",
    "AttendanceSnapshotEntries",
    "PendingAttendanceSnapshotSubmissions",
    "PendingAttendanceSnapshotEntries",
    "PendingAttendanceWindowSubmissions",
    "PendingAttendanceWindowMemberSubmissions",
    -- Finances (gil/treasury) + inventory
    "RevenueEntries",
    "Items"
RESTART IDENTITY;

-- The roster rows stay, but their cached DKP balance / seed watermarks are
-- reset to 0. JobLevels + StrongJobs (profile jobs) are intentionally left
-- untouched.
UPDATE "AppUserLinkshells"
SET "LinkshellDkp"     = 0,
    "SeededDkpEarned"  = 0,
    "SeededDkpSpent"   = 0,
    "DkpSeedLedgerId"  = 0;

-- Sanity check — every wiped table should read 0; roster should be unchanged.
SELECT
    (SELECT count(*) FROM "Events")            AS events,
    (SELECT count(*) FROM "Tods")              AS tods,
    (SELECT count(*) FROM "Auctions")          AS auctions,
    (SELECT count(*) FROM "DkpLedgerEntries")  AS dkp_ledger,
    (SELECT count(*) FROM "RevenueEntries")    AS revenue,
    (SELECT count(*) FROM "Items")             AS items,
    (SELECT count(*) FROM "AppUserLinkshells") AS roster_rows_kept,
    (SELECT count(*) FROM "PartySetups")       AS party_setups_kept;

COMMIT;
