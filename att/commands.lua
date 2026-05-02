-- commands.lua
-- All chat-command handlers for the addon: /att, /attend, /comp, /findoffset,
-- /apidump. Extracted from att.lua. The att.lua file calls M.register(state, deps)
-- once during init; this module then registers each ashita event handler with
-- closures that capture state + deps.
--
-- The handler bodies are byte-for-byte copies of the originals, with the only
-- substitutions being:
--   * top-level upvalues replaced with deps.<name> (api, attendance, memory,
--     comp, resources, settings, chat) or local aliases at the top of register().
--   * helper calls now go through utils.M (utils.ls_prefix(state), etc.)
--   * config is reached via deps.config

local M = {}

function M.register(state, deps)
    local api        = deps.api
    local attendance = deps.attendance
    local memory     = deps.memory
    local comp       = deps.comp
    local resources  = deps.resources
    local settings   = deps.settings
    local chat       = deps.chat
    local utils      = deps.utils
    local config     = deps.config

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

        -- Web sync: /att status (or /att list -- same thing)
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

        -- /att tod debug [on|off|toggle]   -- diagnose missed defeat captures by
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
            local lsSearch = utils.ls_search_param(state)

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
                    AshitaCore:GetChatManager():QueueCommand(1, utils.ls_prefix(state) .. msg)
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
            utils.update_suggestions(state, deps)
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
            local lsSearch = utils.ls_search_param(state)
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

    -- /findoffset, /apidump (global memory tools, kept on a separate event
    -- registration in the original; preserved as-is here).
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
end

return M
