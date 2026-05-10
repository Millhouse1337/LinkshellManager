-- ui/launcher_event_lists.lua
-- Event Presets + Queued Events list + Selection bar + Active Events list.
-- Extracted from ui.lua byte-for-byte.
local imgui      = require('imgui')
local api        = require('api')
local resources  = require('resources')
local constants  = require('constants')
local attendance = require('attendance')

local M = {}

function M.draw(state, callbacks)
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
        -- HNMs whose linkshells traditionally track a "day" counter for
        -- each spawn cycle. Renders an inline Day input next to the
        -- preset button; non-empty values become a "D<n>" suffix on the
        -- event name when the preset is clicked.
        local DAY_TRACKED_HNMS = {
            ['Nidhogg']       = true,
            ['King Behemoth'] = true,
            ['Aspidochelone'] = true,
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

                        -- Render the inline Day input first (when applicable)
                        -- so the cursor position when we draw the button is
                        -- already past the input + label.
                        local dayValue = nil
                        if cat == 'HNMS' and DAY_TRACKED_HNMS[ev] then
                            state.eventPresetDayInputs = state.eventPresetDayInputs or {}
                            local dayPtr = { state.eventPresetDayInputs[ev] or '' }
                            imgui.Text('Day:')
                            imgui.SameLine()
                            imgui.PushItemWidth(50)
                            if imgui.InputText('##presetDay_' .. ev, dayPtr, 8) then
                                state.eventPresetDayInputs[ev] = dayPtr[1] or ''
                            end
                            imgui.PopItemWidth()
                            imgui.SameLine()
                            dayValue = state.eventPresetDayInputs[ev]
                        end

                        if imgui.Button(string.format('%s##btn_%s', display, ev)) then
                            -- Day is forwarded as a separate arg so the
                            -- callback can apply the "D<n>" suffix to the
                            -- server-side event name only — local lookups
                            -- (credit zones, search areas, window count)
                            -- still resolve against the canonical display
                            -- name like "Fafnir/Nidhogg".
                            local trimmed = dayValue and dayValue:gsub('%s', '') or ''
                            local dayArg = (trimmed ~= '') and trimmed or nil
                            if callbacks.on_preset_button then
                                callbacks.on_preset_button(display, cat, presetDkpOpts(), dayArg)
                            end
                        end
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
    imgui.BeginChild('syncQueued', { 0, 160 }, true)
    if #queued == 0 then
        imgui.TextDisabled('No Queued Events')
    else
        for _, ev in ipairs(queued) do render_event_row(ev, true) end
    end
    imgui.EndChild()

    -- Selection status (only when an event is currently chosen).
    -- Sits between the Queued and Active lists since selection can come from either.
    if state.linkedEventId then
        local selShowIds = (callbacks.event_defaults
                            and callbacks.event_defaults.showEventIds == true)
        local selLabel
        if selShowIds then
            selLabel = string.format('Selected: %s (id %d)',
                state.linkedEventName or '?', state.linkedEventId)
        else
            selLabel = 'Selected: ' .. (state.linkedEventName or '?')
        end
        imgui.TextColored({ 0.6, 1.0, 0.6, 1.0 }, selLabel)
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
    imgui.BeginChild('syncActive', { 0, 140 }, true)
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

return M
