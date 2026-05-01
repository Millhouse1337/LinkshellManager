-- ui.lua
local ui = {}
local imgui = require('imgui')
local ffi = require('ffi')
local helpers = require('helpers')
local resources = require('resources')
local api = require('api')
local attendance = require('attendance')
local constants = require('constants')

-- Persistent filter state
local filterPtr = { '' }
local syncNewEventNamePtr = { '' }
-- Per-session flag: false until the user explicitly picks a style
-- (Regular or HNM Style). Both checkboxes show unchecked while it's
-- false. Gating the Create Event button on this prevents an event from
-- being created with an implicit style the user never selected.
local syncStyleChosen     = { false }

-- Color used to highlight the local player's row in any roster table.
local SELF_COLOR = { 1.0, 0.85, 0.3, 1.0 } -- warm gold

local function get_self_name()
    local pm = AshitaCore and AshitaCore:GetMemoryManager() and AshitaCore:GetMemoryManager():GetParty()
    return pm and pm:GetMemberName(0) or nil
end

local function is_self_row(row, selfKey)
    if not selfKey then return false end
    return (row.name or ''):gsub('^X%s+', ''):lower() == selfKey
end

function ui.draw_attendance_window(is_open, att_module, state, callbacks)
    if not is_open then return false end
    
    imgui.SetNextWindowSize({ 1050, 600 }, ImGuiCond_FirstUseEver)

    local openPtr = { is_open }
    if imgui.Begin('Attendance Results', openPtr) then

        imgui.Text('Select Mode:')
        imgui.SameLine()
        if imgui.RadioButton('HNM', state.selectedMode == 'HNM') then
            state.selectedMode = 'HNM'
        end
        imgui.SameLine()
        if imgui.RadioButton('Event', state.selectedMode == 'Event') then
            state.selectedMode = 'Event'
        end

        imgui.Separator()
        imgui.Text('Attendance for: ' .. (state.pendingEventName or ''))
        
        local znames = resources.attCreditNames[state.pendingEventName]
        imgui.Text('Zone: ' .. (((znames and #znames > 0) and table.concat(znames, ', ')) or 'UnknownZone'))
        imgui.Separator()

        imgui.Text('Attendees: ' .. #att_module.data)

        if imgui.Button('Party Only') then
            if callbacks.on_party_only then callbacks.on_party_only() end
        end
        imgui.SameLine()

        if imgui.Button('Rescan') then
            att_module.gather_zone(state.pendingEventName)
        end
        imgui.SameLine()
        
        -- Scan Letter logic
        if not state.scanNextLetter then
             -- Simple heuristic if none set
             local last = att_module.data[#att_module.data]
             local ch = (last and last.name) and last.name:match('^X?%s*(%a)') or 'A'
             state.scanNextLetter = ch:upper()
        end

        if imgui.Button('Scan ' .. state.scanNextLetter) then
             if callbacks.on_scan_letter then callbacks.on_scan_letter(state.scanNextLetter) end
             state.scanNextLetter = helpers.get_next_letter(state.scanNextLetter)
        end

        imgui.Separator()

        imgui.BeginChild('att_list', { 0, -50 }, true)
        local selfKey = (get_self_name() or ''):lower()
        if selfKey == '' then selfKey = nil end
        local i = 1
        while i <= #att_module.data do
            local r = att_module.data[i]
            if imgui.Button('Remove##' .. i) then
                table.remove(att_module.data, i)
            else
                imgui.SameLine()
                local line = string.format('%s (%s | %s/%s)', r.name, r.zone, r.jobsMain, r.jobsSub)
                if is_self_row(r, selfKey) then
                    imgui.TextColored(SELF_COLOR, line)
                else
                    imgui.Text(line)
                end
                i = i + 1
            end
        end
        imgui.EndChild()
        imgui.Separator()

        if imgui.Button('Write') then
             if callbacks.on_write then callbacks.on_write(false) end
        end
        imgui.SameLine()
        if imgui.Button('Write & Close') then
             if callbacks.on_write then callbacks.on_write(true) end
             openPtr[1] = false
        end
        imgui.SameLine()
        if imgui.Button('Cancel') then
            openPtr[1] = false
        end

        imgui.End()
    end
    
    return openPtr[1]
end

function ui.draw_launcher(is_open, state, callbacks)
    if not is_open then return false end

    -- ImGuiCond_FirstUseEver is overridden by imgui.ini's saved per-window
    -- state, so a prior manual resize would persist across opens. The
    -- launcherSizePending flag is set by the /attend command handler each
    -- time the launcher transitions to open; here we consume it once with
    -- ImGuiCond_Always to snap the window back to our preferred size. The
    -- user can still drag-resize during the session — the flag is only
    -- set on open, not every frame.
    if state.launcherSizePending then
        imgui.SetNextWindowSize({ 1240, 640 }, ImGuiCond_Always)
        state.launcherSizePending = false
    else
        imgui.SetNextWindowSize({ 1240, 640 }, ImGuiCond_FirstUseEver)
    end
    local openPtr = { is_open }
    if imgui.Begin('Att', openPtr) then
        
        -- Single Refresh action: refreshes queued events, rescans the zone,
        -- and re-fetches the linkshell roster used by the Loot Pool panel's
        -- Winner combo. The top-of-launcher Refresh button is the one place
        -- to pull fresh server state; per-section refresh buttons are gone.
        local function do_full_refresh()
            -- 1. Pull latest events list from the web app.
            local events, err = api.list_events()
            if events then
                state.webEvents = events
                state.webEventsLoadedAt = os.time()
            else
                state.lastSyncSummary = 'Refresh failed: ' .. tostring(err)
            end
            -- 2. Trigger a zone scan. If an event is selected, use its credit zone;
            --    otherwise just scan the player's current zone.
            if callbacks.on_launcher_scan then
                if state.linkedEventId then
                    callbacks.on_launcher_scan(state.linkedEventName or '?', false)
                else
                    local nm = (syncNewEventNamePtr[1] or ''):gsub('^%s+', ''):gsub('%s+$', '')
                    if nm == '' then nm = 'Current Zone' end
                    callbacks.on_launcher_scan(nm, true)
                end
            end
            -- 3. Force-refresh the linkshell roster cache.
            if callbacks.on_load_roster then
                callbacks.on_load_roster(true)
            end
        end

        -- Web Sync indicator on the far left of the header row (when paired).
        if api.is_paired() then
            imgui.TextColored({ 0.5, 0.85, 1.0, 1.0 }, '[Web Sync Activated]')
            imgui.SameLine()
        end

        -- Linkshells dropdown: lists each paired LS by name with its channel.
        -- Slots without a pairing show "(not paired)" and are non-interactive.
        do
            local p1 = api.get_pairing_by_channel(1)
            local p2 = api.get_pairing_by_channel(2)

            -- Auto-clear a channel flag if its pairing was removed, so we don't
            -- keep trying to /l1 or /l2 on a slot the user has unlinked.
            if not p1 and state.lsChannels.ls1 then state.lsChannels.ls1 = false end
            if not p2 and state.lsChannels.ls2 then state.lsChannels.ls2 = false end

            local label1 = p1 and p1.linkshellName or 'LS1 (not paired)'
            local label2 = p2 and p2.linkshellName or 'LS2 (not paired)'
            local sel = {}
            if state.lsChannels.ls1 then table.insert(sel, p1 and p1.linkshellName or 'LS1') end
            if state.lsChannels.ls2 then table.insert(sel, p2 and p2.linkshellName or 'LS2') end
            local preview = (#sel > 0) and table.concat(sel, ', ') or 'None'

            -- Center the dropdown between the [Web Sync Activated] tag on the
            -- left and the Refresh button on the right by setting cursor X to
            -- (windowWidth - comboWidth) / 2, all on the same header line.
            local COMBO_W = 180
            local windowWidth = 600
            pcall(function()
                local ww = imgui.GetWindowWidth()
                if type(ww) == 'number' then windowWidth = ww end
            end)
            imgui.SameLine(math.max(8, (windowWidth - COMBO_W) / 2))

            imgui.PushItemWidth(COMBO_W)
            if imgui.BeginCombo('##lsCombo', preview) then
                if p1 then
                    local pp = { state.lsChannels.ls1 }
                    if imgui.Checkbox(label1, pp) then state.lsChannels.ls1 = pp[1] end
                else
                    imgui.TextDisabled(label1)
                end
                if p2 then
                    local pp = { state.lsChannels.ls2 }
                    if imgui.Checkbox(label2, pp) then state.lsChannels.ls2 = pp[1] end
                else
                    imgui.TextDisabled(label2)
                end
                imgui.EndCombo()
            end
            imgui.PopItemWidth()
        end

        -- Refresh button at the top right of the launcher header.
        do
            local REFRESH_BTN_W = 90
            local windowWidth = 600
            pcall(function()
                local ww = imgui.GetWindowWidth()
                if type(ww) == 'number' then windowWidth = ww end
            end)
            imgui.SameLine(windowWidth - REFRESH_BTN_W - 16)
            if imgui.Button('Refresh##topRefresh', { REFRESH_BTN_W, 0 }) then
                do_full_refresh()
            end
        end

        -- Settings button on the left + centered Timezone label on the
        -- same row. The button toggles the settings popup; the label is
        -- centered horizontally based on the launcher's current width.
        do
            if imgui.Button('Settings##openSettings', { 80, 0 }) then
                state.isSettingsWindowOpen = not state.isSettingsWindowOpen
            end

            local tzLabel = os.date('%Z') or ''
            if tzLabel == '' or tzLabel:match('^%s*$') then
                local nowLocal = os.time()
                local nowUtc = os.date('!*t', nowLocal)
                local offset = nowLocal - os.time(nowUtc)
                local sign = (offset >= 0) and '+' or '-'
                local abs = math.abs(offset)
                tzLabel = string.format('UTC%s%02d:%02d', sign,
                    math.floor(abs / 3600), math.floor((abs % 3600) / 60))
            end
            local tzText = 'Timezone: ' .. tzLabel
            local windowWidth = 600
            pcall(function()
                local ww = imgui.GetWindowWidth()
                if type(ww) == 'number' then windowWidth = ww end
            end)
            local textWidth = 0
            pcall(function()
                local s = imgui.CalcTextSize(tzText)
                if type(s) == 'table' and s[1] then textWidth = s[1] end
            end)
            if textWidth <= 0 then textWidth = #tzText * 7 end
            imgui.SameLine(math.max(96, (windowWidth - textWidth) / 2))
            imgui.TextDisabled(tzText)
        end
        imgui.Separator()

        -- Two-column layout: left column carries the Web Sync controls
        -- (Event Presets, Queued / Active Events, Action buttons); right
        -- column carries the live work surfaces (Attendance, Loot Pool,
        -- ToD Capturing). The footer (CSV Export + Close) sits below both
        -- columns at full width. The negative bottom heights reserve
        -- ~50px at the launcher's bottom for the footer.
        local LEFT_COL_WIDTH = 470
        local COLS_BOTTOM    = -50
        imgui.BeginChild('lhsCol', { LEFT_COL_WIDTH, COLS_BOTTOM }, false)

        -- Web Sync (LSManager)
        if api.is_paired() then
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

                -- Both checkboxes start unchecked (syncStyleChosen=false).
                -- Picking one flips the flag and sets state.selectedMode;
                -- the other auto-unchecks via its own binding next frame.
                centerCursor(160)
                local regPtr = { syncStyleChosen[1] and state.selectedMode ~= 'HNM' }
                if imgui.Checkbox('Regular', regPtr) then
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
                if trimmedName ~= '' and syncStyleChosen[1] then
                    centerCursor(260)
                    if imgui.Button('Create Event: ' .. trimmedName .. '##syncCreateInline', { 260, 0 }) then
                        if api.is_paired() then
                            local d = callbacks.event_defaults or {}
                            local dkp = (state.selectedMode == 'HNM')
                                and tonumber(d.dkpPerWindowHnm)
                                or  tonumber(d.dkpPerHourRegular)
                            local created, err = api.create_event(trimmedName, state.selectedMode, nil, dkp)
                            if created and created.eventId then
                                state.lastSyncSummary = 'Created event: ' .. (created.name or trimmedName)
                                    .. ' (id ' .. tostring(created.eventId) .. ')'
                                local events = api.list_events()
                                if events then state.webEvents = events end
                                syncNewEventNamePtr[1] = ''
                            else
                                state.lastSyncSummary = 'Create failed: ' .. tostring(err)
                            end
                        else
                            state.lastSyncSummary = 'Not paired with web. Use /att link <code>.'
                        end
                    end
                end
            end

            do
                imgui.Dummy({ 0, 6 })
                imgui.Text('Event Presets')
            end
            do
                local EVENTS_PRESETS = { 'Sky', 'Sea', 'Dynamis', 'Limbus' }
                local HNM_DISPLAY_NAMES = {
                    ['Nidhogg']        = 'Fafnir/Nidhogg',
                    ['King Behemoth']  = 'Behemoth/King Behemoth',
                    ['Aspidochelone']  = 'Adamantoise/Aspidochelone',
                }
                -- Pull DKP rates from persisted settings (gear icon ->
                -- Settings popup). on_preset_button picks the right value
                -- (per-window for HNM-style categories, per-hour for
                -- everything else) at click time.
                local function presetDkpOpts()
                    local d = callbacks.event_defaults or {}
                    return {
                        dkpPerHour   = tonumber(d.dkpPerHourRegular) or 0,
                        dkpPerWindow = tonumber(d.dkpPerWindowHnm)   or 0,
                    }
                end
                imgui.BeginChild('attend_list', { 0, 180 }, true)
                for _, cat in ipairs(resources.attendCategoriesOrder) do
                    local catEvents = resources.attendCategories[cat] or {}
                    local renderEvents = catEvents
                    if cat == 'Events' then renderEvents = EVENTS_PRESETS end
                    if #renderEvents > 0 then
                        if imgui.CollapsingHeader(string.format('%s (%d)', cat, #renderEvents)) then
                            for _, ev in ipairs(renderEvents) do
                                local display = ev
                                if cat == 'HNMS' and HNM_DISPLAY_NAMES[ev] then
                                    display = HNM_DISPLAY_NAMES[ev]
                                end
                                if imgui.Button(string.format('%s##btn_%s', display, ev)) then
                                    if callbacks.on_preset_button then
                                        callbacks.on_preset_button(display, cat, presetDkpOpts())
                                    end
                                end
                            end
                        end
                    end
                end

                -- Testing presets: hardcoded so they don't pollute
                -- resources/creditnames.txt. Goblin Pathfinder routes through
                -- the Regular flow; Goblin Furrier through the HNM-Style
                -- (post-by-window) flow. The category 'Testing' is handled
                -- by on_preset_button alongside the production categories.
                local TESTING_PRESETS = { 'Goblin Pathfinder', 'Goblin Smithy', 'Goblin Furrier', 'Goblin Shaman' }
                if imgui.CollapsingHeader(string.format('Testing (%d)', #TESTING_PRESETS)) then
                    for _, ev in ipairs(TESTING_PRESETS) do
                        if imgui.Button(string.format('%s##btn_%s', ev, ev)) then
                            if callbacks.on_preset_button then
                                callbacks.on_preset_button(ev, 'Testing', presetDkpOpts())
                            end
                        end
                    end
                end
                imgui.EndChild()
            end

            -- Selection status moved to between the Queued Events and Active Events
            -- lists below so the indicator sits next to the lists it relates to.

            -- Split the cached events list into queued (not started) and active (live).
            local queued, active = {}, {}
            for _, ev in ipairs(state.webEvents or {}) do
                if ev.isLive then table.insert(active, ev) else table.insert(queued, ev) end
            end

            -- Compute a row-width budget once so Selectables on every row leave room for
            -- the cancel button without wrapping.
            local cancelBtnW = 24
            local rowAvailW = 540
            pcall(function()
                local r = imgui.GetContentRegionAvail()
                if type(r) == 'table' and r[1] then rowAvailW = r[1] end
            end)
            local selW = math.max(80, rowAvailW - cancelBtnW - 8)

            local function render_event_row(ev, allowCancel)
                -- The "(id N)" suffix is gated on the Settings toggle.
                -- The "##syncev_N" suffix stays so imgui IDs remain unique
                -- regardless of the visible label. Default is off — only
                -- the explicit `true` from the merged defaults turns it on.
                local showIds = (callbacks.event_defaults
                                and callbacks.event_defaults.showEventIds == true)
                local namePart = ev.name or '<unnamed>'
                local label
                if showIds then
                    label = string.format('%s (id %s)##syncev_%d',
                        namePart, tostring(ev.id), ev.id)
                else
                    label = string.format('%s##syncev_%d', namePart, ev.id)
                end
                if imgui.Selectable(label, state.linkedEventId == ev.id, 0, { selW, 0 }) then
                    state.linkedEventId = ev.id
                    state.linkedEventName = ev.name
                    state.pendingEventName = ev.name
                    -- Load this event's HNM window state from the per-event map so
                    -- previously posted windows aren't lost when the user navigates
                    -- to another event and comes back. Server-supplied ev.windowCount
                    -- is the source of truth; fall back to the local constants table.
                    state.windowStateByEvent = state.windowStateByEvent or {}
                    local entry = state.windowStateByEvent[ev.id]
                    local isMultiWindow = (tonumber(ev.windowCount) or constants.window_count_for(ev.name)) > 1
                    if entry then
                        state.windowMax      = entry.max
                        state.windowSequence = entry.sequence
                        state.windowRosters  = entry.rosters
                    elseif isMultiWindow and api.is_paired() then
                        -- No local cache yet (fresh addon load, etc.) — pull the
                        -- event's posted windows from the server so the user sees
                        -- previously-posted attendance instead of an empty tab list.
                        local detail, derr = api.get_event(ev.id)
                        local rosters, postedAt, maxSeq = {}, {}, 0
                        if detail and detail.windows then
                            for _, w in ipairs(detail.windows) do
                                local seq = tonumber(w.sequenceNumber) or 0
                                if seq > 0 then
                                    local snap = {}
                                    for _, att in ipairs(w.attendees or {}) do
                                        snap[#snap + 1] = {
                                            name     = att.characterName or '',
                                            jobsMain = att.jobName or '',
                                            jobsSub  = att.subJobName or '',
                                            zone     = att.zone or '',
                                        }
                                    end
                                    rosters[seq] = snap
                                    -- Convert the server's ISO-UTC postedAt into the
                                    -- viewer's local time so the "Posted at" line
                                    -- matches what the addon stores at post time.
                                    local localTable = constants.parse_iso_utc_to_local_table(tostring(w.postedAt or ''))
                                    postedAt[seq] = constants.format_posted_at(localTable) or tostring(w.postedAt or '')
                                    if seq > maxSeq then maxSeq = seq end
                                end
                            end
                        elseif derr then
                            state.lastSyncSummary = 'Could not load event windows: ' .. tostring(derr)
                        end
                        state.windowMax      = tonumber(ev.windowCount) or constants.window_count_for(ev.name)
                        state.windowSequence = maxSeq
                        state.windowRosters  = rosters
                        state.windowStateByEvent[ev.id] = {
                            max      = state.windowMax,
                            sequence = state.windowSequence,
                            rosters  = state.windowRosters,
                            postedAt = postedAt,
                        }
                    else
                        state.windowMax      = tonumber(ev.windowCount) or constants.window_count_for(ev.name)
                        state.windowSequence = 0
                        state.windowRosters  = {}
                        state.windowStateByEvent[ev.id] = {
                            max      = state.windowMax,
                            sequence = state.windowSequence,
                            rosters  = state.windowRosters,
                            postedAt = {},
                        }
                    end
                    if callbacks.on_launcher_scan then
                        callbacks.on_launcher_scan(ev.name, false)
                        state.lastScannedFor = 'event:' .. tostring(ev.id)
                    end
                end
                -- Cancel action lives next to the Clear button below the lists,
                -- not per-row, so we don't render a button here.
                _ = allowCancel
            end

            -- Queued Events: events that haven't been started yet (cancellable).
            imgui.Dummy({ 0, 6 })
            imgui.Text('Queued Events')
            imgui.BeginChild('syncQueued', { 0, 100 }, true)
            if #queued == 0 then
                imgui.TextDisabled('No Queued Events')
            else
                for _, ev in ipairs(queued) do render_event_row(ev, true) end
            end
            imgui.EndChild()

            -- Selection status (only when an event is currently chosen).
            -- Sits between the Queued and Active lists since selection can come from either.
            if state.linkedEventId then
                imgui.TextColored({ 0.6, 1.0, 0.6, 1.0 },
                    string.format('Selected: %s (id %d)', state.linkedEventName or '?', state.linkedEventId))
                imgui.SameLine()
                if imgui.Button('Clear##syncClear') then
                    if state.windowStateByEvent and state.linkedEventId then
                        state.windowStateByEvent[state.linkedEventId] = nil
                    end
                    state.linkedEventId   = nil
                    state.linkedEventName = nil
                    state.pendingEventName = nil  -- resets "Attendance for:" header
                    state.lastScannedFor  = nil
                    state.windowMax       = 1
                    state.windowSequence  = 0
                    state.windowRosters   = {}
                    state.activeWindowTab = nil
                    attendance.clear()
                end

                -- Delete: only offered for queued (not-yet-live) selections.
                -- Use the same `isLive` classifier the queued/active split above
                -- uses — gating on commencementStartTime alone would hide the
                -- button for events that were started AND ended but haven't
                -- been archived yet (Commencement set, End set, isLive=false).
                local linkedLive = false
                for _, ev in ipairs(state.webEvents or {}) do
                    if ev.id == state.linkedEventId and ev.isLive then
                        linkedLive = true
                        break
                    end
                end
                if not linkedLive then
                    imgui.SameLine()
                    if imgui.Button('Delete##syncDelete') then
                        local deletedId = state.linkedEventId
                        local deletedName = state.linkedEventName or '<unnamed>'
                        local _, err = api.cancel_event(deletedId)
                        if err then
                            state.lastSyncSummary = 'Delete failed: ' .. tostring(err)
                        else
                            state.lastSyncSummary = 'Deleted: ' .. deletedName
                            if state.windowStateByEvent then
                                state.windowStateByEvent[deletedId] = nil
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
                    end
                end
            end

            -- Active Events: events that have been started (live; not cancellable).
            imgui.Dummy({ 0, 6 })
            imgui.Text('Active Events')
            imgui.BeginChild('syncActive', { 0, 80 }, true)
            if #active == 0 then
                imgui.TextDisabled('No Active Events')
            else
                for _, ev in ipairs(active) do render_event_row(ev, false) end
            end
            imgui.EndChild()

            -- Start & Post / End Event for non-HNM selections is rendered in
            -- the right column alongside the Attendance roster (see the
            -- isHnmEvent branch below) so it sits next to the other "post"
            -- actions and the left column doesn't have to reserve footer
            -- height for it.
        end

        imgui.EndChild()  -- /lhsCol
        imgui.SameLine()
        imgui.BeginChild('rhsCol', { 0, COLS_BOTTOM }, false)

        -- Attendance Results (merged from the old standalone window).
        do
            imgui.Text('Attendance for: ' .. (state.pendingEventName or '<none>'))
            imgui.SameLine()
            imgui.TextDisabled(string.format('  Attendees: %d', #attendance.data))

            if state.launcherGather then
                imgui.SameLine()
                local left = math.max(0, state.launcherGather.fireAt - os.clock())
                imgui.TextDisabled(string.format('  scanning... %.1fs', left))
            end

            -- Event Timer: counts up from CommencementStartTime for the
            -- four event types that use a runtime ticker (Sky / Sea /
            -- Dynamis / Limbus). Mirrors the elapsed timer on the web app's
            -- active-event page. Pulled from the cached webEvents list so
            -- the value updates whenever Refresh runs.
            do
                local TIMER_TYPES = {
                    Sky = true, Sea = true, Dynamis = true, Limbus = true,
                }
                if state.linkedEventId and state.webEvents then
                    local linkedEv
                    for _, ev in ipairs(state.webEvents) do
                        if ev.id == state.linkedEventId then linkedEv = ev; break end
                    end
                    if linkedEv and linkedEv.commencementStartTime
                       and TIMER_TYPES[linkedEv.type or ''] then
                        local startEpoch = constants.parse_iso_utc_to_epoch(
                            tostring(linkedEv.commencementStartTime))
                        if startEpoch then
                            local elapsed = math.max(0, os.time() - startEpoch)
                            local h = math.floor(elapsed / 3600)
                            local m = math.floor((elapsed % 3600) / 60)
                            local s = elapsed % 60
                            imgui.SameLine()
                            imgui.TextColored({ 0.6, 1.0, 0.6, 1.0 }, string.format(
                                '  Event Timer: %02d:%02d:%02d', h, m, s))
                        end
                    end
                end
            end

            local windowMax     = state.windowMax or 1
            local windowSeq     = state.windowSequence or 0
            local isHnmEvent    = windowMax > 1
            local rosterChildId = 'arRoster'
            -- Negative BeginChild height = fill to within N pixels of the
            -- launcher's bottom. Reserve room for: Loot Pool panel
            -- (~150px: separator + header + scrollable content child +
            -- separator), ToD panel (~116px), plus the original footer
            -- (40px non-HNM / 64px HNM for "Post New Window" + window-
            -- status text on top of CSV/Close).
            -- Both branches now reserve room for an action bar between the
            -- roster and the Loot Pool: HNM has the Post-window + End Event
            -- pair, non-HNM has a single Start & Post (or End Event) button.
            local rosterHeight  = isHnmEvent and -330 or -330

            local function render_live_roster()
                local selfKey = (get_self_name() or ''):lower()
                if selfKey == '' then selfKey = nil end
                local i = 1
                while i <= #attendance.data do
                    local r = attendance.data[i]
                    local line = string.format('%s (%s | %s/%s)',
                        r.name, r.zone or '?', r.jobsMain or '?', r.jobsSub or '?')
                    if is_self_row(r, selfKey) then
                        imgui.TextColored(SELF_COLOR, line)
                    else
                        imgui.Text(line)
                    end
                    imgui.SameLine()
                    if imgui.SmallButton('Remove##arRem' .. i) then
                        table.remove(attendance.data, i)
                    else
                        i = i + 1
                    end
                end
            end

            -- Frozen roster for a posted window. Each row gets a Remove button so
            -- accidental posts can be undone server-side; the local snapshot is
            -- pruned in lock-step so the UI reflects the change immediately.
            local function render_frozen_roster(seq, snapshot)
                if not snapshot or #snapshot == 0 then
                    imgui.TextDisabled('No entries posted for this window.')
                    return
                end
                local i = 1
                while i <= #snapshot do
                    local r = snapshot[i]
                    local name = r.name or ''
                    -- "X "-prefixed entries are local-only ignores; treat the bare name
                    -- as the canonical character name when calling the server.
                    local cleanName = name:gsub('^X%s+', '')
                    imgui.Text(string.format('%s (%s | %s/%s)',
                        name, r.zone or '?', r.jobsMain or '?', r.jobsSub or '?'))
                    imgui.SameLine()
                    if imgui.SmallButton(string.format('Remove##winrm_%d_%d', seq, i)) then
                        local _, perr = api.remove_window_attendee(state.linkedEventId, seq, cleanName)
                        if perr then
                            state.lastSyncSummary = 'Remove failed: ' .. tostring(perr)
                        else
                            state.lastSyncSummary = string.format('Removed %s from window %d.', cleanName, seq)
                            table.remove(snapshot, i)
                        end
                    else
                        i = i + 1
                    end
                end
            end

            -- Posted-at lookup for the active event (used to label frozen tabs).
            local postedAt = nil
            if state.windowStateByEvent and state.linkedEventId then
                local entry = state.windowStateByEvent[state.linkedEventId]
                postedAt = entry and entry.postedAt or nil
            end

            imgui.BeginChild(rosterChildId, { 0, rosterHeight }, true)
            if not isHnmEvent then
                render_live_roster()
            else
                -- ImGui's BeginTabBar can't wrap onto multiple rows, so we
                -- render the window selector ourselves as a grid of
                -- Selectable widgets that breaks every BTNS_PER_ROW.
                -- state.activeWindowTab is either a frozen seq number or
                -- the literal string 'inprog' to mean "the trailing in-
                -- progress tab" (so a post that bumps windowSeq still
                -- leaves the live-roster view focused without extra
                -- bookkeeping).
                local BTNS_PER_ROW = 8
                local SEL_WIDTH    = 80
                local SEL_HEIGHT   = 22
                state.activeWindowTab = state.activeWindowTab or 'inprog'
                -- Repair stale state if it got pinned to a frozen seq that
                -- no longer exists (e.g. addon reload mid-event).
                if type(state.activeWindowTab) == 'number'
                   and state.activeWindowTab > windowSeq then
                    state.activeWindowTab = (windowSeq < windowMax) and 'inprog' or windowSeq
                end

                local hasInProgress = (windowSeq < windowMax)
                local totalTabs = windowSeq + (hasInProgress and 1 or 0)
                for tabIdx = 1, totalTabs do
                    local col = (tabIdx - 1) % BTNS_PER_ROW
                    if tabIdx > 1 and col > 0 then
                        imgui.SameLine()
                    end
                    local isInProg = hasInProgress and (tabIdx == totalTabs)
                    local seqForLabel = isInProg and (windowSeq + 1) or tabIdx
                    local labelText = constants.window_label(state.linkedEventName, seqForLabel)
                    local idSuffix  = isInProg and 'inprog' or tostring(tabIdx)
                    local isActive  = isInProg
                        and (state.activeWindowTab == 'inprog')
                        or  (state.activeWindowTab == tabIdx)
                    if imgui.Selectable(labelText .. '##wsel_' .. idSuffix,
                            isActive, 0, { SEL_WIDTH, SEL_HEIGHT }) then
                        state.activeWindowTab = isInProg and 'inprog' or tabIdx
                    end
                end

                imgui.Separator()

                -- Render content for whichever tab is active. Frozen
                -- windows show their snapshot + posted-at stamp; the in-
                -- progress tab shows the live editable roster.
                local active = state.activeWindowTab
                if active == 'inprog' then
                    render_live_roster()
                elseif type(active) == 'number'
                       and active >= 1
                       and active <= windowSeq then
                    local stamp = postedAt and postedAt[active]
                    if stamp then
                        imgui.TextDisabled('Posted at ' .. stamp)
                    end
                    render_frozen_roster(active, state.windowRosters[active])
                end
            end
            imgui.EndChild()

            -- Non-HNM action bar: a single right-aligned Start & Post (or
            -- End Event, once live) button, mirroring where the HNM Post
            -- buttons sit. Lives in the right column so it doesn't compete
            -- with the Selected/Clear/Delete row in the left column.
            if state.linkedEventId and not isHnmEvent then
                local SINGLE_BTN_W = 280
                local barWindowWidth = 600
                pcall(function()
                    local ww = imgui.GetWindowWidth()
                    if type(ww) == 'number' then barWindowWidth = ww end
                end)

                local linkedLive = false
                for _, ev in ipairs(state.webEvents or {}) do
                    if ev.id == state.linkedEventId and ev.isLive then
                        linkedLive = true
                        break
                    end
                end

                imgui.SetCursorPosX(barWindowWidth - SINGLE_BTN_W - 16)
                if linkedLive then
                    if imgui.Button('End Event: ' .. (state.linkedEventName or '?') .. '##syncEndEvent', { SINGLE_BTN_W, 0 }) then
                        if callbacks.on_end_event then
                            callbacks.on_end_event()
                        end
                    end
                else
                    if imgui.Button('Start & Post: ' .. (state.linkedEventName or '?') .. '##syncStartPost', { SINGLE_BTN_W, 0 }) then
                        if callbacks.on_start_and_post then
                            local d = callbacks.event_defaults or {}
                            state.lastSyncSummary = callbacks.on_start_and_post({
                                eventId      = state.linkedEventId,
                                eventName    = state.linkedEventName,
                                isAutoCreate = false,
                                csvOnStart   = state.launcherCsvOnStart,
                                dkpPerHour   = tonumber(d.dkpPerHourRegular) or 0,
                                dkpPerWindow = tonumber(d.dkpPerWindowHnm)   or 0,
                            })
                        end
                    end
                end
            end

            -- HNM-only action area: Post / "All windows posted" hint on
            -- the top row, End Event button on its own row below. Two
            -- rows is more reliable than the SameLine + SetCursorPosX
            -- side-by-side layout, and gives End Event a permanent
            -- right-aligned anchor regardless of post-button state.
            if isHnmEvent and state.linkedEventId then
                local POST_BTN_W   = 220
                local END_BTN_W    = 120
                local barWindowWidth = 600
                pcall(function()
                    local ww = imgui.GetWindowWidth()
                    if type(ww) == 'number' then barWindowWidth = ww end
                end)

                -- Row 1: Post button (still windows to post) OR
                -- "All N windows posted." hint.
                if windowSeq < windowMax then
                    local nextSeq = windowSeq + 1
                    -- First window starts the event AND posts; subsequent
                    -- windows just post (the event is already live), so
                    -- drop the "Start &" prefix from the second post on.
                    local prefix = (nextSeq == 1) and 'Start & Post' or 'Post'
                    local label = string.format('%s: %s (%d/%d)##postWindow',
                        prefix,
                        constants.window_label(state.linkedEventName, nextSeq),
                        nextSeq, windowMax)
                    imgui.SetCursorPosX(barWindowWidth - POST_BTN_W - 16)
                    if imgui.Button(label, { POST_BTN_W, 0 }) and callbacks.on_start_and_post then
                        local d = callbacks.event_defaults or {}
                        state.lastSyncSummary = callbacks.on_start_and_post({
                            eventId      = state.linkedEventId,
                            eventName    = state.linkedEventName,
                            isAutoCreate = false,
                            csvOnStart   = state.launcherCsvOnStart,
                            dkpPerHour   = tonumber(d.dkpPerHourRegular) or 0,
                            dkpPerWindow = tonumber(d.dkpPerWindowHnm)   or 0,
                        })
                    end
                else
                    imgui.SetCursorPosX(barWindowWidth - POST_BTN_W - 16)
                    imgui.TextDisabled(string.format('All %d windows posted.', windowMax))
                end

                -- Row 2: End Event button. Visible once the event has at
                -- least one posted window (i.e. it is actually live on
                -- the server). Right-aligned so it doesn't drift around.
                if windowSeq > 0 then
                    imgui.SetCursorPosX(barWindowWidth - END_BTN_W - 16)
                    if imgui.Button('End Event##hnmEndEvent', { END_BTN_W, 0 }) then
                        if callbacks.on_end_event then
                            callbacks.on_end_event()
                        end
                    end
                end
            end
        end
        imgui.Separator()

        -- Loot Pool panel: shows items detected in chat ("You find X on the
        -- <monster>.") for the most recent capture matching the selected
        -- preset (state.pendingEventName). Each row carries a "Post Loot"
        -- button that opens an inline Winner / DKP Spent form bound to the
        -- per-drop draft state set up in att.lua's text_in handler.
        do
            -- Loot pool tracks the *most recent* capture by default. If the
            -- user explicitly clicked an Event Preset, prefer a capture
            -- matching that monster (so clicking a different preset can
            -- pivot back to that monster's loot pool while it's still in
            -- the ring buffer). Otherwise fall through to the newest
            -- capture so kills made without pre-selecting a preset still
            -- surface their drops.
            local function find_active_capture()
                local captures = state.todCaptures or {}
                local target = state.pendingEventName
                if target and target ~= '' then
                    for idx, cap in ipairs(captures) do
                        if cap.monster == target then return cap, idx end
                    end
                end
                if captures[1] then return captures[1], 1 end
                return nil, -1
            end

            local activeCap, activeIdx = find_active_capture()
            local titleMonster = (activeCap and activeCap.monster)
                or state.pendingEventName or '<none>'

            imgui.Text(titleMonster .. ' Loot Pool')

            -- Right-aligned Clear button on the title row. Wipes only the drops
            -- for the active capture, never the parent ToD entry — clearing the
            -- loot pool shouldn't make the matching ToD line disappear.
            local hasDrops = activeCap and activeCap.lootDrops and #activeCap.lootDrops > 0
            if hasDrops then
                local LOOT_CLEAR_W = 70
                local lootWindowWidth = 600
                pcall(function()
                    local ww = imgui.GetWindowWidth()
                    if type(ww) == 'number' then lootWindowWidth = ww end
                end)
                imgui.SameLine(lootWindowWidth - LOOT_CLEAR_W - 16)
                if imgui.Button('Clear##lootClear', { LOOT_CLEAR_W, 0 }) then
                    activeCap.lootDrops = {}
                end
            end

            imgui.BeginChild('lootPool', { 0, 110 }, false)

            if not activeCap then
                imgui.TextDisabled('  Pick an Event Preset to see its loot pool.')
            else
                local drops = activeCap.lootDrops or {}
                if #drops == 0 then
                    imgui.TextDisabled('  Drops will appear here as items fall in chat.')
                else
                    for dIdx, drop in ipairs(drops) do
                        if drop.posted then
                            -- Posted state: greyed item + green confirmation.
                            imgui.Text('  ' .. tostring(drop.itemName or '?'))
                            imgui.SameLine()
                            imgui.TextColored({ 0.4, 1.0, 0.4, 1.0 }, string.format(
                                'Posted: %s -- %s DKP',
                                tostring(drop.posted.itemWinner or '?'),
                                tostring(drop.posted.winningDkpSpent or '?')))
                        elseif drop.draftOpen then
                            -- Inline form. Winner combo + DKP int input + Save/Cancel.
                            imgui.Text('  ' .. tostring(drop.itemName or '?'))
                            imgui.Indent(20)
                            local cache = state.rosterCache
                            local names = (cache and cache.names) or {}
                            local lootStruct = (cache and cache.lootStructure) or 'Dkp'

                            -- Winner combo. Falls back to a text input when
                            -- the roster cache is empty (initial fetch in
                            -- flight or fetch failed) so the user can still
                            -- type a name manually.
                            imgui.Text('Winner:')
                            imgui.SameLine()
                            imgui.PushItemWidth(160)
                            if #names == 0 then
                                local winnerPtr = { drop.draft.winner or '' }
                                if imgui.InputText(string.format('##winner_%d_%d', activeIdx, dIdx),
                                        winnerPtr, 64) then
                                    drop.draft.winner = winnerPtr[1] or ''
                                end
                            else
                                local current = drop.draft.winner or ''
                                if imgui.BeginCombo(string.format('##winnerCombo_%d_%d', activeIdx, dIdx),
                                        current ~= '' and current or 'Select winner') then
                                    for _, name in ipairs(names) do
                                        local selected = (name == current)
                                        if imgui.Selectable(name, selected) then
                                            drop.draft.winner = name
                                        end
                                        if selected then imgui.SetItemDefaultFocus() end
                                    end
                                    imgui.EndCombo()
                                end
                            end
                            imgui.PopItemWidth()
                            imgui.SameLine()

                            local dkpLabel = (lootStruct == 'Hybrid') and 'Deduction %:' or 'DKP Spent:'
                            imgui.Text(dkpLabel)
                            imgui.SameLine()
                            imgui.PushItemWidth(80)
                            local dkpPtr = { tostring(drop.draft.dkpSpent or '') }
                            if imgui.InputText(string.format('##dkp_%d_%d', activeIdx, dIdx),
                                    dkpPtr, 16) then
                                drop.draft.dkpSpent = dkpPtr[1] or ''
                            end
                            imgui.PopItemWidth()
                            imgui.SameLine()

                            if drop.posting then
                                imgui.TextDisabled('Saving...')
                            else
                                if imgui.Button(string.format('Save##save_%d_%d', activeIdx, dIdx),
                                        { 60, 0 }) then
                                    if callbacks.on_post_loot then
                                        callbacks.on_post_loot(activeIdx, dIdx)
                                    end
                                end
                                imgui.SameLine()
                                if imgui.Button(string.format('Cancel##cancel_%d_%d', activeIdx, dIdx),
                                        { 60, 0 }) then
                                    drop.draftOpen = false
                                    drop.postError = nil
                                end
                            end

                            if drop.postError then
                                imgui.TextColored({ 1.0, 0.5, 0.5, 1.0 },
                                    'Post failed: ' .. tostring(drop.postError))
                            end
                            imgui.Unindent(20)
                        else
                            -- Default state: item + Post Loot button. The
                            -- server endpoint still requires a parent Tod
                            -- row, so on_post_loot auto-posts a ToD first
                            -- when cap.posted is nil. From the UI's POV,
                            -- Post Loot is always available.
                            imgui.Text('  ' .. tostring(drop.itemName or '?'))
                            local pcWidth = 600
                            pcall(function()
                                local ww = imgui.GetWindowWidth()
                                if type(ww) == 'number' then pcWidth = ww end
                            end)
                            imgui.SameLine(pcWidth - 100)
                            if imgui.Button(string.format('Post Loot##postLoot_%d_%d',
                                    activeIdx, dIdx), { 80, 0 }) then
                                drop.draftOpen = true
                                drop.postError = nil
                                if callbacks.on_load_roster then
                                    callbacks.on_load_roster(false)
                                end
                            end
                        end
                    end
                end
            end

            if state.rosterError then
                imgui.TextColored({ 1.0, 0.5, 0.5, 1.0 },
                    '  Roster error: ' .. tostring(state.rosterError))
            end

            imgui.EndChild()
        end

        imgui.Separator()

        -- ToD Capture panel: shows recent HNM defeat lines parsed by the
        -- text_in callback in att.lua. Session-only (no disk persist), with
        -- a master toggle and a manual Clear so the user can dismiss the
        -- list once they've recorded the ToD elsewhere.
        do
            imgui.Text('ToD Capturing')
            imgui.SameLine()
            local todPtr = { state.todCaptureEnabled and true or false }
            if imgui.Checkbox('##todToggle', todPtr) then
                state.todCaptureEnabled = todPtr[1]
            end
            imgui.SameLine()
            imgui.TextDisabled(state.todCaptureEnabled and '(listening)' or '(disabled)')

            -- Right-aligned Clear button mirrors the Close-button placement
            -- below so the panel feels visually consistent with the footer.
            do
                local CLEAR_W = 70
                local todWindowWidth = 600
                pcall(function()
                    local ww = imgui.GetWindowWidth()
                    if type(ww) == 'number' then todWindowWidth = ww end
                end)
                imgui.SameLine(todWindowWidth - CLEAR_W - 16)
                if imgui.Button('Clear##todClear', { CLEAR_W, 0 }) then
                    -- Hide the visible ToD rows but keep the underlying captures
                    -- (and any nested lootDrops) intact, so clearing the ToD list
                    -- doesn't also wipe the Loot Pool entries that depend on the
                    -- same capture rows. The render loop below skips todHidden.
                    for _, cap in ipairs(state.todCaptures or {}) do
                        cap.todHidden = true
                    end
                    state.todLastCaptureKey   = nil
                    state.todLastCaptureClock = 0
                end
            end

            -- Captures area is wrapped in a fixed-height BeginChild so the
            -- ring-buffer rows can't push CSV Export / Close off the bottom
            -- of the launcher. Content scrolls inside this region when it
            -- exceeds 100px (e.g. all 3 ring slots populated).
            imgui.BeginChild('todCaptures', { 0, 100 }, false)
            local captures = state.todCaptures or {}
            -- Count visible (non-hidden) captures up front so the empty hint
            -- only fires when the user-visible list is genuinely empty.
            local visibleCount = 0
            for _, cap in ipairs(captures) do
                if not cap.todHidden then visibleCount = visibleCount + 1 end
            end
            if visibleCount == 0 then
                imgui.TextDisabled('  No captures yet. ToDs for selected enemies in your range will appear here.')
            else
                local rendered = 0
                for i, cap in ipairs(captures) do
                    if not cap.todHidden then
                        rendered = rendered + 1
                        imgui.TextColored({ 1.0, 0.85, 0.2, 1.0 }, '  ToD Captured!')

                        local monster    = tostring(cap.monster    or '?')
                        local message    = tostring(cap.message    or '')
                        local capturedAt = tostring(cap.callbackAt or '?')
                        -- Scrub stray 0x00..0x1F bytes that survived clean_str
                        -- so they can't break imgui rendering downstream.
                        message = message:gsub('[%z\1-\31]', ' '):gsub('%s+', ' ')
                        if message == '' then message = '<no message text>' end

                        imgui.Text('  ' .. monster .. ':')
                        imgui.Text('    ' .. message)
                        imgui.Text('    Captured at: ' .. capturedAt)

                        -- Post ToD button + state. Three states, mutually exclusive:
                        --   posted   -> green "Posted ✓" + repop hint
                        --   posting  -> disabled "Posting..."
                        --   default  -> "Post ToD" button, plus optional error line
                        if cap.posted then
                            imgui.TextColored({ 0.4, 1.0, 0.4, 1.0 }, '    Posted to web!')
                            if cap.posted.repopFormatted then
                                imgui.Text('    Repop: ' .. tostring(cap.posted.repopFormatted))
                            end
                        elseif cap.posting then
                            imgui.TextDisabled('    Posting...')
                        else
                            imgui.Indent(20)
                            if imgui.Button('Post ToD##postTod' .. tostring(i), { 90, 0 }) then
                                if callbacks.on_post_tod then
                                    callbacks.on_post_tod(i)
                                end
                            end
                            imgui.Unindent(20)
                            if cap.postError then
                                imgui.TextColored({ 1.0, 0.5, 0.5, 1.0 },
                                    '    Post failed: ' .. tostring(cap.postError))
                            end
                        end

                        if rendered < visibleCount then imgui.Separator() end
                    end
                end
            end
            imgui.EndChild()
        end

        imgui.EndChild()  -- /rhsCol

        imgui.Separator()

        -- "Write CSV on start" on the LEFT, label before checkbox.
        imgui.Text('CSV Export')
        imgui.SameLine()
        local csvPtr = { state.launcherCsvOnStart }
        if imgui.Checkbox('##bottomCsv', csvPtr) then
            state.launcherCsvOnStart = csvPtr[1]
        end

        -- Close button right-aligned.
        do
            local CLOSE_BTN_W = 70
            local windowWidth = 600
            pcall(function()
                local ww = imgui.GetWindowWidth()
                if type(ww) == 'number' then windowWidth = ww end
            end)
            imgui.SameLine(windowWidth - CLOSE_BTN_W - 16)
            if imgui.Button('Close##attend', { CLOSE_BTN_W, 0 }) then openPtr[1] = false end
        end

        imgui.End()
    end
    return openPtr[1]
end


function ui.draw_composition_window(is_open, comp_module)
    if not is_open or not comp_module.results then return false end

    -- Calculate Required Width
    -- Req Column (300) + Padding (~30) + Alliances (Count * (310 + 8 padding))
    local allianceCount = (comp_module.party_results and comp_module.party_results.alliances) and #comp_module.party_results.alliances or 1
    local targetWidth = 340 + (allianceCount * 320)
    if targetWidth < 800 then targetWidth = 800 end -- Min width
    
    -- Dynamic Resize if count changed
    comp_module.uiState = comp_module.uiState or {}
    if comp_module.uiState.lastAllianceCount ~= allianceCount then
        imgui.SetNextWindowSize({ targetWidth, 600 }, ImGuiCond_Always)
        comp_module.uiState.lastAllianceCount = allianceCount
    else
        imgui.SetNextWindowSize({ targetWidth, 600 }, ImGuiCond_FirstUseEver)
    end

    local openPtr = { is_open }
    if imgui.Begin('Composition Check: ' .. (comp_module.currentEvent or 'Unknown'), openPtr) then
        
        local res = comp_module.results
        
        -- LEFT COLUMN: Requirements
        imgui.BeginChild('col_req', { 300, -40 }, true)
        imgui.Text('Requirements')
        imgui.Separator()
        
        local function draw_section(title, data)
            imgui.TextColored({0.4, 1.0, 0.4, 1.0}, title)
            
            for _, entry in ipairs(data) do
                local have = #entry.filled
                local need = entry.needed
                local color = (have >= need) and {0.6, 1.0, 0.6, 1.0} or {1.0, 0.4, 0.4, 1.0}
                
                imgui.TextColored(color, string.format('[%d/%d] %s', have, need, entry.role))
                if have > 0 then
                    for _, p in ipairs(entry.filled) do
                        imgui.Indent(15)
                        -- Selectable Name
                        local is_selected = (comp_module.selected_player == p.name)
                        if imgui.Selectable(string.format('%s (%s/%s)', p.name, p.jobMain, p.jobSub), is_selected) then
                            -- Toggle selection
                            if is_selected then
                                comp_module.selected_player = nil
                            else
                                comp_module.selected_player = p.name
                            end
                        end
                        imgui.Unindent(15)
                    end
                end
            end
            imgui.Spacing()
        end

        draw_section('Required', res.required)
        draw_section('Suggested', res.suggested)
        
        imgui.Separator()
        imgui.Separator()
        
        -- Persistent buffers
        comp_module.uiState.newName = comp_module.uiState.newName or { '' }
        
        -- Header
        imgui.Text('Unassigned Pool')
        imgui.Separator()

        -- Search Filter
        imgui.InputText('Filter', filterPtr, 64)
        local filter_str = (filterPtr[1] or ''):lower()
        
        imgui.Separator()
        
        -- Gather and Sort from Dynamic Party Results
        local display_list = {}
        local source_pool = (comp_module.party_results and comp_module.party_results.unassigned) or res.unassigned
        
        for _, p in ipairs(source_pool) do
            local text = string.format('%s %s/%s', p.name, p.jobMain, p.jobSub):lower()
            if filter_str == '' or text:find(filter_str) then
                table.insert(display_list, p)
            end
        end
        table.sort(display_list, function(a,b) return a.name < b.name end)
        
        -- 4. Render List
        imgui.BeginChild('unassigned_list_inner', { 0, 0 }, true) -- Fill remaining height
        for _, p in ipairs(display_list) do
            local label = string.format('%s (%s/%s)', p.name, p.jobMain, p.jobSub)
            local is_selected = (comp_module.selected_player == p.name)
            
            if imgui.Selectable(label, is_selected) then
                if is_selected then
                    comp_module.selected_player = nil
                else
                    comp_module.selected_player = p.name
                end
            end
        end
        
        -- Clickable "Blank Space" to Unassign
        -- Use { -1, -1 } to fill remaining content region
        -- Make it transparent/background color
        imgui.PushStyleColor(ImGuiCol_Button, {0,0,0,0})
        imgui.PushStyleColor(ImGuiCol_ButtonHovered, {0,0,0,0})
        imgui.PushStyleColor(ImGuiCol_ButtonActive, {0,0,0,0})
        imgui.PushStyleColor(ImGuiCol_Border, {0,0,0,0})
        
        if imgui.Button('##drop_unassign', { -1, -1 }) then
             if comp_module.selected_player then
                comp_module.unassign_player(comp_module.selected_player)
                comp_module.selected_player = nil
             end
        end
        imgui.PopStyleColor(4)
        
        imgui.EndChild()
        imgui.EndChild()
        
        imgui.SameLine()
        
        -- RIGHT COLUMN: Parties
        imgui.BeginChild('col_party', { 0, -40 }, true)
        if imgui.Button('Create Alliance') then
            comp_module.add_group(comp_module.currentEvent)
        end
        imgui.Separator()
        
        if comp_module.party_results and comp_module.party_results.alliances then
            for aIdx, alliance in ipairs(comp_module.party_results.alliances) do
                if aIdx > 1 then imgui.SameLine() end
                
                -- Use Sub-Window for Alliance
                imgui.BeginChild('alliance_' .. aIdx, { 310, 0 }, true)
                imgui.TextColored({0.4, 0.8, 1.0, 1.0}, alliance.name)
                imgui.SameLine()
                if imgui.SmallButton('Delete##del_all_' .. aIdx) then
                    comp_module.remove_alliance(aIdx)
                end
                imgui.Separator()
                
                for pIdx, p in ipairs(alliance.parties) do
                    imgui.Text(p.name)
                    for i = 1, 6 do
                        if p.members[i] then
                            local m = p.members[i]
                            if m.empty then
                                -- Empty Slot
                                local label = string.format('%d. [%s] ---##%d-%d-%d', i, m.role, aIdx, pIdx, i)
                                if imgui.Selectable(label) then
                                    if comp_module.selected_player then
                                        comp_module.manual_assign(comp_module.selected_player, aIdx, pIdx, i)
                                        comp_module.selected_player = nil
                                    end
                                end
                            else
                                -- Filled Slot
                                local label = string.format('%d. [%s] %s (%s)##%d-%d-%d', i, m.role, m.name, m.jobMain, aIdx, pIdx, i)
                                local is_selected = (comp_module.selected_player == m.name)
                                if imgui.Selectable(label, is_selected) then
                                    if comp_module.selected_player and comp_module.selected_player ~= m.name then
                                        -- Swap
                                        comp_module.manual_assign(comp_module.selected_player, aIdx, pIdx, i)
                                        comp_module.selected_player = nil 
                                    else
                                        -- Select
                                        if is_selected then
                                            comp_module.selected_player = nil
                                        else
                                            comp_module.selected_player = m.name
                                        end
                                    end
                                end
                            end
                        else
                             imgui.TextDisabled(string.format('%d. ---', i))
                        end
                    end
                    imgui.Separator()
                end
                
                imgui.EndChild() -- End Alliance Window
            end
            
        else
            imgui.TextDisabled('No parties built yet. Click Create Alliance.')
        end
        
        imgui.EndChild()
        
        -- Footer Buttons
        imgui.Separator()
        
        if imgui.Button('Close') then openPtr[1] = false end
        imgui.SameLine()
        if imgui.Button('Refresh Roster') then
             -- 1. Gather (Async-like but immediate call here)
             if att_module and comp_module.currentEvent then
                att_module.gather_zone(comp_module.currentEvent)
                -- 2. Update Comp Pool
                comp_module.refresh_unassigned(att_module.data)
             end
        end

        imgui.End()
    end
    
    return openPtr[1]
end

-- Debug window removed

-- Settings popup. Draws a small floating window with the per-installation
-- DKP defaults (per-hour for Regular events, per-window for HNM events).
-- Values are pulled from / written back to a config table the caller
-- supplies; the caller is responsible for calling settings.save() when
-- closing so the values persist across addon reloads. Returns the new
-- open-state to mirror the convention used by draw_launcher.
local syncSettingsDkpHourPtr     = { '' }
local syncSettingsDkpWindowPtr   = { '' }
local syncSettingsShowIdsPtr     = { false }
local syncSettingsAddMonsterPtr  = { '' }
local syncSettingsBoundFor       = nil  -- identity tracker so we re-prime
                                         -- the inputs when the window opens

function ui.draw_settings(is_open, state, callbacks)
    if not is_open then
        syncSettingsBoundFor = nil
        return false
    end

    local cfg = (callbacks and callbacks.event_defaults) or {}

    -- Re-seed the input ptrs from cfg when the window first opens (or
    -- after a /addon reload). Use a per-frame identity check so live
    -- typing isn't clobbered.
    if syncSettingsBoundFor ~= cfg then
        syncSettingsDkpHourPtr[1]   = tostring(cfg.dkpPerHourRegular or 0)
        syncSettingsDkpWindowPtr[1] = tostring(cfg.dkpPerWindowHnm   or 0)
        syncSettingsShowIdsPtr[1]   = (cfg.showEventIds == true)   -- default false
        syncSettingsBoundFor        = cfg
    end

    imgui.SetNextWindowSize({ 460, 540 }, ImGuiCond_FirstUseEver)
    local openPtr = { is_open }
    if imgui.Begin('att Settings', openPtr) then
        imgui.Text('Default DKP rates for events created from the addon:')
        imgui.Dummy({ 0, 6 })

        imgui.Text('Regular events  -  DKP / Hour')
        imgui.PushItemWidth(100)
        imgui.InputText('##settDkpHour', syncSettingsDkpHourPtr, 8)
        imgui.PopItemWidth()

        imgui.Dummy({ 0, 6 })
        imgui.Text('HNM events      -  DKP / Window')
        imgui.PushItemWidth(100)
        imgui.InputText('##settDkpWindow', syncSettingsDkpWindowPtr, 8)
        imgui.PopItemWidth()

        imgui.Dummy({ 0, 10 })
        imgui.Separator()
        imgui.Dummy({ 0, 6 })

        imgui.Text('Display:')
        imgui.SameLine()
        if imgui.Checkbox('Show event IDs in Queued / Active lists', syncSettingsShowIdsPtr) then
            -- bound directly; nothing else to do
        end

        imgui.Dummy({ 0, 10 })
        imgui.Separator()
        imgui.Dummy({ 0, 6 })

        -- ToD Capture monster list. Built-in entries (HNMs + Testing) are
        -- read-only; user-added entries can be removed inline. Add input
        -- at the bottom; Add button auto-saves to disk so the new entry
        -- survives /addon reload.
        imgui.Text('ToD Capture monsters')
        imgui.TextDisabled('  Built-in monsters and any custom names you add are matched against incoming defeat lines.')

        imgui.BeginChild('settMonstersList', { 0, 200 }, true)

        local builtInHnms     = (callbacks and callbacks.built_in_hnms)    or {}
        local builtInSky      = (callbacks and callbacks.built_in_sky)     or {}
        local builtInTesting  = (callbacks and callbacks.built_in_testing) or {}
        local customMonsters  = (callbacks and callbacks.custom_monsters)  or {}

        -- Helper: render a list of monster names in a 3-column grid using
        -- imgui.SameLine(<x>) for absolute-X column alignment. Saves a lot
        -- of vertical space versus the previous one-per-row layout.
        local function render_monster_grid(names)
            local COLS    = 3
            local COL_W   = 140  -- pixels per column (fits up to ~16-char names)
            local INDENT  = 12
            for i, n in ipairs(names) do
                local col = (i - 1) % COLS
                if col == 0 then
                    imgui.TextDisabled('  ' .. tostring(n))
                else
                    imgui.SameLine(INDENT + col * COL_W)
                    imgui.TextDisabled(tostring(n))
                end
            end
        end

        -- Built-in HNMs.
        local hnmNames = {}
        for n, _ in pairs(builtInHnms) do hnmNames[#hnmNames + 1] = n end
        table.sort(hnmNames)
        if #hnmNames > 0 then
            imgui.TextColored({ 0.6, 0.85, 1.0, 1.0 }, 'Built-in HNMs:')
            render_monster_grid(hnmNames)
            imgui.Dummy({ 0, 4 })
        end

        -- Built-in Sky-farm NMs.
        local skyNames = {}
        for n, _ in pairs(builtInSky) do skyNames[#skyNames + 1] = n end
        table.sort(skyNames)
        if #skyNames > 0 then
            imgui.TextColored({ 0.7, 1.0, 0.7, 1.0 }, 'Built-in Sky NMs:')
            render_monster_grid(skyNames)
            imgui.Dummy({ 0, 4 })
        end

        -- Built-in Testing monsters.
        local testNames = {}
        for n, _ in pairs(builtInTesting) do testNames[#testNames + 1] = n end
        table.sort(testNames)
        if #testNames > 0 then
            imgui.TextColored({ 0.85, 0.85, 0.85, 1.0 }, 'Built-in Testing:')
            render_monster_grid(testNames)
            imgui.Dummy({ 0, 4 })
        end

        -- User-added monsters. Each row gets a Remove button.
        imgui.TextColored({ 1.0, 0.85, 0.4, 1.0 }, 'Custom (user-added):')
        if #customMonsters == 0 then
            imgui.TextDisabled('  (none yet)')
        else
            for i, n in ipairs(customMonsters) do
                imgui.Text('  ' .. tostring(n))
                imgui.SameLine()
                if imgui.Button('Remove##settRemMonster_' .. tostring(i), { 70, 0 }) then
                    if callbacks and callbacks.on_remove_custom_monster then
                        callbacks.on_remove_custom_monster(i)
                    end
                end
            end
        end

        imgui.EndChild()

        -- Add row: text input + Add button. Auto-saves on add.
        imgui.PushItemWidth(220)
        imgui.InputText('##settAddMonster', syncSettingsAddMonsterPtr, 64)
        imgui.PopItemWidth()
        imgui.SameLine()
        if imgui.Button('Add monster##settAddMonsterBtn', { 110, 0 }) then
            local raw = syncSettingsAddMonsterPtr[1] or ''
            if raw:gsub('%s', '') ~= '' then
                if callbacks and callbacks.on_add_custom_monster then
                    callbacks.on_add_custom_monster(raw)
                end
                syncSettingsAddMonsterPtr[1] = ''
            end
        end

        imgui.Dummy({ 0, 12 })
        imgui.Separator()

        if imgui.Button('Save##settSave', { 90, 0 }) then
            cfg.dkpPerHourRegular = tonumber(syncSettingsDkpHourPtr[1])   or 0
            cfg.dkpPerWindowHnm   = tonumber(syncSettingsDkpWindowPtr[1]) or 0
            cfg.showEventIds      = syncSettingsShowIdsPtr[1] and true or false
            if callbacks and callbacks.on_settings_save then
                callbacks.on_settings_save()
            end
            openPtr[1] = false
        end
        imgui.SameLine()
        if imgui.Button('Cancel##settCancel', { 90, 0 }) then
            openPtr[1] = false
        end

        imgui.End()
    end

    return openPtr[1]
end

return ui
