-- state_defaults.lua
-- Builds and returns a fresh, fully-populated `state` table with every default
-- value used throughout the addon. Extracted byte-for-byte from att.lua's
-- top-level `local state = {...}` literal.
--
-- All addon state lives on this single table. Modules that need to mutate
-- state receive it as their first argument. Calling M.create() multiple times
-- returns independent tables (each call constructs fresh subtables).

local M = {}

function M.create()
    return {
        debugMode    = false,
        selectedMode = 'HNM',
        g_LSMode     = nil, -- 'ls' or 'ls2'

        isAttendanceWindowOpen = false,
        isAttendLauncherOpen   = false,
        isHelpWindowOpen       = false,
        isDebugWindowOpen      = false,
        isSettingsWindowOpen   = false,

        pendingEventName     = nil,
        pendingFilePath      = nil,
        pendingLSMessage     = nil,
        pendingAttend        = nil, -- { eventName, useLS2, fireAt }
        pendingSeaScan       = nil,
        pendingGather        = nil, -- { eventName, fireAt }
        launcherGather       = nil, -- { eventName, fireAt, isAutoCreate } - for /attend launcher's auto-scan
        pendingComp          = nil, -- { eventName, fireAt }

        -- Multi-select linkshell channels for /sea targeting and LS chat output.
        -- LS1 default-on; LS2 off. Both can be enabled to broadcast to both.
        lsChannels           = { ls1 = true, ls2 = false },
        attendDelaySec       = 3,

        scanNextLetter       = nil,

        suggestions          = { evs={}, zone='' },
        lastDetectedZid      = nil,
        attForceRefreshAt    = nil,
        skipNextSearch       = false,

        -- Web sync (att-addon -> LSManager API)
        linkedEventId        = nil,   -- chosen via launcher dropdown for the current session
        linkedEventName      = nil,
        autoCreateOnWrite    = false, -- if true and no event chosen, /lsm <name> creates one before posting
        showEventPresets     = false, -- launcher's Event Presets section is collapsed by default

        -- HNM attendance windows (Phase B of Model A).
        --   windowMax       = total windows allowed for the selected event (1 for non-HNM).
        --   windowSequence  = how many windows have been posted so far. The next post
        --                     uses windowSequence+1; the Post New Window button is
        --                     disabled when windowSequence >= windowMax.
        --   windowRosters   = sequence-keyed snapshots of the entries posted for each
        --                     window (used by the Attendance For: tab UI).
        -- The flat fields above are a "view" of the currently selected event. The
        -- per-event source of truth is windowStateByEvent so navigating away and
        -- back keeps the posted windows intact:
        --   windowStateByEvent[eventId] = { max, sequence, rosters, postedAt }
        -- where postedAt[seq] is the human-readable HH:MM:SS that window was posted.
        windowMax            = 1,
        windowSequence       = 0,
        windowRosters        = {},
        windowStateByEvent   = {},
        webEvents            = {},    -- cache of fetched events for the launcher dropdown
        webEventsLoadedAt    = 0,

        -- Break-room: cached participant list for the currently-selected live event.
        -- Auto-refreshed every BREAK_ROOM_REFRESH_SEC by the d3d_present hook while a
        -- live event is selected. expanded snaps to true the first time anyone is on
        -- break or has a pending self-return so officers see the panel without an
        -- extra click; the user can collapse manually after that.
        breakRoom            = {
            participants     = {},
            canModerate      = false,
            loaded           = false,
            lastFetchAt      = 0,
            expanded         = false,
            autoExpanded     = false,
        },
        lastSyncSummary      = nil,   -- string shown in launcher after a sync attempt
        launcherCsvOnStart   = false, -- if true, Start & Post also writes the local CSV
        -- Compact view toggle (header checkbox). When true, the launcher
        -- hides everything except the Attendance, Loot Pool, and ToD
        -- Capturing sections so the panel takes less screen real estate
        -- during routine farm runs. False = full launcher with all panels.
        launcherCompact      = false,
        -- Per-window opacity multipliers, all driven by sliders in the
        -- Settings window. 1.0 = fully opaque (default), 0.0 = invisible.
        -- Same range/semantics across all three windows so the sliders
        -- read consistently.
        launcherMainAlpha    = 1.0,
        launcherCompactAlpha = 1.0,
        settingsAlpha        = 1.0,

        -- Per-monster day number inputs for the HNM Event Presets that
        -- traditionally track a day-of-monster counter (Fafnir, Behemoth,
        -- Adamantoise). Keys are the canonical resource name (Nidhogg,
        -- King Behemoth, Aspidochelone — same keys the preset list uses);
        -- values are the user's current input string. When the preset
        -- button is clicked with a non-empty day value, the event name
        -- carries a "D<n>" suffix end-to-end (queued / active list, ToD
        -- record, Discord activity).
        eventPresetDayInputs = {},
        lastScannedFor       = nil,   -- name of event the launcher last scanned for (avoids re-scanning the same selection)

        -- Attendance scope filter for multi-window (HNM / Claim-Kill) events.
        -- Narrows zone-scan results to just the player's party, the full
        -- alliance, or everyone in the credit zone (current behaviour).
        -- Values: 'party' | 'alliance' | 'zone'. Only the launcher UI for
        -- multi-window events surfaces the picker; non-HNM events always
        -- treat this as 'zone' regardless of the stored value.
        attendanceScopeFilter = 'zone',

        -- ToD Capture: ring buffer of recent HNM defeat lines plus dedup state.
        -- Newest capture lives at todCaptures[1]; nothing here is persisted.
        todCaptureEnabled    = true,
        todCaptureDebug      = false,  -- /lsm tod debug; logs every defeat-ish line
        todCaptures          = {},
        todLastCaptureKey    = nil,
        todLastCaptureClock  = 0,

        -- Loot Pool: roster cache used to populate the Winner combo on
        -- the per-item Post Loot form. Lazily filled by on_load_roster.
        rosterCache          = nil,    -- { fetchedAt, names = {...}, lootStructure }
        rosterFetching       = false,
        rosterError          = nil,
    }
end

return M
