-- utils.lua
-- Small helpers extracted from att.lua. Each function takes the addon `state`
-- table as its first argument so no module-level state lives here. Functions
-- that need external modules (attendance, memory, resources) take a `deps`
-- table -- att.lua builds one at init and threads it through.

local M = {}

-- Returns the in-game chat prefix matching the launcher's primary LS choice.
function M.ls_prefix(state)
    return (state.g_LSMode == 'ls2') and '/l2 ' or '/l '
end

-- Returns 'linkshell' or 'linkshell2' for /sea targeting based on the launcher's
-- multi-select. Prefers LS1 if both are checked. Falls back to LS1 if neither.
function M.ls_search_param(state)
    if state.lsChannels.ls1 then return 'linkshell' end
    if state.lsChannels.ls2 then return 'linkshell2' end
    return 'linkshell'
end

-- Sends a chat message to every linkshell channel selected in the launcher.
function M.broadcast_to_selected_ls(state, message)
    if state.lsChannels.ls1 then
        AshitaCore:GetChatManager():QueueCommand(1, '/l ' .. message)
    end
    if state.lsChannels.ls2 then
        AshitaCore:GetChatManager():QueueCommand(1, '/l2 ' .. message)
    end
end

-- (Legacy / unused dispatcher kept for parity with the original att.lua.)
function M.prep_write_targets(deps, mode, eventName)
    return deps.attendance.write_file(addon.path, mode, eventName) -- Dry run or path prep?
    -- Actually attendance.write_file writes it. We might want just the path/msg first?
    -- Refactored attendance.write_file handles opening and writing.
    -- We'll just call it when needed.
end

-- Refreshes state.suggestions for the current zone. deps.{memory, attendance,
-- resources} supply the heavy lifting; only state is mutated.
function M.update_suggestions(state, deps)
    local memory     = deps.memory
    local attendance = deps.attendance
    local resources  = deps.resources

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

-- Schedules a /sea then a delayed /lsm <name> via state.pendingAttend.
-- Reads only resources + state; uses ls_search_param above.
function M.queue_attend_launch(state, deps, eventName)
    local resources = deps.resources
    local constants = deps.constants
    -- Strip the launcher's "D<n>" day-suffix before resource lookups so a
    -- day-tagged HNM event still finds its canonical credit / search area.
    local lookupName = (constants and constants.canonical_event_name)
        and constants.canonical_event_name(eventName) or eventName
    local area = resources.attCreditNames[lookupName] and resources.attCreditNames[lookupName][1]
    if resources.attSearchArea[lookupName] then area = resources.attSearchArea[lookupName] end

    if not area or area == '' then
        print(string.format('[att] No search area found for "%s".', eventName))
        return
    end

    local lsSearch = M.ls_search_param(state)
    AshitaCore:GetChatManager():QueueCommand(1, string.format('/sea %s %s', area, lsSearch))

    local delay = tonumber(state.attendDelaySec) or 2
    if delay < 0 then delay = 0 end

    state.pendingAttend = {
        eventName  = eventName,
        useLS2     = (lsSearch == 'linkshell2'),
        fireAt     = os.clock() + delay,
    }
end

return M
