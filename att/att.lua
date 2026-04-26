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
        baseUrl       = '',
        token         = '',
        linkshellId   = 0,
        linkshellName = '',
        label         = ''
    }
})

-- Hand the API client a reference to the persisted config block so
-- pair() / unpair() update the same table that gets saved to disk.
api.set_config(config.api)

-- Global State
local state = {
    debugMode    = false,
    selectedMode = 'HNM',
    g_LSMode     = nil, -- 'ls' or 'ls2'
    
    isAttendanceWindowOpen = false,
    isAttendLauncherOpen   = false,
    isHelpWindowOpen       = false,
    isDebugWindowOpen      = false,

    pendingEventName     = nil,
    pendingFilePath      = nil,
    pendingLSMessage     = nil,
    pendingAttend        = nil, -- { eventName, useLS2, fireAt }
    pendingSeaScan       = nil,
    pendingGather        = nil, -- { eventName, fireAt }
    launcherGather       = nil, -- { eventName, fireAt, isAutoCreate } - for /attend launcher's auto-scan
    pendingComp          = nil, -- { eventName, fireAt }

    attendUseLS2         = false,
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
    webEvents            = {},    -- cache of fetched events for the launcher dropdown
    webEventsLoadedAt    = 0,
    lastSyncSummary      = nil,   -- string shown in launcher after a sync attempt
    launcherCsvOnStart   = false, -- if true, Start & Post also writes the local CSV
    lastScannedFor       = nil    -- name of event the launcher last scanned for (avoids re-scanning the same selection)
}

--------------------------------------------------------------------------------
-- INITIALIZATION
--------------------------------------------------------------------------------
ashita.events.register('load', 'att_load_cb', function()
    resources.load(addon.path)
end)

--------------------------------------------------------------------------------
-- UTILS
--------------------------------------------------------------------------------
local function ls_prefix()
    return (state.g_LSMode == 'ls2') and '/l2 ' or '/l '
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

    local lsSearch = state.attendUseLS2 and 'linkshell2' or 'linkshell'
    AshitaCore:GetChatManager():QueueCommand(1, string.format('/sea %s %s', area, lsSearch))

    local delay = tonumber(state.attendDelaySec) or 2
    if delay < 0 then delay = 0 end

    state.pendingAttend = {
        eventName  = eventName,
        useLS2     = state.attendUseLS2,
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
        api.set_base_url(url)
        settings.save()
        print(chat.header('att') .. 'Web server set to: ' .. (config.api.baseUrl ~= '' and config.api.baseUrl or '<empty>'))
        return
    end

    -- Web sync: /att link <code>
    if #args >= 3 and args[2]:lower() == 'link' then
        local code = args[3]
        local result, err = api.pair(code)
        if result then
            settings.save()
            print(chat.header('att') .. 'Linked to ' .. (result.linkshellName or '<linkshell>')
                .. (result.label and result.label ~= '' and (' (' .. result.label .. ')') or ''))
            state.linkedEventId = nil
        else
            print(chat.header('att') .. 'Pair failed: ' .. tostring(err))
        end
        return
    end

    -- Web sync: /att unlink
    if #args == 2 and args[2]:lower() == 'unlink' then
        api.unpair()
        settings.save()
        state.linkedEventId = nil
        print(chat.header('att') .. 'Unlinked from web server. Local CSV writes still work.')
        return
    end

    -- Web sync: /att status
    if #args == 2 and args[2]:lower() == 'status' then
        if not api.is_paired() then
            print(chat.header('att') .. 'Not linked. Use /att link <code> after generating one on the website.')
        else
            print(chat.header('att') .. 'Linked to ' .. (config.api.linkshellName or '?')
                .. ' (id ' .. tostring(config.api.linkshellId) .. ')'
                .. (config.api.label ~= '' and (' [' .. config.api.label .. ']') or ''))
            print(chat.header('att') .. 'Server: ' .. (config.api.baseUrl ~= '' and config.api.baseUrl or '<not set>'))
        end
        return
    end

    if #args == 2 and args[2]:lower() == 'help' then
        state.isHelpWindowOpen = true
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
        local lsSearch = state.attendUseLS2 and 'linkshell2' or 'linkshell'
        
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
        update_suggestions()
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
        local lsSearch = state.attendUseLS2 and 'linkshell2' or 'linkshell'
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
                        local summary = string.format('Synced %d / reported %d - %d unmatched',
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
        -- Launcher: kick off /sea + memory scan; results land in attendance.data
        -- and render in the launcher's own roster panel (no standalone window).
        on_launcher_scan = function(eventName, isAutoCreate)
            local lsSearch = state.attendUseLS2 and 'linkshell2' or 'linkshell'
            local area
            if isAutoCreate then
                area = 'here'
            else
                area = resources.attSearchArea[eventName]
                    or (resources.attCreditNames[eventName] and resources.attCreditNames[eventName][1])
                    or 'here'
            end
            AshitaCore:GetChatManager():QueueCommand(1, string.format('/sea %s %s', area, lsSearch))
            state.launcherGather = {
                eventName = eventName,
                fireAt = os.clock() + (state.attendDelaySec or 3),
                isAutoCreate = isAutoCreate
            }
        end,

        -- Launcher: combined Start/Create+Start + post attendance + LS msgs + optional CSV.
        -- opts: { eventId, eventName, isAutoCreate, csvOnStart }
        -- Returns a one-line summary string for state.lastSyncSummary.
        on_start_and_post = function(opts)
            if not api.is_paired() then
                return 'Not paired with web. Use /att link <code>.'
            end

            local lsPrefix = state.attendUseLS2 and '/l2 ' or '/l '
            local eventId = opts.eventId
            local eventName = opts.eventName

            -- 1. Start (or create+start) on the web app.
            local startedFresh = false
            if eventId then
                local r, err = api.start_event(eventId)
                if not r then return 'Start failed: ' .. tostring(err) end
                startedFresh = not r.alreadyStarted
                eventName = r.name or eventName
            else
                local created, err = api.create_event(eventName, state.selectedMode, nil)
                if not created or not created.eventId then
                    return 'Create failed: ' .. tostring(err)
                end
                eventId = created.eventId
                eventName = created.name or eventName
                startedFresh = true
                state.linkedEventId = eventId
                state.linkedEventName = eventName
            end

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

            -- 3. Post attendance (if there's anyone to post).
            local syncSummary = 'No roster entries.'
            if #entries > 0 then
                local result, perr = api.post_attendance(eventId, entries)
                if result then
                    local unmatched = result.unmatched or {}
                    syncSummary = string.format('Synced %d / reported %d - %d unmatched',
                        result.matched or 0, #entries, #unmatched)
                    if #unmatched > 0 then
                        local sample = {}
                        for i = 1, math.min(5, #unmatched) do sample[i] = unmatched[i] end
                        syncSummary = syncSummary .. ': ' .. table.concat(sample, ', ')
                        if #unmatched > 5 then syncSummary = syncSummary .. ', ...' end
                    end
                else
                    syncSummary = 'Sync failed: ' .. tostring(perr)
                end
            end

            -- 4. Optional CSV.
            if opts.csvOnStart and #entries > 0 then
                attendance.write_file(addon.path, state.selectedMode, eventName)
            end

            -- 5. LS chat announcements.
            if startedFresh then
                AshitaCore:GetChatManager():QueueCommand(1,
                    lsPrefix .. string.format(messages.EVENT_STARTED, eventName))
            end
            if #entries > 0 then
                local takenTpl = (state.selectedMode == 'HNM') and messages.HNM_TAKEN or messages.EVENT_TAKEN
                AshitaCore:GetChatManager():QueueCommand(1,
                    lsPrefix .. string.format(takenTpl, eventName))
            end

            return (startedFresh and 'Started: ' or 'Already live: ') .. eventName .. '. ' .. syncSummary
        end
    }

    if state.isAttendanceWindowOpen then
        state.isAttendanceWindowOpen = ui.draw_attendance_window(state.isAttendanceWindowOpen, attendance, state, callbacks)
    end
    
    if state.isAttendLauncherOpen then
        state.isAttendLauncherOpen = ui.draw_launcher(state.isAttendLauncherOpen, state, callbacks)
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
