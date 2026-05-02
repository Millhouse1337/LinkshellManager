-- ui/launcher_create_event.lua
-- "Create New Event" toggle + form section of draw_launcher.
-- Extracted from ui.lua (body byte-for-byte). Mutates `state` and fires
-- `callbacks` exactly as the original.
local imgui = require('imgui')
local api   = require('api')

local M = {}

-- Renders the centered "Create New Event" toggle and (when enabled) the
-- Style/Type/Name form + Create button. `ctx` carries the persistent input
-- pointers that previously lived as file-level locals in ui.lua so they
-- survive across frames identically to the pre-split behavior.
function M.draw(state, callbacks, ctx)
    local syncNewEventNamePtr = ctx.syncNewEventNamePtr
    local syncStyleChosen     = ctx.syncStyleChosen
    local syncNewEventType    = ctx.syncNewEventType
    local EVENT_TYPE_OPTIONS  = ctx.EVENT_TYPE_OPTIONS

    -- "Create New Event" toggle on its own row at the top of the
    -- left column. Label + checkbox are centered horizontally so
    -- the control reads as a single unit rather than scattered.
    do
        imgui.Dummy({ 0, 6 })
        local labelW = 120  -- approx pixel width of "Create New Event" + spacing + checkbox
        local windowWidth = 600
        pcall(function()
            local ww = imgui.GetWindowWidth()
            if type(ww) == 'number' then windowWidth = ww end
        end)
        local startX = math.max(0, (windowWidth - labelW) / 2)
        imgui.SetCursorPosX(startX)
        imgui.Text('Create New Event')
        imgui.SameLine()
        local acPtr = { state.autoCreateOnWrite }
        if imgui.Checkbox('##autoCreate', acPtr) then
            state.autoCreateOnWrite = acPtr[1]
        end
    end

    -- New-event controls (only visible when Create New Event is
    -- checked). Sits right under the toggle so it's clearly part of
    -- the same control. Event Style toggles + a context-aware DKP
    -- input (DKP/Window for HNM Style, DKP/Hour for Regular) +
    -- Event Name input.
    if state.autoCreateOnWrite then
        local windowWidth = 600
        pcall(function()
            local ww = imgui.GetWindowWidth()
            if type(ww) == 'number' then windowWidth = ww end
        end)
        local function centerCursor(blockWidth)
            imgui.SetCursorPosX(math.max(0, (windowWidth - blockWidth) / 2))
        end

        imgui.Dummy({ 0, 6 })
        centerCursor(72)
        imgui.Text('Event Style')

        -- All three checkboxes start unchecked (syncStyleChosen=false).
        -- Picking one flips the flag and sets state.selectedMode;
        -- the others auto-uncheck via their own bindings next frame.
        -- 'Claim/Kill' is just an HNM with windowCount=2 — server
        -- behavior is identical to ShortWindowHnms (On Time / Claim/Kill).
        centerCursor(280)
        local regPtr = { syncStyleChosen[1] and state.selectedMode == 'Event' }
        if imgui.Checkbox('Timed', regPtr) then
            if regPtr[1] then
                state.selectedMode = 'Event'
                syncStyleChosen[1] = true
            else
                syncStyleChosen[1] = false
            end
        end
        imgui.SameLine()
        local hnmPtr = { syncStyleChosen[1] and state.selectedMode == 'HNM' }
        if imgui.Checkbox('HNM Style', hnmPtr) then
            if hnmPtr[1] then
                state.selectedMode = 'HNM'
                syncStyleChosen[1] = true
            else
                syncStyleChosen[1] = false
            end
        end
        imgui.SameLine()
        local claimKillPtr = { syncStyleChosen[1] and state.selectedMode == 'ClaimKill' }
        if imgui.Checkbox('Claim/Kill', claimKillPtr) then
            if claimKillPtr[1] then
                state.selectedMode = 'ClaimKill'
                syncStyleChosen[1] = true
            else
                syncStyleChosen[1] = false
            end
        end

        imgui.Dummy({ 0, 6 })
        centerCursor(72)
        imgui.Text('Event Type')
        centerCursor(260)
        imgui.PushItemWidth(260)
        local typePreview = (syncNewEventType[1] ~= '' and syncNewEventType[1])
            or 'Select event type'
        if imgui.BeginCombo('##syncNewType', typePreview) then
            for _, opt in ipairs(EVENT_TYPE_OPTIONS) do
                local selected = (syncNewEventType[1] == opt)
                if imgui.Selectable(opt, selected) then
                    syncNewEventType[1] = opt
                end
                if selected then imgui.SetItemDefaultFocus() end
            end
            imgui.EndCombo()
        end
        imgui.PopItemWidth()

        imgui.Dummy({ 0, 6 })
        centerCursor(72)
        imgui.Text('Event Name')
        centerCursor(260)
        imgui.PushItemWidth(260)
        imgui.InputText('##syncNewName', syncNewEventNamePtr, 64)
        imgui.PopItemWidth()

        -- Create Event button — only enabled once a name has been
        -- typed. Uses the persisted DKP rate matching the chosen
        -- style (Settings popup -> DKP / Hour for Regular, DKP /
        -- Window for HNM Style).
        imgui.Dummy({ 0, 6 })
        local trimmedName = (syncNewEventNamePtr[1] or '')
            :gsub('^%s+', ''):gsub('%s+$', '')
        local typeChosen = syncNewEventType[1] ~= ''
        if trimmedName ~= '' and syncStyleChosen[1] and typeChosen then
            centerCursor(260)
            if imgui.Button('Create Event: ' .. trimmedName .. '##syncCreateInline', { 260, 0 }) then
                if api.is_paired() then
                    local d = callbacks.event_defaults or {}
                    local mode = state.selectedMode
                    local isMultiPost = (mode == 'HNM') or (mode == 'ClaimKill')
                    local dkp = isMultiPost
                        and tonumber(d.dkpPerWindowHnm)
                        or  tonumber(d.dkpPerHourRegular)
                    -- Style determines window count and DKP rate semantics;
                    -- the user-chosen Event Type from the dropdown is what
                    -- gets sent to the server's EventType field verbatim.
                    -- HNM Style => 24 windows (long pop), Claim/Kill => 2.
                    local windowCount = nil
                    if mode == 'HNM' then windowCount = 24
                    elseif mode == 'ClaimKill' then windowCount = 2 end
                    local created, err = api.create_event(trimmedName, syncNewEventType[1], nil, dkp, windowCount)
                    if created and created.eventId then
                        state.lastSyncSummary = 'Created event: ' .. (created.name or trimmedName)
                            .. ' (id ' .. tostring(created.eventId) .. ')'
                        local events = api.list_events()
                        if events then state.webEvents = events end
                        syncNewEventNamePtr[1] = ''
                        syncNewEventType[1] = ''
                    else
                        state.lastSyncSummary = 'Create failed: ' .. tostring(err)
                    end
                else
                    state.lastSyncSummary = 'Not paired with web. Use /att link <code>.'
                end
            end
        end
    end
end

return M
