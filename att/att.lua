-- att.lua (Refactored)
addon.name    = 'att'
addon.author  = 'Nils'
addon.version = '4.1.8'
addon.desc    = 'Attendance manager (Modular)'

require('common')

-- Setup package path to include the current directory (New Att)
-- Assuming this file is in .../att/New Att/
local folderPath = addon.path .. 'New Att\\'
package.path = package.path .. ';' .. folderPath .. '?.lua'

local imgui      = require('imgui')
local chat       = require('chat')
local struct     = require('struct')
local resources  = require('resources')
local memory     = require('memory')
local attendance = require('attendance')
local helpers    = require('helpers')
local ui         = require('ui')
local constants  = require('constants')
local comp       = require('comp')
local messages   = require('messages')
local settings   = require('settings')
local api        = require('api')

local config = settings.load(T{
    api = T{
        baseUrl  = '',
        pairings = T{},
    },
    -- Per-installation DKP defaults applied to events created from the
    -- addon. Edited via the gear-icon settings popup. Persisted to disk
    -- so the values survive /addon reload.
    eventDefaults = T{
        dkpPerHourRegular = 1,
        dkpPerWindowHnm   = 1,
        -- Whether the Queued Events / Active Events rows include the
        -- "(id N)" suffix. Toggle from the Settings popup.
        showEventIds      = false,
    },
    -- User-added monster names that the ToD / loot listener should match
    -- in addition to the built-in HNM and Testing tables. Edited from the
    -- Settings popup. Stored as a list of strings; persisted to disk.
    customMonsters = T{},
})

-- One-time migration from the legacy single-pairing schema (token, linkshellId,
-- linkshellName, label living directly on config.api) into the pairings list.
-- Reason: the legacy fields may still exist on disk from a previous version even
-- though they're no longer in the defaults; preserve the user's pairing instead
-- of forcing them to /att link again.
do
    local apiCfg = config.api
    if apiCfg.token and apiCfg.token ~= '' and #(apiCfg.pairings or {}) == 0 then
        apiCfg.pairings = T{
            T{
                token         = apiCfg.token,
                linkshellId   = apiCfg.linkshellId or 0,
                linkshellName = apiCfg.linkshellName or '',
                label         = apiCfg.label or '',
                channel       = 1,
            }
        }
        apiCfg.token         = nil
        apiCfg.linkshellId   = nil
        apiCfg.linkshellName = nil
        apiCfg.label         = nil
        settings.save()
    end
end

-- One-time migration: bump older 0-valued DKP defaults to the new 1
-- baseline. The schema default already says 1 for new installs, but
-- existing settings.xml files written under the previous default of 0
-- need a nudge so the Settings popup doesn't keep showing 0. Treats 0
-- as "unset" — anything else (incl. user-chosen 0) wouldn't match here
-- because users explicitly setting 0 would still be using the default.
do
    local d = config.eventDefaults
    if d then
        local migrated = false
        if (d.dkpPerHourRegular or 0) <= 0 then
            d.dkpPerHourRegular = 1
            migrated = true
        end
        if (d.dkpPerWindowHnm or 0) <= 0 then
            d.dkpPerWindowHnm = 1
            migrated = true
        end
        if migrated then settings.save() end
    end
end

-- Hand the API client a reference to the persisted config block so
-- pair() / unpair() update the same table that gets saved to disk.
api.set_config(config.api)

-- ToD Capture (HNM defeat-message detector) tunables. Session-only; not
-- persisted to disk. Bumping TOD_CAPTURE_MAX widens how far back the panel
-- scrolls; TOD_CAPTURE_DEDUP_SEC suppresses duplicate defeat lines for the
-- same monster within the window (server packet replay, etc.).
local TOD_CAPTURE_MAX        = 3
local TOD_CAPTURE_DEDUP_SEC  = 5
local ROSTER_CACHE_TTL_SEC   = 300  -- refetch roster after 5 min of staleness

-- Global State
local state = {
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
    autoCreateOnWrite    = false, -- if true and no event chosen, /att <name> creates one before posting
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
    lastSyncSummary      = nil,   -- string shown in launcher after a sync attempt
    launcherCsvOnStart   = false, -- if true, Start & Post also writes the local CSV
    lastScannedFor       = nil,   -- name of event the launcher last scanned for (avoids re-scanning the same selection)

    -- ToD Capture: ring buffer of recent HNM defeat lines plus dedup state.
    -- Newest capture lives at todCaptures[1]; nothing here is persisted.
    todCaptureEnabled    = true,
    todCaptureDebug      = false,  -- /att tod debug; logs every defeat-ish line
    todCaptures          = {},
    todLastCaptureKey    = nil,
    todLastCaptureClock  = 0,

    -- Loot Pool: roster cache used to populate the Winner combo on
    -- the per-item Post Loot form. Lazily filled by on_load_roster.
    rosterCache          = nil,    -- { fetchedAt, names = {...}, lootStructure }
    rosterFetching       = false,
    rosterError          = nil,
}

--------------------------------------------------------------------------------
-- INITIALIZATION
--------------------------------------------------------------------------------
ashita.events.register('load', 'att_load_cb', function()
    resources.load(addon.path)

    -- Alias display labels to existing credit zone entries so the launcher
    -- can show user-friendly names while the scan/lookup still works.
    -- Pattern: { displayName, sourceName }
    local aliases = {
        { 'Sky',                       'Sky/Kirin'    },
        { 'Fafnir/Nidhogg',            'Nidhogg'      },
        { 'Behemoth/King Behemoth',    'King Behemoth'},
        { 'Adamantoise/Aspidochelone', 'Aspidochelone'},
    }
    for _, pair in ipairs(aliases) do
        local newName, src = pair[1], pair[2]
        if resources.attCreditNames and resources.attCreditNames[src] then
            resources.attCreditNames[newName] = resources.attCreditNames[src]
        end
        if resources.attCreditZoneIds and resources.attCreditZoneIds[src] then
            resources.attCreditZoneIds[newName] = resources.attCreditZoneIds[src]
        end
        if resources.attSearchArea and resources.attSearchArea[src] then
            resources.attSearchArea[newName] = resources.attSearchArea[src]
        end
    end
end)

--------------------------------------------------------------------------------
-- UTILS
--------------------------------------------------------------------------------
local function ls_prefix()
    return (state.g_LSMode == 'ls2') and '/l2 ' or '/l '
end

-- Returns 'linkshell' or 'linkshell2' for /sea targeting based on the launcher's
-- multi-select. Prefers LS1 if both are checked. Falls back to LS1 if neither.
local function ls_search_param()
    if state.lsChannels.ls1 then return 'linkshell' end
    if state.lsChannels.ls2 then return 'linkshell2' end
    return 'linkshell'
end

-- Sends a chat message to every linkshell channel selected in the launcher.
local function broadcast_to_selected_ls(message)
    if state.lsChannels.ls1 then
        AshitaCore:GetChatManager():QueueCommand(1, '/l ' .. message)
    end
    if state.lsChannels.ls2 then
        AshitaCore:GetChatManager():QueueCommand(1, '/l2 ' .. message)
    end
end

local function prep_write_targets(mode, eventName)
    return attendance.write_file(addon.path, mode, eventName) -- Dry run or path prep?
    -- Actually attendance.write_file writes it. We might want just the path/msg first?
    -- Refactored attendance.write_file handles opening and writing.
    -- We'll just call it when needed.
end

local function update_suggestions()
    local zid = memory.get_current_zone_id()
    local evs, zname = attendance.resolve_events_for_zone(zid)
    
    -- Sort logic (needs category order from resources)
    if evs and #evs > 1 then
        local order = {}
        local idx = 1
        for _, cat in ipairs(resources.attendCategoriesOrder) do
            for _, ev in ipairs(resources.attendCategories[cat] or {}) do
                order[ev] = idx
                idx = idx + 1
            end
        end
        table.sort(evs, function(a, b)
            local oa = order[a] or 999999
            local ob = order[b] or 999999
            if oa ~= ob then return oa < ob end
            return a < b
        end)
    end
    state.suggestions = { evs = evs or {}, zone = zname }
    state.lastDetectedZid = zid
end

local function queue_attend_launch(eventName)
    local area = resources.attCreditNames[eventName] and resources.attCreditNames[eventName][1]
    if resources.attSearchArea[eventName] then area = resources.attSearchArea[eventName] end
    
    if not area or area == '' then
        print(string.format('[att] No search area found for "%s".', eventName))
        return
    end

    local lsSearch = ls_search_param()
    AshitaCore:GetChatManager():QueueCommand(1, string.format('/sea %s %s', area, lsSearch))

    local delay = tonumber(state.attendDelaySec) or 2
    if delay < 0 then delay = 0 end

    state.pendingAttend = {
        eventName  = eventName,
        useLS2     = (lsSearch == 'linkshell2'),
        fireAt     = os.clock() + delay,
    }
end

--------------------------------------------------------------------------------
-- COMMAND HANDLERS
--------------------------------------------------------------------------------
-- /att
ashita.events.register('command', 'att_command_cb', function(e)
    local args = e.command:args()
    if #args == 0 or args[1]:lower() ~= '/att' then return end
    e.blocked = true

    -- Web sync: /att server <url>
    if #args >= 3 and args[2]:lower() == 'server' then
        local url = args[3]
        for i = 4, #args do url = url .. ' ' .. args[i] end

        if not url:lower():match('^https?://') then
            print(chat.header('att') .. 'Invalid URL: must start with http:// or https://')
            return
        end

        api.set_base_url(url)
        settings.save()
        print(chat.header('att') .. 'Web server set to: ' .. (config.api.baseUrl ~= '' and config.api.baseUrl or '<empty>'))

        -- Verify the URL actually points at an LSManager server before we
        -- leave the user wondering whether the typo took. Probe is a quick
        -- GET; any HTTP response (incl. 401) means reachable.
        local ok, info = api.probe()
        if ok then
            print(chat.header('att') .. string.format(
                'Server OK (HTTP %s). Use /att link <code> [1|2] to pair.',
                tostring(info)))
        else
            print(chat.header('att') .. 'Probe FAILED: ' .. tostring(info))
            print(chat.header('att') .. 'The URL was saved, but the server is not responding. Check the URL and try again.')
        end
        return
    end

    -- Web sync: /att link <code> [1|2]
    -- Channel is the in-game pearl slot the linkshell is worn on. Defaults to 1.
    -- Pairing on a channel that already has one replaces the existing pairing.
    if #args >= 3 and args[2]:lower() == 'link' then
        local code    = args[3]
        local channel = tonumber(args[4]) or 1
        if channel ~= 1 and channel ~= 2 then
            print(chat.header('att') .. 'Channel must be 1 or 2.')
            return
        end
        local result, err = api.pair(code, channel)
        if result then
            settings.save()
            print(chat.header('att') .. string.format('Linked to %s on LS%d%s',
                result.linkshellName or '<linkshell>',
                channel,
                (result.label and result.label ~= '') and (' [' .. result.label .. ']') or ''))
            state.linkedEventId = nil
            state.windowMax = 1
            state.windowSequence = 0
            state.windowRosters = {}
            state.windowStateByEvent = {}
        else
            print(chat.header('att') .. 'Pair failed: ' .. tostring(err))
        end
        return
    end

    -- Web sync: /att unlink [1|2|all]
    if #args >= 2 and args[2]:lower() == 'unlink' then
        local target = (args[3] or 'all'):lower()
        if target == 'all' then
            api.unpair()
        elseif target == '1' or target == '2' then
            api.unpair(tonumber(target))
        else
            print(chat.header('att') .. 'Usage: /att unlink [1|2|all]')
            return
        end
        settings.save()
        state.linkedEventId = nil
        state.windowMax = 1
        state.windowSequence = 0
        state.windowRosters = {}
        state.windowStateByEvent = {}
        if target == 'all' then
            print(chat.header('att') .. 'Unlinked all pairings. Local CSV writes still work.')
        else
            print(chat.header('att') .. 'Unlinked LS' .. target .. '.')
        end
        return
    end

    -- Web sync: /att status (or /att list — same thing)
    if #args == 2 and (args[2]:lower() == 'status' or args[2]:lower() == 'list') then
        print(chat.header('att') .. 'Server: ' .. (config.api.baseUrl ~= '' and config.api.baseUrl or '<not set>'))
        local pairings = api.list_pairings()
        if #pairings == 0 then
            print(chat.header('att') .. 'Not linked. Use /att link <code> [1|2] after generating one on the website.')
        else
            for _, p in ipairs(pairings) do
                print(chat.header('att') .. string.format('  LS%d: %s (id %s)%s',
                    p.channel,
                    p.linkshellName or '?',
                    tostring(p.linkshellId or '?'),
                    (p.label and p.label ~= '') and (' [' .. p.label .. ']') or ''))
            end
        end
        return
    end

    if #args == 2 and args[2]:lower() == 'help' then
        state.isHelpWindowOpen = true
        return
    end

    -- /att tod debug [on|off|toggle]   — diagnose missed defeat captures by
    -- printing every chat line that mentions defeat keywords, with the chat
    -- mode and whether a known monster matched. Default toggles the flag.
    if #args >= 2 and args[2]:lower() == 'tod' then
        local sub = (args[3] or ''):lower()
        if sub == 'debug' then
            local arg2 = (args[4] or 'toggle'):lower()
            if arg2 == 'on' then
                state.todCaptureDebug = true
            elseif arg2 == 'off' then
                state.todCaptureDebug = false
            else
                state.todCaptureDebug = not state.todCaptureDebug
            end
            print(chat.header('att') .. 'ToD debug ' .. (state.todCaptureDebug and 'ON' or 'OFF'))
        elseif sub == 'clear' then
            state.todCaptures = {}
            state.todLastCaptureKey = nil
            state.todLastCaptureClock = 0
            print(chat.header('att') .. 'ToD captures cleared.')
        else
            print(chat.header('att') .. 'Usage: /att tod debug [on|off|toggle] | /att tod clear')
        end
        return
    end

    -- /att debug
    if #args == 2 and args[2]:lower() == 'debug' then
        state.isDebugWindowOpen = not state.isDebugWindowOpen
        return
    end

    -- /att debugmode
    if #args == 2 and args[2]:lower() == 'debugmode' then
        state.debugMode = not state.debugMode
        memory.debug = state.debugMode
        attendance.debug = state.debugMode
        print(chat.header('att') .. 'Debug Mode: ' .. (state.debugMode and 'ON (Verbose)' or 'OFF'))
        return
    end

    -- /att here
    if #args == 2 and args[2]:lower() == 'here' then
        local zid = memory.get_current_zone_id()
        local evs, _ = attendance.resolve_events_for_zone(zid)
        if evs and evs[1] then
            AshitaCore:GetChatManager():QueueCommand(1, string.format('/att ls "%s"', evs[1]))
        else
            print('[att] No event found for current zone.')
        end
        return
    end
    
    -- /att memscan
    if #args == 2 and args[2]:lower() == 'memscan' then
        local ptr = memory.find_entity_list()
        if ptr ~= 0 then
             print(string.format('[att] Suggested Pointer: 0x%08X', ptr))
        else
             print('[att] Could not find Entity List via signature.')
        end
        return
    end

    -- /att memdump <addr> [count]
    if #args >= 3 and args[2]:lower() == 'memdump' then
        local addr = args[3]
        local cnt  = tonumber(args[4]) or 64
        memory.dump_address(addr, cnt)
        return
    end

    -- /att api (DEBUG)
    if #args == 2 and args[2]:lower() == 'api' then
        local entMgr = AshitaCore:GetMemoryManager():GetEntity()
        print('[att] Dumping Entity Manager Methods:')
        -- Try to iterate metatable
        local meta = getmetatable(entMgr)
        if meta then
            for k, v in pairs(meta) do
                print(' - ' .. tostring(k) .. ' (' .. type(v) .. ')')
            end
        else
            print(' - No metatable found (UserData?)')
        end
        return
    end

    -- /att all
    if #args == 2 and args[2]:lower() == 'all' then
        attendance.clear()
        state.selectedMode = 'HNM' 
        state.g_LSMode = 'ls'
        state.pendingEventName = 'Global Search'
        
        -- Dynamic resource mapping to support "all" area
        resources.attSearchArea['Global Search'] = 'all'
        -- Ensure credit works (though we mainly rely on search results, which add_entry accepts if we don't filter strictly?)
        -- Actually gather_zone filters by zid_in_credit. We need to bypass that or ensure 'all' matches?
        -- For now, this is just for the SEARCH command. 
        -- The user said "/sea all linkshell" and the letter button.
        -- Gathering usually happens via "Rescan" which calls gather_zone. 
        -- If we want the results of /sea to appear, we rely on the packet handler adding them?
        -- Existing packet handler ? No, it's SA mode packet handler.
        -- Wait, standard ATT relies on memory scanning (/sea results populate memory?).
        -- Yes, Ashita memory manager reads entity list.
        -- So we need `attendance.gather_zone` to NOT filter by zone if name is 'Global Search'.
        
        AshitaCore:GetChatManager():QueueCommand(1, '/sea all linkshell')
        state.isAttendanceWindowOpen = true
        return
    end

    -- Reset
    attendance.clear()
    state.selectedMode = 'HNM'
    state.g_LSMode     = nil
    state.scanNextLetter      = nil
    state.pendingSeaScan      = nil

    local lsMode, writeMode = nil, nil
    local aliasParts = {}

    for i = 2, #args do
        local a  = args[i]
        local al = a:lower()
        if     al == 'ls'  then lsMode    = 'ls'
        elseif al == 'ls2' then lsMode    = 'ls2'
        elseif al == 'h'   then writeMode = 'HNM'
        elseif al == 'e'   then writeMode = 'Event'
        else table.insert(aliasParts, a) end
    end

    local alias = table.concat(aliasParts, ' '):gsub('^"(.*)"$', '%1')
    state.g_LSMode = lsMode

    -- Resolve Event
    if alias ~= '' then
        state.pendingEventName = resources.attShortNames[alias:lower()] or alias
    else
        state.pendingEventName = 'Current Zone'
        state.skipNextSearch = true -- Skip /sea scan for pure /att
    end

    -- Special handling for Current Zone
    if state.pendingEventName == 'Current Zone' then
        local zid = memory.get_current_zone_id()
        local zname = resources.attZoneList[zid] or 'UnknownZone'
        resources.attCreditNames['Current Zone']   = { zname }
        resources.attCreditZoneIds['Current Zone'] = { [zid] = true }
    end

    -- POPULATION / GATHER FLOW
    -- 1. Determine Search Area
    local area = resources.attCreditNames[state.pendingEventName] and resources.attCreditNames[state.pendingEventName][1]
    if resources.attSearchArea[state.pendingEventName] then area = resources.attSearchArea[state.pendingEventName] end
    
    local doSearch = true
    if state.skipNextSearch then
        doSearch = false
        state.skipNextSearch = false
    end
    
    if not saFlag and area and area ~= '' and doSearch then
        -- Trigger Search
        local lsSearch = ls_search_param()

        -- Queue /sea command
        AshitaCore:GetChatManager():QueueCommand(1, string.format('/sea %s %s', area, lsSearch))
        print(string.format('[att] Scanning %s (Waiting for results)...', area))
        
        -- Set Pending State
        local delay = tonumber(state.attendDelaySec) or 2
        state.pendingGather = {
            eventName = state.pendingEventName,
            fireAt    = os.clock() + delay,
            writeMode = writeMode  -- If set, will write file after gather
        }
        
        -- DELAY WINDOW OPENING UNTIL GATHER COMPLETE
        -- state.isAttendanceWindowOpen = true
        return
    else
        -- Fallback OR Skipped Search: Immediate Gather
        if lsMode then
            attendance.gather_zone(state.pendingEventName)
        else
            -- attendance.gather_alliance(state.pendingEventName) -- Removed
            attendance.gather_zone(state.pendingEventName)
        end
        
        if writeMode then
            state.selectedMode = writeMode
            local count, msg = attendance.write_file(addon.path, writeMode, state.pendingEventName)
            if count and msg then
                AshitaCore:GetChatManager():QueueCommand(1, ls_prefix() .. msg)
            end
        end
        state.isAttendanceWindowOpen = true
    end
end)

-- /attend
ashita.events.register('command', 'att_attend_cmd', function(e)
    local args = e.command:args()
    if #args == 0 or args[1]:lower() ~= '/attend' then return end
    e.blocked = true
    
    -- Toggle/Open logic
    if #args > 1 and args[2]:lower() == 'close' then
        state.isAttendLauncherOpen = false
    else
        state.isAttendLauncherOpen = not state.isAttendLauncherOpen
    end

    if state.isAttendLauncherOpen then
        -- Flag picked up by ui.draw_launcher to force the window back to
        -- our preferred default dimensions on every fresh /attend, so a
        -- prior manual resize (cached by imgui.ini) doesn't shrink the
        -- launcher next time the user opens it.
        state.launcherSizePending = true
        update_suggestions()
        -- Seed the launcher roster with the local player so they show up
        -- immediately, before any scan. attendance.add_self() is a no-op if
        -- the user is already in attendance.data.
        attendance.add_self()
        -- Pull the latest queued events from the web app so they appear in
        -- the Queued Events list without requiring a manual Refresh.
        if api.is_paired() then
            local events, err = api.list_events()
            if events then
                state.webEvents = events
                state.webEventsLoadedAt = os.time()
            elseif err then
                print(chat.header('att') .. 'Could not load events: ' .. tostring(err))
            end
        end
    end
end)

-- /comp
ashita.events.register('command', 'att_comp_cmd', function(e)
    local args = e.command:args()
    if #args == 0 or args[1]:lower() ~= '/comp' then return end
    e.blocked = true

    if #args < 2 then
        print('[att] Usage: /comp <event_name> | /comp list')
        return
    end

    if args[2]:lower() == 'list' then
        print('[att] Available Compositions:')
        local keys = {}
        for k in pairs(resources.compositions) do table.insert(keys, k) end
        table.sort(keys)
        for _, k in ipairs(keys) do
            print(' - ' .. k)
        end
        return
    end

    local aliasParts = {}
    for i = 2, #args do table.insert(aliasParts, args[i]) end
    local alias = table.concat(aliasParts, ' '):gsub('^"(.*)"$', '%1'):lower()
    
    -- Resolve Event Name
    local eventName = resources.attShortNames[alias] or alias
    -- Try case-insensitive match on compositions keys if not found
    if not resources.compositions[eventName] then
        for k, v in pairs(resources.compositions) do
            if k:lower() == eventName:lower() then
                eventName = k
                break
            end
        end
    end
    -- Try substring match if still not found
    if not resources.compositions[eventName] then
        for k, v in pairs(resources.compositions) do
            if k:lower():find(alias, 1, true) then
                eventName = k
                break
            end
        end
    end

    if not resources.compositions[eventName] then
        print('[att] No composition found for event: ' .. eventName)
        return
    end

    -- Refresh roster first
    -- Determine area to scan
    local area = resources.attCreditNames[eventName] and resources.attCreditNames[eventName][1]
    area = resources.attSearchArea[eventName] or area
    
    if area and area ~= '' then
        -- Trigger search first
        local lsSearch = ls_search_param()
        local delay = tonumber(state.attendDelaySec) or 2
        AshitaCore:GetChatManager():QueueCommand(1, string.format('/sea %s %s', area, lsSearch))
        
        print('[att] Scanning ' .. area .. ' for ' .. eventName .. ' (Please wait)...')
        
        state.pendingComp = {
            eventName = eventName,
            fireAt = os.clock() + delay
        }
    else
        print('[att] Could not determine zone for ' .. eventName)
    end
end)

--------------------------------------------------------------------------------
-- TEXT_IN: HNM defeat-message detector (ToD Capture)
--------------------------------------------------------------------------------
-- Listens to incoming chat lines on the battle-log channels and, when one
-- of the canonical preset HNMs is reported as defeated, pushes a record
-- onto state.todCaptures so the launcher can show a "ToD Captured!" panel.
-- Display-only: no HTTP, no mutation of e.message_modified or e.blocked.
ashita.events.register('text_in', 'att_tod_capture_cb', function(e)
    if not state.todCaptureEnabled then return end
    if e.blocked then return end
    if e.injected then return end -- ignore other addons echoing lines

    -- Mode filter intentionally OFF. Different FFXI clients and private
    -- servers route battle-defeat text through different log types (121,
    -- 122, 36, ...). The patterns below are specific enough that random
    -- chat won't false-match, so we let any incoming line through and let
    -- the pattern engine be the gate.
    local raw = e.message_modified or e.message
    local clean = helpers.clean_str(raw or '')
    if clean == '' then return end

    -- Match against canonical singles (Tiamat, "King Behemoth", etc.) and
    -- the testing presets. Iterating constants tables (not state.linkedEventName)
    -- keeps capture independent of the linked event, and works for
    -- slash-joined preset labels like "Behemoth/King Behemoth" since the
    -- chat line always uses the canonical single name.
    -- Match a defeat line against any monster name. Accepts either a
    -- pairs-iterated table (keys = names, like HNM_WINDOW_COUNTS) or a
    -- list-iterated table (values = names, like config.customMonsters).
    local function defeat_pattern_hits(monsterName)
        local esc = monsterName:gsub('(%W)', '%%%1')
        return clean:find('defeats the ' .. esc .. '%.', 1)
            or clean:find('[Tt]he ' .. esc .. ' was defeated by', 1)
            or clean:find('[Tt]he ' .. esc .. ' falls to the ground', 1)
    end

    local function find_defeat_match(tbl)
        for monsterName, _ in pairs(tbl) do
            if defeat_pattern_hits(monsterName) then return monsterName end
        end
        return nil
    end

    local function find_defeat_match_list(list)
        for _, monsterName in ipairs(list or {}) do
            if monsterName ~= '' and defeat_pattern_hits(monsterName) then
                return monsterName
            end
        end
        return nil
    end

    local hitName = find_defeat_match(constants.HNM_WINDOW_COUNTS)
                 or find_defeat_match(constants.TESTING_MONSTERS)
                 or find_defeat_match(constants.SKY_FARM_NMS)
                 or find_defeat_match_list(config.customMonsters)

    -- Diagnostic: when /att tod debug is on, print every chat line that
    -- mentions "defeats" / "defeated" / "falls" so we can see what mode &
    -- wording the server actually uses if a kill was missed.
    if state.todCaptureDebug
       and (clean:find('defeats') or clean:find('defeated') or clean:find('falls')) then
        print(chat.header('att') .. string.format('[tod-debug] mode=%s match=%s :: %s',
            tostring(e.mode), tostring(hitName or '<none>'), clean))
    end

    -- Loot pool detection: "You find <a/an/the> <item> on the <monster>."
    -- Items are attributed to the most recent capture in the ring buffer
    -- whose monster matches. Drop is silently dropped if the defeat line
    -- hasn't fired yet (lag) so we don't accidentally attribute it to a
    -- stale capture. Runs alongside defeat matching so a single chat line
    -- can't be both — defeat matches return early below; loot matches
    -- append and return here without falling through.
    if not hitName then
        local function loot_capture_for(monsterName)
            local esc = monsterName:gsub('(%W)', '%%%1')
            return clean:match('^You find ([^%.]+) on the ' .. esc .. '%.$')
        end

        local function find_loot_match(tbl)
            for monsterName, _ in pairs(tbl) do
                local item = loot_capture_for(monsterName)
                if item then return monsterName, item end
            end
            return nil, nil
        end

        local function find_loot_match_list(list)
            for _, monsterName in ipairs(list or {}) do
                if monsterName ~= '' then
                    local item = loot_capture_for(monsterName)
                    if item then return monsterName, item end
                end
            end
            return nil, nil
        end

        local lootMonster, lootItem = find_loot_match(constants.HNM_WINDOW_COUNTS)
        if not lootMonster then
            lootMonster, lootItem = find_loot_match(constants.TESTING_MONSTERS)
        end
        if not lootMonster then
            lootMonster, lootItem = find_loot_match(constants.SKY_FARM_NMS)
        end
        if not lootMonster then
            lootMonster, lootItem = find_loot_match_list(config.customMonsters)
        end

        if state.todCaptureDebug and clean:find('You find') then
            print(chat.header('att') .. string.format(
                '[loot-debug] mode=%s monster=%s item=%s :: %s',
                tostring(e.mode), tostring(lootMonster or '<none>'),
                tostring(lootItem or '<none>'), clean))
        end

        if lootMonster and lootItem then
            -- Strip leading article so "a Goblin mask" becomes "Goblin mask".
            lootItem = lootItem:gsub('^[Aa]n? ', '')
                                :gsub('^[Tt]he ', '')
                                :gsub('^[Ss]ome ', '')
                                :gsub('%s+$', '')
            -- Find the most recent matching capture (newest at index 1)
            -- and append. If none, the drop is dropped silently.
            for _, cap in ipairs(state.todCaptures or {}) do
                if cap.monster == lootMonster then
                    cap.lootDrops = cap.lootDrops or {}
                    table.insert(cap.lootDrops, {
                        itemName   = lootItem,
                        detectedAt = os.time(),
                        draftOpen  = false,
                        draft      = { winner = '', dkpSpent = '' },
                        posting    = false,
                        posted     = nil,
                        postError  = nil,
                    })
                    print(chat.header('att') .. 'Loot detected: '
                        .. lootItem .. ' (' .. lootMonster .. ')')
                    return
                end
            end
        end
        return
    end

    -- Dedup window: same monster within TOD_CAPTURE_DEDUP_SEC is treated as
    -- a duplicate. A different monster within the window still records.
    local now = os.clock()
    if state.todLastCaptureKey == hitName
       and (now - state.todLastCaptureClock) < TOD_CAPTURE_DEDUP_SEC then
        return
    end

    -- The verbatim chat line, trimmed only of trailing whitespace. We rely
    -- on Captured at (wall-clock at callback time, second precision) for
    -- the actual ToD value, since FFXI's optional [HH:MM:SS] chat-prefix
    -- requires a client setting most users don't have on and would just
    -- duplicate Captured at anyway.
    local stripped = clean:gsub('%s+$', '')

    table.insert(state.todCaptures, 1, {
        monster      = hitName,
        message      = stripped,
        callbackAt   = constants.format_posted_at(os.date('*t')),
        callbackSec  = os.time(),
    })
    while #state.todCaptures > TOD_CAPTURE_MAX do
        table.remove(state.todCaptures)
    end
    state.todLastCaptureKey   = hitName
    state.todLastCaptureClock = now

    -- Echo the capture to chat so the user has visual feedback even when
    -- the launcher window isn't open.
    print(chat.header('att') .. 'ToD Captured: ' .. hitName
        .. ' :: "' .. tostring(stripped) .. '"')
end)

--------------------------------------------------------------------------------
-- D3D PRESENT
--------------------------------------------------------------------------------
ashita.events.register('d3d_present', 'att_present_cb', function()
    -- Show an ImGui-drawn cursor whenever any att window is open. Has a
    -- known DPI-related visual offset in fullscreen on HorizonXI/sugar
    -- bindings; clicks still register at the correct location.
    do
        local anyWindowOpen = state.isAttendanceWindowOpen
            or state.isAttendLauncherOpen
            or state.isHelpWindowOpen
            or state.isDebugWindowOpen
            or (comp and comp.isOpen)
        local io_ok, io = pcall(imgui.GetIO)
        if io_ok and io then io.MouseDrawCursor = anyWindowOpen and true or false end
    end

    -- Pending Attend Launch
    if state.pendingAttend and os.clock() >= state.pendingAttend.fireAt then
        local lsFlag = state.pendingAttend.useLS2 and 'ls2' or 'ls'
        local cmd = string.format('/att %s "%s"', lsFlag, state.pendingAttend.eventName)
        state.skipNextSearch = true
        AshitaCore:GetChatManager():QueueCommand(1, cmd)
        state.attForceRefreshAt = os.clock() + 0.05
        state.pendingAttend = nil
    end

    -- Deferred LS broadcast: Start & Post sends "Event started" first, then
    -- queues "Attendance taken" through here so the two /l commands are not
    -- fired on the same frame. Back-to-back chat sends produce "A command
    -- error occurred" on the second one because the game still has the first
    -- in flight when the second arrives.
    if state.pendingLSMessage and os.clock() >= state.pendingLSMessage.fireAt then
        broadcast_to_selected_ls(state.pendingLSMessage.message)
        state.pendingLSMessage = nil
    end

    -- Pending Sea Scan
    if state.pendingSeaScan and os.clock() >= state.pendingSeaScan.fireAt then
        attendance.gather_zone(state.pendingEventName)
        state.pendingSeaScan = nil
    end

    -- Comp Async Evaluation
    if state.pendingComp and os.clock() >= state.pendingComp.fireAt then
        local ev = state.pendingComp.eventName
        attendance.clear()
        -- FIX: Gather Alliance FIRST, then Zone. 
        -- gather_zone respects existing entries in data, gather_alliance does not checks.
        -- gather_zone respects existing entries in data
        -- attendance.gather_alliance(ev) -- Removed as per user request (redundant/broken)
        attendance.gather_zone(ev)
        
        local res, err = comp.evaluate(ev, attendance.data)
        if not res then
            print('[att] Error evaluating: ' .. tostring(err))
        else
            -- Auto-Build Parties
            print('[att] Auto-Building Parties for ' .. ev)
            local bpRes, bpErr = comp.build_parties(ev, attendance.data)
            if not bpRes then print('[att] Build Error: ' .. tostring(bpErr)) end
        end
        state.pendingComp = nil
    end

    -- Pending Gather (Normal /att flow)
    if state.pendingGather and os.clock() >= state.pendingGather.fireAt then
        local ev = state.pendingGather.eventName
        -- Clear old data? Yes, usually refreshing.
        attendance.clear()

        -- Gather
        -- Gather
        -- attendance.gather_alliance(ev) -- Removed
        attendance.gather_zone(ev)     -- Then scan zone/search results

        -- Handle Write if requested
        if state.pendingGather.writeMode then
            state.selectedMode = state.pendingGather.writeMode
            local count, msg = attendance.write_file(addon.path, state.pendingGather.writeMode, ev)
            if count and msg then
                AshitaCore:GetChatManager():QueueCommand(1, ls_prefix() .. msg)
            end
        end

        state.isAttendanceWindowOpen = true
        state.pendingGather = nil
    end

    -- Launcher Gather (/attend in-window auto-scan; does NOT open the standalone window)
    if state.launcherGather and os.clock() >= state.launcherGather.fireAt then
        local lg = state.launcherGather
        attendance.clear()
        if lg.isAutoCreate then
            attendance.gather_current_zone()
        else
            attendance.gather_zone(lg.eventName)
        end
        state.launcherGather = nil
    end

    -- Auto Refresh Suggestions
    local zidNow = memory.get_current_zone_id()
    if zidNow ~= state.lastDetectedZid then
        update_suggestions()
    end
    
    if state.attForceRefreshAt and os.clock() >= state.attForceRefreshAt then
        update_suggestions()
        state.attForceRefreshAt = nil
    end

    -- CALLBACKS for UI
    local callbacks = {
        -- Persisted settings the launcher reads directly (DKP rates etc.)
        event_defaults = config.eventDefaults,

        on_party_only = function()
            local pm = AshitaCore:GetMemoryManager():GetParty()
            if not pm then return end
            
            local partyNames = {}
            for i = 0, 17 do
                local name = pm:GetMemberName(i)
                if name and type(name) == 'string' and #name > 0 then
                    partyNames[name:lower()] = true
                end
            end
            
            local filtered = {}
            for _, r in ipairs(attendance.data) do
                local cleanName = r.name:gsub('^X%s+', ''):lower()
                if partyNames[cleanName] then
                    table.insert(filtered, r)
                end
            end
            attendance.data = filtered
        end,
        on_write = function(close)
            local _, msg = attendance.write_file(addon.path, state.selectedMode, state.pendingEventName)
            if msg then AshitaCore:GetChatManager():QueueCommand(1, ls_prefix() .. msg) end

            -- Web sync: also POST attendance to LSManager if paired.
            if api.is_paired() then
                local entries = {}
                for _, row in ipairs(attendance.data) do
                    if not row.name:match('^X ') then
                        entries[#entries + 1] = {
                            characterName = row.name,
                            mainJob       = row.jobsMain,
                            subJob        = row.jobsSub,
                            zone          = row.zone
                        }
                    end
                end

                local targetEventId = state.linkedEventId
                if not targetEventId and state.autoCreateOnWrite then
                    local created, cerr = api.create_event(state.pendingEventName, state.selectedMode, nil)
                    if created and created.eventId then
                        targetEventId = created.eventId
                        state.linkedEventId = created.eventId
                        state.linkedEventName = created.name
                        print(chat.header('att') .. 'Auto-created event: ' .. tostring(created.name)
                            .. ' (id ' .. tostring(created.eventId) .. ')')
                    else
                        state.lastSyncSummary = 'Auto-create failed: ' .. tostring(cerr)
                        print(chat.header('att') .. state.lastSyncSummary)
                    end
                end

                if targetEventId and #entries > 0 then
                    local result, perr = api.post_attendance(targetEventId, entries)
                    if result then
                        local unmatched = result.unmatched or {}
                        local summary = string.format('Synced %d / Reported %d - %d unmatched',
                            result.matched or 0, #entries, #unmatched)
                        if #unmatched > 0 then
                            local sample = {}
                            for i = 1, math.min(5, #unmatched) do sample[i] = unmatched[i] end
                            summary = summary .. ': ' .. table.concat(sample, ', ')
                            if #unmatched > 5 then summary = summary .. ', ...' end
                        end
                        state.lastSyncSummary = summary
                        print(chat.header('att') .. summary)
                    else
                        state.lastSyncSummary = 'Web sync failed: ' .. tostring(perr)
                        print(chat.header('att') .. state.lastSyncSummary)
                    end
                elseif #entries == 0 then
                    state.lastSyncSummary = 'Nothing to sync (no confirmed entries).'
                elseif not targetEventId then
                    state.lastSyncSummary = 'No web event selected. Open the launcher to pick one or enable auto-create.'
                end
            end

            if close then
                state.isAttendanceWindowOpen = false
            end
        end,
        on_launch_event = function(ev)
             queue_attend_launch(ev)
        end,
        on_update_zone = function() update_suggestions() end,
        on_scan_letter = function(letter)
             local area = resources.attCreditNames[state.pendingEventName] and resources.attCreditNames[state.pendingEventName][1]
             area = resources.attSearchArea[state.pendingEventName] or area
             if area and area ~= '' then
                 local lsSearch = (state.g_LSMode == 'ls2') and 'linkshell2' or 'linkshell'
                 AshitaCore:GetChatManager():QueueCommand(1, string.format('/sea %s %s %s', area, lsSearch, letter))
                 
                 state.pendingSeaScan = { fireAt = os.clock() + (state.attendDelaySec or 2) }
             end
        end,
        -- Launcher: refresh the local roster directly from the entity list.
        -- No /sea is fired (so the FFXI Search window doesn't pop), and the
        -- scan happens immediately — no delay needed.
        on_launcher_scan = function(eventName, isAutoCreate)
            attendance.clear()
            if isAutoCreate then
                attendance.gather_current_zone()
            else
                attendance.gather_zone(eventName)
            end
            attendance.add_self()
            -- Clear any pending gather indicator from prior scans.
            state.launcherGather = nil
        end,

        -- Event Preset button: clicking "Nidhogg" (or any preset) creates that
        -- event in the web app, makes it the active session event, and refreshes
        -- the local roster from the entity list. Does NOT fire any /sea command.
        -- `category` is the resource bucket the preset came from (HNMS / NMS /
        -- HENMs / Events) and drives the EventType we record server-side.
        on_preset_button = function(eventName, category, opts)
            opts = opts or {}
            state.pendingEventName = eventName
            state.scanNextLetter = nil
            -- HNM Style (post-by-window attendance) engages for the HNMS
            -- preset category. NMS / HENMs / Events keep single-window
            -- (regular) attendance even when the event name appears in the
            -- HNM window-count table. The Testing category opts in
            -- per-monster via TESTING_MONSTERS so QA presets can exercise
            -- both flows from the same UI.
            local testStyle = constants.testing_style_for(eventName) -- 'HNM' / 'Regular' / nil
            local isHnmCategory = (category == 'HNMS')
                or (category == 'Testing' and testStyle == 'HNM')
            state.windowMax = isHnmCategory and constants.window_count_for(eventName) or 1
            state.windowSequence = 0
            state.windowRosters = {}

            -- Mirror category-driven HNM style into selectedMode so downstream
            -- code that branches on it (LS chat template HNM_TAKEN vs
            -- EVENT_TAKEN, attendance.write_file CSV format, fallback create
            -- in on_start_and_post) picks the right path for this preset.
            state.selectedMode = isHnmCategory and 'HNM' or 'Event'

            -- Map the preset's category into the EventType field the website
            -- displays (and now offers as a dropdown):
            --   HNMS    -> "HNM"
            --   HENMs   -> "HENM"
            --   NMS     -> "HNM"  (NMs are HNM-style for chat/templating purposes)
            --   Events  -> the event name itself ("Sky", "Sea", "Dynamis", "Limbus")
            --   Testing -> "HNM" or "Event" so test runs land in the same
            --              server-side bucket as real runs of the same style.
            -- For unknown categories fall back to the runtime selectedMode.
            local resolvedType
            if category == 'HNMS' or category == 'NMS' then
                resolvedType = 'HNM'
            elseif category == 'HENMs' then
                resolvedType = 'HENM'
            elseif category == 'Events' then
                resolvedType = eventName
            elseif category == 'Testing' then
                resolvedType = isHnmCategory and 'HNM' or 'Event'
            else
                resolvedType = state.selectedMode
            end

            -- DKP rate: HNM-style events use the per-window input; all
            -- other categories use the per-hour input. The opts table is
            -- supplied by ui.lua's preset button click handler and pulls
            -- from the visible DKP / Hour and DKP / Window text fields.
            local dkpRate = isHnmCategory and opts.dkpPerWindow or opts.dkpPerHour

            -- 1. Create on the web app if paired.
            if api.is_paired() then
                local created, err = api.create_event(eventName, resolvedType, nil, dkpRate)
                if created and created.eventId then
                    state.linkedEventId = created.eventId
                    state.linkedEventName = created.name or eventName
                    print(chat.header('att') .. 'Created event: ' .. (created.name or eventName)
                        .. ' (id ' .. tostring(created.eventId) .. ')')
                    -- Refresh the events list so the new event appears in Queued Events.
                    local events = api.list_events()
                    if events then state.webEvents = events end
                else
                    state.lastSyncSummary = 'Create failed: ' .. tostring(err)
                    print(chat.header('att') .. state.lastSyncSummary)
                end
            end

            -- 2. Refresh roster directly from the entity list (no /sea).
            attendance.clear()
            attendance.gather_zone(eventName)
            attendance.add_self()
        end,

        -- Launcher: combined Start/Create+Start + post attendance + LS msgs + optional CSV.
        -- opts: { eventId, eventName, isAutoCreate, csvOnStart }
        -- Returns a one-line summary string for state.lastSyncSummary.
        on_start_and_post = function(opts)
            if not api.is_paired() then
                return 'Not paired with web. Use /att link <code>.'
            end

            local eventId = opts.eventId
            local eventName = opts.eventName

            -- 1. Make sure the event exists, then start it.
            if not eventId then
                -- HNM events take per-window DKP; everything else takes
                -- per-hour. Mirror the resolve done in on_preset_button.
                local dkpRate = (state.selectedMode == 'HNM')
                    and opts.dkpPerWindow
                    or  opts.dkpPerHour
                local created, cerr = api.create_event(eventName, state.selectedMode, nil, dkpRate)
                if not created or not created.eventId then
                    return 'Create failed: ' .. tostring(cerr)
                end
                eventId = created.eventId
                eventName = created.name or eventName
                state.linkedEventId = eventId
                state.linkedEventName = eventName
            end

            local r, serr = api.start_event(eventId)
            if not r then return 'Start failed: ' .. tostring(serr) end
            local startedFresh = not r.alreadyStarted
            eventName = r.name or eventName

            -- 2. Build entries list from current roster (skip pending X-prefixed).
            local entries = {}
            for _, row in ipairs(attendance.data) do
                if not row.name:match('^X ') then
                    entries[#entries + 1] = {
                        characterName = row.name,
                        mainJob = row.jobsMain,
                        subJob = row.jobsSub,
                        zone = row.zone
                    }
                end
            end

            -- 3. Determine windowSequence (nil for non-HNM single-window events).
            -- For HNMs the addon increments per Post; the server pins the batch to
            -- that sequence number so it lands on its own attendance tab.
            local windowMax = state.windowMax or 1
            local nextSequence = nil
            if windowMax > 1 then
                nextSequence = (state.windowSequence or 0) + 1
                if nextSequence > windowMax then
                    return string.format('All %d windows already posted for %s.', windowMax, eventName)
                end
            end

            -- 4. Post attendance (if there's anyone to post).
            local syncSummary = 'No roster entries.'
            if #entries > 0 then
                local result, perr = api.post_attendance(eventId, entries, nextSequence)
                if result then
                    local unmatched = result.unmatched or {}
                    local windowTag = nextSequence and (' [window ' .. nextSequence .. ']') or ''
                    syncSummary = string.format('Synced %d / Reported %d - %d unmatched%s',
                        result.matched or 0, #entries, #unmatched, windowTag)
                    if #unmatched > 0 then
                        local sample = {}
                        for i = 1, math.min(5, #unmatched) do sample[i] = unmatched[i] end
                        syncSummary = syncSummary .. ': ' .. table.concat(sample, ', ')
                        if #unmatched > 5 then syncSummary = syncSummary .. ', ...' end
                    end

                    -- For HNMs: snapshot the just-posted roster into windowRosters,
                    -- bump the sequence, and clear the live roster so the next /sea
                    -- builds the next window from scratch.
                    if nextSequence then
                        local snapshot = {}
                        for i, row in ipairs(attendance.data) do snapshot[i] = row end
                        state.windowRosters[nextSequence] = snapshot
                        state.windowSequence = nextSequence
                        -- Mirror the new state into the per-event map so re-selecting
                        -- this event (after navigating away) still shows the posted
                        -- windows + when each one was posted.
                        state.windowStateByEvent = state.windowStateByEvent or {}
                        local entry = state.windowStateByEvent[state.linkedEventId]
                        if not entry then
                            entry = {
                                max      = state.windowMax,
                                sequence = nextSequence,
                                rosters  = state.windowRosters,
                                postedAt = {},
                            }
                            state.windowStateByEvent[state.linkedEventId] = entry
                        else
                            entry.max      = state.windowMax
                            entry.sequence = nextSequence
                            entry.rosters  = state.windowRosters
                            entry.postedAt = entry.postedAt or {}
                        end
                        entry.postedAt[nextSequence] = constants.format_posted_at(os.date('*t'))
                        attendance.clear()
                        attendance.add_self()
                    end
                else
                    syncSummary = 'Sync failed: ' .. tostring(perr)
                end
            end

            -- 5. Optional CSV.
            if opts.csvOnStart and #entries > 0 then
                attendance.write_file(addon.path, state.selectedMode, eventName)
            end

            -- 6. Refresh the cached events list so the launcher's Queued/Active
            -- panels reflect the new live state without requiring a manual Refresh.
            -- Best-effort; if the call fails the user can still hit Refresh.
            local refreshed = api.list_events()
            if refreshed then state.webEvents = refreshed end

            -- 7. LS chat announcements (broadcast to every selected linkshell).
            -- Stagger the second message: FFXI rejects rapid back-to-back /l
            -- commands ("A command error occurred"), so when both messages
            -- need to fire we send "Event started" now and defer the
            -- attendance line through pendingLSMessage. d3d_present picks it
            -- up after the delay and broadcasts then.
            if startedFresh then
                broadcast_to_selected_ls(string.format(messages.EVENT_STARTED, eventName))
            end
            if #entries > 0 then
                local takenTpl = (state.selectedMode == 'HNM') and messages.HNM_TAKEN or messages.EVENT_TAKEN
                local takenMsg = string.format(takenTpl, eventName)
                if startedFresh then
                    local delay = tonumber(state.attendDelaySec) or 2
                    if delay < 1 then delay = 1 end
                    state.pendingLSMessage = {
                        message = takenMsg,
                        fireAt  = os.clock() + delay,
                    }
                else
                    broadcast_to_selected_ls(takenMsg)
                end
            end

            return (startedFresh and 'Started: ' or 'Already live: ') .. eventName .. '. ' .. syncSummary
        end,

        -- Posts a captured ToD to the web app. The capture record sits in
        -- state.todCaptures[index]; we mark it posting=true while the HTTP
        -- call is in flight so the button can grey itself, then write the
        -- server's response onto the record so the UI can render Repop time.
        -- Synchronous (api.request blocks) but the call is small and fast.
        on_post_tod = function(captureIndex)
            local cap = state.todCaptures and state.todCaptures[captureIndex]
            if not cap then return end
            if cap.posting or cap.posted then return end
            if not api.is_paired() then
                cap.postError = 'Not paired with web. Use /att link <code>.'
                return
            end

            cap.posting   = true
            cap.postError = nil
            local result, err = api.post_tod(cap.monster, cap.callbackSec, cap.message)
            cap.posting = false

            if not result then
                cap.postError = tostring(err or 'unknown error')
                print(chat.header('att') .. 'Post ToD failed: ' .. cap.postError)
                return
            end

            -- Format repop time in the same "April 29th 2026 5:45:58 PM"
            -- style as Captured at: parse the server's ISO-8601 UTC string
            -- to a local-time table, then run through format_posted_at. Fall
            -- back to the raw string if parsing somehow fails.
            local repopLocalTable = constants.parse_iso_utc_to_local_table(result.repopTimeUtc)
            local repopFormatted  = repopLocalTable
                and constants.format_posted_at(repopLocalTable)
                or  tostring(result.repopTimeUtc)

            cap.posted = {
                todId          = result.todId,
                repopTimeUtc   = result.repopTimeUtc,
                repopFormatted = repopFormatted,
                cooldown       = result.cooldown,
                interval       = result.interval,
            }
            print(chat.header('att') .. string.format('Posted ToD: %s (id %s)',
                tostring(cap.monster), tostring(result.todId)))
        end,

        -- Lazy-fetches the linkshell roster + loot structure used to
        -- populate the Winner combo on the Loot Pool panel. force=true
        -- bypasses the TTL check (used by an explicit Refresh button).
        on_load_roster = function(force)
            if state.rosterFetching then return end
            local cache = state.rosterCache
            if not force and cache and cache.fetchedAt
               and (os.time() - cache.fetchedAt) < ROSTER_CACHE_TTL_SEC then
                return
            end
            if not api.is_paired() then
                state.rosterError = 'Not paired with web. Use /att link <code>.'
                return
            end
            state.rosterFetching = true
            state.rosterError    = nil
            local result, err = api.list_roster()
            state.rosterFetching = false
            if not result then
                state.rosterError = tostring(err or 'roster fetch failed')
                return
            end
            state.rosterCache = {
                fetchedAt     = os.time(),
                names         = result.characterNames or {},
                lootStructure = result.lootStructure or 'Dkp',
            }
        end,

        -- Posts a single drop's loot detail to the server. The drop record
        -- on the capture's lootDrops carries draft state (winner, dkpSpent)
        -- which the UI binds to imgui inputs; this callback reads them,
        -- calls api.post_loot, and writes the response back onto the drop
        -- so the UI can render "Posted: <winner> for <dkp> DKP".
        on_post_loot = function(captureIndex, dropIndex)
            local cap = state.todCaptures and state.todCaptures[captureIndex]
            if not cap then return end
            local drop = cap.lootDrops and cap.lootDrops[dropIndex]
            if not drop then return end
            if drop.posting or drop.posted then return end

            if not api.is_paired() then
                drop.postError = 'Not paired with web. Use /att link <code>.'
                return
            end

            -- Auto-post the parent ToD if the user hasn't already done so.
            -- The server requires a parent Tod row for every TodLootDetail,
            -- so we transparently create one using the capture's monster +
            -- detection time. The result is stashed back on cap.posted so
            -- the ToD Capturing panel updates (Posted to web! + repop time)
            -- the same way as if the user had clicked Post ToD manually.
            local todId = cap.posted and cap.posted.todId
            if not todId then
                drop.posting   = true
                drop.postError = nil
                local todResult, todErr = api.post_tod(cap.monster, cap.callbackSec, cap.message)
                drop.posting = false
                if not todResult or not todResult.todId then
                    drop.postError = 'ToD auto-post failed: ' .. tostring(todErr or 'unknown error')
                    return
                end
                local repopLocalTable = constants.parse_iso_utc_to_local_table(todResult.repopTimeUtc)
                cap.posted = {
                    todId          = todResult.todId,
                    repopTimeUtc   = todResult.repopTimeUtc,
                    repopFormatted = repopLocalTable
                        and constants.format_posted_at(repopLocalTable)
                        or  tostring(todResult.repopTimeUtc),
                    cooldown       = todResult.cooldown,
                    interval       = todResult.interval,
                }
                todId = todResult.todId
                print(chat.header('att') .. string.format(
                    'Auto-posted ToD for loot: %s (id %s)',
                    tostring(cap.monster), tostring(todId)))
            end

            local winner = (drop.draft.winner or ''):gsub('^%s+', ''):gsub('%s+$', '')
            local dkpStr = tostring(drop.draft.dkpSpent or '')
            local dkp    = tonumber(dkpStr)
            if winner == '' then
                drop.postError = 'Pick a winner.'
                return
            end
            if not dkp or dkp <= 0 then
                drop.postError = 'Enter a positive DKP value.'
                return
            end

            drop.posting   = true
            drop.postError = nil
            local result, err = api.post_loot(todId, drop.itemName, winner, dkp)
            drop.posting = false

            if not result then
                drop.postError = tostring(err or 'unknown error')
                print(chat.header('att') .. 'Post Loot failed: ' .. drop.postError)
                return
            end

            drop.posted = {
                lootDetailId      = result.lootDetailId,
                itemWinner        = result.itemWinner,
                winningDkpSpent   = result.winningDkpSpent,
                actualDeductedDkp = result.actualDeductedDkp,
            }
            drop.draftOpen = false
            print(chat.header('att') .. string.format(
                'Posted loot: %s -> %s for %s DKP',
                tostring(drop.itemName),
                tostring(result.itemWinner),
                tostring(result.winningDkpSpent)))
        end,

        -- Ends a running event. Mirrors the web app's End flow: server
        -- writes EventHistory + DkpLedgerEntry rows and removes the live
        -- Event / Jobs / participants / loot. After a successful end we
        -- clear the local linked-event/window state and refresh the
        -- cached events list so the launcher's Active Events drops it.
        on_end_event = function()
            if not state.linkedEventId then return end
            if not api.is_paired() then
                state.lastSyncSummary = 'Not paired with web. Use /att link <code>.'
                return
            end
            local eventId = state.linkedEventId
            local eventName = state.linkedEventName or '?'
            local result, err = api.end_event(eventId)
            if not result then
                state.lastSyncSummary = 'End failed: ' .. tostring(err)
                print(chat.header('att') .. state.lastSyncSummary)
                return
            end
            state.lastSyncSummary = 'Ended: ' .. eventName
            print(chat.header('att') .. state.lastSyncSummary)

            if state.windowStateByEvent then
                state.windowStateByEvent[eventId] = nil
            end
            state.linkedEventId    = nil
            state.linkedEventName  = nil
            state.pendingEventName = nil
            state.lastScannedFor   = nil
            state.windowMax        = 1
            state.windowSequence   = 0
            state.windowRosters    = {}
            state.activeWindowTab  = nil
            attendance.clear()

            local refreshed = api.list_events()
            if refreshed then state.webEvents = refreshed end
        end
    }

    -- Standalone Attendance Results window has been merged into the launcher.
    -- Force-suppress it so the /att <name> chat path no longer pops a second window.
    state.isAttendanceWindowOpen = false

    if state.isAttendLauncherOpen then
        state.isAttendLauncherOpen = ui.draw_launcher(state.isAttendLauncherOpen, state, callbacks)
    end

    if state.isSettingsWindowOpen then
        -- Hand the settings popup the persisted eventDefaults table plus
        -- a save callback so it can persist edits to disk on Save click.
        -- The custom-monster list is also exposed (with auto-saving add /
        -- remove helpers) so the user can extend the ToD listener without
        -- a code change.
        state.isSettingsWindowOpen = ui.draw_settings(state.isSettingsWindowOpen, state, {
            event_defaults     = config.eventDefaults,
            built_in_hnms      = constants.HNM_WINDOW_COUNTS,
            built_in_sky       = constants.SKY_FARM_NMS,
            built_in_testing   = constants.TESTING_MONSTERS,
            custom_monsters    = config.customMonsters,
            on_add_custom_monster = function(name)
                if type(name) ~= 'string' then return end
                local trimmed = name:gsub('^%s+', ''):gsub('%s+$', '')
                if trimmed == '' then return end
                -- De-dup against itself + the built-in tables (case-insensitive).
                local lower = trimmed:lower()
                if constants.HNM_WINDOW_COUNTS[trimmed] or constants.TESTING_MONSTERS[trimmed] then
                    return
                end
                for _, existing in ipairs(config.customMonsters) do
                    if existing:lower() == lower then return end
                end
                table.insert(config.customMonsters, trimmed)
                settings.save()
            end,
            on_remove_custom_monster = function(index)
                if type(index) ~= 'number' then return end
                if index < 1 or index > #config.customMonsters then return end
                table.remove(config.customMonsters, index)
                settings.save()
            end,
            on_settings_save  = function()
                settings.save()
                print(chat.header('att') .. 'Settings saved.')
            end,
        })
    end

    if comp.isOpen then
        comp.isOpen = ui.draw_composition_window(comp.isOpen, comp, attendance)
    end
    
    -- Debug window call removed

end)

ashita.events.register('command', 'att_global_tools', function(e)
    local args = e.command:args()
    if #args > 0 and args[1]:lower() == '/findoffset' then
        e.blocked = true
        memory.deep_scan_for_entity_list()
        return
    end

    if #args > 0 and args[1]:lower() == '/apidump' then
        e.blocked = true
        memory.dump_api_methods()
        return
    end
end)
