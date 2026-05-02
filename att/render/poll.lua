-- render/poll.lua
-- Per-frame polling and pending-timer dispatch. Lifted byte-for-byte from
-- the body of att.lua's d3d_present hook (everything BEFORE the callbacks
-- table). Runs once per frame from render_pump.lua.

local M = {}

function M.tick(state, deps)
    local imgui      = deps.imgui
    local api        = deps.api
    local attendance = deps.attendance
    local memory     = deps.memory
    local comp       = deps.comp
    local utils      = deps.utils

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

    -- Break-room auto-refresh. Polls the participants endpoint every 10s while
    -- a live event is selected and the launcher is open. Cheap server call (one
    -- query + one ledger lookup) so the cadence is fine; users see status
    -- changes without manually hitting Refresh. Auto-expands the panel when
    -- anyone first hits "on break" or has a pending self-return.
    do
        local BREAK_ROOM_REFRESH_SEC = 10
        local linkedId = state.linkedEventId
        local launcherOpen = state.isAttendLauncherOpen
        if launcherOpen and linkedId and api.is_paired() then
            local linkedLive = false
            for _, ev in ipairs(state.webEvents or {}) do
                if ev.id == linkedId and ev.isLive then linkedLive = true; break end
            end
            if linkedLive then
                local now = os.time()
                if (state.breakRoom.lastFetchAt or 0) + BREAK_ROOM_REFRESH_SEC <= now then
                    state.breakRoom.lastFetchAt = now
                    local result, err = api.list_participants(linkedId)
                    if result then
                        state.breakRoom.participants = result.participants or {}
                        state.breakRoom.canModerate  = result.canModerateLiveEvent and true or false
                        state.breakRoom.loaded       = true
                        -- Auto-expand once when something needs attention.
                        -- imgui's CollapsingHeader owns its open state, so we
                        -- pass a one-shot via autoExpandRequested that ui.lua
                        -- consumes with SetNextItemOpen on the next frame. The
                        -- autoExpanded latch then prevents re-arming on every
                        -- 10s poll, so a manual collapse stays sticky.
                        if not state.breakRoom.autoExpanded then
                            for _, p in ipairs(state.breakRoom.participants) do
                                if p.isOnBreak or p.pendingReturnLedgerId then
                                    state.breakRoom.autoExpandRequested = true
                                    state.breakRoom.autoExpanded        = true
                                    break
                                end
                            end
                        end
                    elseif err then
                        -- Don't toast every poll on error; just leave the cache.
                    end
                end
            else
                -- Selected event isn't live (or not selected); reset the cache so
                -- the next live selection starts clean and re-arms autoExpanded.
                if state.breakRoom.loaded then
                    state.breakRoom.participants = {}
                    state.breakRoom.canModerate  = false
                    state.breakRoom.loaded       = false
                    state.breakRoom.autoExpanded = false
                    state.breakRoom.lastFetchAt  = 0
                end
            end
        end
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
        utils.broadcast_to_selected_ls(state, state.pendingLSMessage.message)
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
                AshitaCore:GetChatManager():QueueCommand(1, utils.ls_prefix(state) .. msg)
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
        utils.update_suggestions(state, deps)
    end

    if state.attForceRefreshAt and os.clock() >= state.attForceRefreshAt then
        utils.update_suggestions(state, deps)
        state.attForceRefreshAt = nil
    end
end

return M
