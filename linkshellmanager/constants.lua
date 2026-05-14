-- constants.lua
local constants = {}

constants.STRIDE_CANDIDATES = { 0x4C, 0x50 }
constants.NAME_OFFSETS      = { 0x08, 0x04 }
constants.ZONE_OFFSETS      = { 0x2C, 0x28 }
constants.MJ_OFFSETS        = { 0x24, 0x20 }
constants.SJ_OFFSETS        = { 0x25, 0x21 }
constants.NAME_LENGTHS      = { 16, 15 }

constants.DEFAULT_SA_DURATION = 300
constants.CONFIRM_COMMANDS    = { 'here', 'present', 'herebrother' }

-- HNM attendance window counts. Keys are the canonical event names as they
-- appear in resources/creditnames.txt (and as the server's HnmConfig keys them).
-- Mirrored from Services/HnmConfig.cs so the addon can pre-fill state.windowMax
-- when an event is selected, before list_events() returns the server-supplied
-- windowCount. Server is authoritative; the constant is the offline fallback.
constants.HNM_WINDOW_COUNTS = {
    ['Tiamat']         = 24,
    ['Jormungand']     = 24,
    ['Vrtra']          = 24,
    ['Fafnir']         = 2,
    ['Nidhogg']        = 2,
    ['Behemoth']       = 2,
    ['King Behemoth']  = 2,
    ['Adamantoise']    = 2,
    ['Aspidochelone']  = 2,
}

-- Testing presets — temporary in-zone monsters used to validate the full
-- create -> start -> post -> ToD-capture pipeline without needing a real
-- HNM kill. Goblin Pathfinder exercises the Regular (single-window) path;
-- Goblin Furrier mirrors a 2-window HNM for the post-by-window path. Both
-- get scanned by the text_in defeat detector. Mirrored on the server in
-- Services/HnmConfig.cs (TestingHnms) so window-sequence validation passes.
constants.TESTING_MONSTERS = {
    ['Goblin Pathfinder'] = { windows = 1, style = 'Regular' },
    ['Goblin Smithy']     = { windows = 1, style = 'Regular' },
    ['Goblin Furrier']    = { windows = 2, style = 'HNM' },
    ['Goblin Shaman']     = { windows = 2, style = 'HNM' },
}

function constants.testing_style_for(name)
    local entry = name and constants.TESTING_MONSTERS[name]
    return entry and entry.style or nil
end

-- Curated Sky-farm NMs the ToD listener should always pick up alongside
-- the HNM and Testing tables. These are timed/popped Tu'Lia (and Ro'Maeve
-- for Faust) NMs commonly farmed for god pop items / loot. Keys are the
-- canonical chat-line names. The value is currently always true; if any
-- ever needs HNM-style multi-window attendance the table can grow into
-- the same shape as TESTING_MONSTERS.
constants.SKY_FARM_NMS = {
    -- Pop / window NMs farmed for god-pop materials.
    ['Despot']           = true,
    ['Mother Globe']     = true,
    ['Zipacna']          = true,
    ['Ullikummi']        = true,
    ['Olla Grande']      = true,
    ['Steam Cleaner']    = true,
    ['Brigandish Blade'] = true,
    ['Faust']            = true,
    -- Sky Gods (popped from the items the farm NMs above drop). Kirin is
    -- the final pop after the four gods, included so its kill line is
    -- captured the same way as the rest of the Sky farm cycle.
    ['Suzaku']           = true,
    ['Seiryu']           = true,
    ['Genbu']            = true,
    ['Byakko']           = true,
    ['Kirin']            = true,
}

-- Curated ground / overworld NMs (Bloodsucker, Simurgh, the Arthros, ...).
-- Same role as SKY_FARM_NMS — included in defeat / loot pattern matching so
-- the user doesn't have to add each one manually. Mirrors creditnames.txt's
-- "-- NMS," section. These typically run a 21-24 hour repop, so they fall
-- through to the server's default 22-hour cooldown (no SkyFarmNms cooldown
-- override needed).
constants.GROUND_NMS = {
    ['Shikigami Weapon']    = true,
    ['King Arthro']         = true,
    ['King Vinegarroon']    = true,
    ['Bloodsucker']         = true,
    ['Simurgh']             = true,
    ['Xolotl']              = true,
    ['Serket']              = true,
}

-- Curated HENMs (Promyvion / Lumoria-tier overworld pops). Mirrors
-- creditnames.txt's "-- HENMs," section. Listed separately from GROUND_NMS
-- so the Settings panel can group them under their own header — players
-- treat HENMs as a distinct tier from the regular ground NMs.
constants.HENMS = {
    ['Overlord Arthro']     = true,
    ['Ruinous Rocs']        = true,
    ['Sacred Scorpions']    = true,
    ['Mammet-9999']         = true,
    ['Ultimega']            = true,
    ['Tonberry Sovereign']  = true,
}

-- Sea NMs (CoP Sea / Al'Taieu camp). Grouped by tier so the Settings panel
-- can render each tier under its own subheading — players think of these
-- as distinct camps, not one alphabetical pile. SEA_NMS is the flat dict
-- the parser consumes; SEA_NMS_GROUPS preserves group order + labels for
-- the UI. The two are kept in sync by the build loop below.
constants.SEA_NMS_GROUPS = {
    {
        label = 'Tier 1 / Pop-item',
        names = {
            'Jailer of Temperance',
            'Jailer of Fortitude',
            'Jailer of Faith',
            "Ix'aern (Monk)",
            "Ix'aern (Dark Knight)",
            "Ix'aern (Dragoon)",
        }
    },
    {
        label = 'Tier 2 Jailers',
        names = {
            'Jailer of Hope',
            'Jailer of Justice',
            'Jailer of Prudence',
        }
    },
    {
        label = 'Tier 3',
        names = {
            'Jailer of Love',
        }
    },
    {
        label = 'Final / Special',
        names = {
            -- Absolute Virtue is the canonical FFXI Sea endgame NM. Remove
            -- (or extend) here if your linkshell tracks a different list.
            'Absolute Virtue',
        }
    },
}

-- Names the chat-line parser scans for. Distinct from SEA_NMS_GROUPS
-- because some mobs share a chat name (the three Ix'aern variants all
-- show as "Ix'aern" in defeat / loot lines) and get disambiguated to a
-- specific variant after the fact via MOB_ID_OVERRIDES.
constants.SEA_NMS_PARSER_DICT = {
    ['Jailer of Temperance'] = true,
    ['Jailer of Fortitude']  = true,
    ['Jailer of Faith']      = true,
    ["Ix'aern"]              = true,  -- chat name for all three Ix'aern jobs
    ['Jailer of Hope']       = true,
    ['Jailer of Justice']    = true,
    ['Jailer of Prudence']   = true,
    ['Jailer of Love']       = true,
    ['Absolute Virtue']      = true,
}

-- Mob server ID -> disambiguated display name. text_parser substitutes
-- the variant name after a match on an AMBIGUOUS_NAMES entry, by reading
-- the in-zone entity table to find the mob that just died.
constants.MOB_ID_OVERRIDES = {
    [16921018] = "Ix'aern (Dark Knight)",
    [16921022] = "Ix'aern (Dragoon)",
    [16916815] = "Ix'aern (Monk)",
}

-- Parser-matched names that need mob-ID disambiguation. Each key here must
-- correspond to a name the parser can match on (so SEA_NMS_PARSER_DICT or
-- one of the other dicts), and the resolved variant should appear in the
-- relevant SEA / Sky / etc. group so the server's cooldown lookup works.
constants.AMBIGUOUS_NAMES = {
    ["Ix'aern"] = true,
}

-- Combined "everything we know about Sea" set, used by render_pump.lua's
-- custom-monster de-dup so users can't accidentally add a name we'll match
-- on our own. Includes both display variants (group names) and parser-only
-- bare chat names.
constants.SEA_NMS = {}
for _, group in ipairs(constants.SEA_NMS_GROUPS) do
    for _, name in ipairs(group.names) do
        constants.SEA_NMS[name] = true
    end
end
for name, _ in pairs(constants.SEA_NMS_PARSER_DICT) do
    constants.SEA_NMS[name] = true
end

-- Strip the trailing " D<n>" suffix added by the launcher's per-monster
-- Day input (Fafnir, Behemoth, Adamantoise) so lookups keyed by the
-- canonical preset name still resolve. Idempotent for names without the
-- suffix; safe to call on any string. Returns the input unchanged when
-- given non-string values.
function constants.canonical_event_name(name)
    if type(name) ~= 'string' then return name end
    return (name:gsub('%s+D%d+%s*$', ''))
end

function constants.window_count_for(name)
    if not name then return 1 end
    -- Direct hit on a canonical key (e.g. "Tiamat", "King Behemoth").
    local direct = constants.HNM_WINDOW_COUNTS[name]
    if direct then return direct end
    -- Testing presets share the same lookup so Goblin Furrier behaves like
    -- a 2-window HNM end-to-end.
    local testEntry = constants.TESTING_MONSTERS[name]
    if testEntry then return testEntry.windows end
    -- Combined display labels like "Behemoth/King Behemoth" or
    -- "Fafnir/Nidhogg" come from the Event Presets UI. Split on '/' and
    -- look up each segment so HNM-style behavior still kicks in.
    for segment in tostring(name):gmatch('[^/]+') do
        local trimmed = segment:match('^%s*(.-)%s*$')
        local match = constants.HNM_WINDOW_COUNTS[trimmed]
        if match then return match end
        local testMatch = constants.TESTING_MONSTERS[trimmed]
        if testMatch then return testMatch.windows end
    end
    return 1
end

-- "On Time" / "Claim/Kill" for any 2-window event; numbered ("Window N") for
-- everything else. Callers that already know the effective window count (e.g.
-- the launcher knows it via state.windowMax, which is set from the server's
-- WindowCountOverride or the resource-driven preset rules) should pass it as
-- the optional third arg so user-named NM/Claim-Kill events get the right
-- labels even when their name isn't in the curated HNM_WINDOW_COUNTS table.
function constants.window_label(name, sequence, explicitCount)
    local count = tonumber(explicitCount) or constants.window_count_for(name)
    if count == 2 then
        return sequence == 1 and 'On Time' or 'Claim/Kill'
    end
    return 'Window ' .. tostring(sequence)
end

-- Format an os.date('*t') table (LOCAL time) as "April 29th 2026 10:26 PM".
-- Used for the per-window "Posted at" label in the launcher's Attendance For:
-- tabs; both the in-session post path and the post-reload server rehydration
-- funnel through this so the format stays consistent.
local MONTH_NAMES = {
    'January', 'February', 'March', 'April', 'May', 'June',
    'July', 'August', 'September', 'October', 'November', 'December'
}

local function ordinal_suffix(day)
    local mod100 = day % 100
    if mod100 >= 11 and mod100 <= 13 then return 'th' end
    local mod10 = day % 10
    if mod10 == 1 then return 'st' end
    if mod10 == 2 then return 'nd' end
    if mod10 == 3 then return 'rd' end
    return 'th'
end

function constants.format_posted_at(localTable)
    if type(localTable) ~= 'table' or not localTable.year then return nil end
    local hour12 = localTable.hour % 12
    if hour12 == 0 then hour12 = 12 end
    local ampm = (localTable.hour >= 12) and 'PM' or 'AM'
    return string.format('%s %d%s %d %02d:%02d:%02d %s',
        MONTH_NAMES[localTable.month] or '?',
        localTable.day, ordinal_suffix(localTable.day),
        localTable.year,
        hour12, localTable.min, localTable.sec or 0, ampm)
end

-- Parse an ISO-8601 UTC timestamp into the equivalent UTC epoch seconds.
-- Returns nil if the input doesn't parse. Used for elapsed-time math (e.g.
-- the Sky/Sea/Dynamis/Limbus event runtime ticker in the launcher).
--
-- DST handling: os.date('!*t', T) sets isdst=false on the returned table.
-- If we then call os.time(nowUtcTable) without nilling isdst, the runtime
-- interprets the UTC components as STANDARD local time (e.g. EST, UTC-5)
-- regardless of the date. Meanwhile os.time() on the parsed table without
-- isdst auto-detects (EDT, UTC-4 in summer). The mismatch makes the result
-- off by 1 hour during DST. Nil-ing isdst on both sides forces auto-detect
-- on both, keeping them consistent.
function constants.parse_iso_utc_to_epoch(iso)
    if type(iso) ~= 'string' then return nil end
    local y, mo, d, h, mi, s = iso:match('(%d+)-(%d+)-(%d+)T(%d+):(%d+):(%d+)')
    if not y then return nil end
    local nowLocal    = os.time()
    local nowUtcTable = os.date('!*t', nowLocal)
    nowUtcTable.isdst = nil
    local tzOffsetSec = nowLocal - os.time(nowUtcTable)
    return os.time({
        year = tonumber(y), month = tonumber(mo), day = tonumber(d),
        hour = tonumber(h), min  = tonumber(mi), sec  = tonumber(s)
    }) + tzOffsetSec
end

-- Parse an ISO-8601 UTC timestamp (e.g. "2026-04-29T22:26:25" or with a "Z" or
-- "+00:00" suffix) and return the equivalent os.date('*t') table for the
-- viewer's LOCAL time zone. Returns nil if the input doesn't parse.
function constants.parse_iso_utc_to_local_table(iso)
    if type(iso) ~= 'string' then return nil end
    local y, mo, d, h, mi, s = iso:match('(%d+)-(%d+)-(%d+)T(%d+):(%d+):(%d+)')
    if not y then return nil end
    -- os.time treats a date table as LOCAL time, so to convert UTC components
    -- to a real epoch we need to add the local offset. Compute the offset by
    -- comparing the system's UTC view of "now" (re-fed through os.time, which
    -- interprets it as local) with the actual local epoch. Nil isdst on the
    -- helper table so os.time auto-detects DST consistently with the parsed
    -- ISO components below; otherwise summer-time captures land 1 hour off.
    local nowLocal = os.time()
    local nowUtcTable = os.date('!*t', nowLocal)
    nowUtcTable.isdst = nil
    local tzOffsetSec = nowLocal - os.time(nowUtcTable)
    local utcEpoch = os.time({
        year = tonumber(y), month = tonumber(mo), day = tonumber(d),
        hour = tonumber(h), min  = tonumber(mi), sec  = tonumber(s)
    }) + tzOffsetSec
    return os.date('*t', utcEpoch)
end

return constants
