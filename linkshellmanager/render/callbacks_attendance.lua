-- render/callbacks_attendance.lua
-- Attendance / scan / preset-button callbacks pulled out of att.lua's
-- d3d_present `callbacks` table. Each function is byte-for-byte the original
-- body, with closure-captured upvalues (api, attendance, resources, chat,
-- constants, etc.) replaced by the deps table threaded through render_pump.
--
-- M.install(out, state, deps) populates fields on the out table so the parent
-- callbacks.lua can compose contributions from sibling modules into the same
-- table that ui.draw_launcher receives.

local M = {}

function M.install(out, state, deps)
    local api        = deps.api
    local attendance = deps.attendance
    local resources  = deps.resources
    local chat       = deps.chat
    local constants  = deps.constants
    local utils      = deps.utils

    out.on_party_only = function()
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
    end

    out.on_write = function(close)
        local _, msg = attendance.write_file(addon.path, state.selectedMode, state.pendingEventName)
        if msg then AshitaCore:GetChatManager():QueueCommand(1, utils.ls_prefix(state) .. msg) end

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
                    print(chat.header('lsm') .. 'Auto-created event: ' .. tostring(created.name)
                        .. ' (id ' .. tostring(created.eventId) .. ')')
                else
                    state.lastSyncSummary = 'Auto-create failed: ' .. tostring(cerr)
                    print(chat.header('lsm') .. state.lastSyncSummary)
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
                    print(chat.header('lsm') .. summary)
                else
                    state.lastSyncSummary = 'Web sync failed: ' .. tostring(perr)
                    print(chat.header('lsm') .. state.lastSyncSummary)
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
    end

    out.on_launch_event = function(ev)
         utils.queue_attend_launch(state, deps, ev)
    end

    out.on_update_zone = function() utils.update_suggestions(state, deps) end

    out.on_scan_letter = function(letter)
         -- Strip the launcher's "D<n>" day-suffix so a day-tagged HNM
         -- event still finds its canonical search area / credit zone.
         local lookupName = constants.canonical_event_name(state.pendingEventName)
         local area = resources.attCreditNames[lookupName] and resources.attCreditNames[lookupName][1]
         area = resources.attSearchArea[lookupName] or area
         if area and area ~= '' then
             local lsSearch = (state.g_LSMode == 'ls2') and 'linkshell2' or 'linkshell'
             AshitaCore:GetChatManager():QueueCommand(1, string.format('/sea %s %s %s', area, lsSearch, letter))

             state.pendingSeaScan = { fireAt = os.clock() + (state.attendDelaySec or 2) }
         end
    end

    -- Launcher: refresh the local roster directly from the entity list.
    -- No /sea is fired (so the FFXI Search window doesn't pop), and the
    -- scan happens immediately -- no delay needed.
    out.on_launcher_scan = function(eventName, isAutoCreate)
        attendance.clear()
        if isAutoCreate then
            attendance.gather_current_zone()
        else
            attendance.gather_zone(eventName)
        end
        attendance.add_self()
        -- Clear any pending gather indicator from prior scans.
        state.launcherGather = nil
    end

    -- Event Preset button: clicking "Nidhogg" (or any preset) creates that
    -- event in the web app, makes it the active session event, and refreshes
    -- the local roster from the entity list. Does NOT fire any /sea command.
    -- `category` is the resource bucket the preset came from (HNMS / NMS /
    -- HENMs / Events) and drives the EventType we record server-side.
    out.on_preset_button = function(eventName, category, opts, dayNumber)
        opts = opts or {}
        -- Day-tracked HNMs (Fafnir, Behemoth, Adamantoise) get a "D<n>"
        -- suffix on the server-stored event name. The local eventName
        -- arg stays canonical ("Fafnir/Nidhogg") so credit zone /
        -- search area / window count lookups still resolve; only the
        -- name we POST to the server (and stash in pendingEventName for
        -- the "Attendance for:" header) carries the suffix.
        local dayClean = dayNumber and tostring(dayNumber):gsub('%s', '') or ''
        local serverEventName = eventName
        if dayClean ~= '' then
            serverEventName = eventName .. ' D' .. dayClean
        end
        state.pendingEventName = serverEventName
        state.scanNextLetter = nil
        -- HNM Style (post-by-window attendance) engages for the HNMS
        -- preset category. NMS uses the same multi-post flow but is
        -- pinned to 2 windows ("On Time" / "Claim/Kill") regardless
        -- of what the per-name window-count table says — the resource
        -- list defines the policy, not the curated HnmConfig table.
        -- HENMs / Events keep single-window (regular) attendance.
        -- The Testing category opts in per-monster via TESTING_MONSTERS
        -- so QA presets can exercise both flows from the same UI.
        local testStyle = constants.testing_style_for(eventName) -- 'HNM' / 'Regular' / nil
        local isNmCategory = (category == 'NMS')
        local isHnmCategory = (category == 'HNMS')
            or isNmCategory
            or (category == 'Testing' and testStyle == 'HNM')
        if isNmCategory then
            state.windowMax = 2
        elseif isHnmCategory then
            state.windowMax = constants.window_count_for(eventName)
        else
            state.windowMax = 1
        end
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

        -- 1. Create on the web app if paired. Pass an explicit windowCount
        -- when the local windowMax differs from the server's name-based
        -- default so the server stores WindowCountOverride and accepts
        -- per-window attendance posts beyond window 1.
        local serverWindowCount = (state.windowMax and state.windowMax > 1) and state.windowMax or nil
        if not api.is_paired() then
            -- Surface the no-pairing case in the launcher toast too so the
            -- user isn't left wondering why nothing showed up in Queued
            -- Events after a preset click.
            state.lastSyncSummary = 'Not paired with web. Use /lsm link <code>.'
            print(chat.header('lsm') .. state.lastSyncSummary)
        else
            local created, err = api.create_event(serverEventName, resolvedType, nil, dkpRate, serverWindowCount)
            if created and created.eventId then
                state.linkedEventId = created.eventId
                state.linkedEventName = created.name or eventName
                state.lastSyncSummary = string.format('Queued: %s (id %d)',
                    created.name or eventName, created.eventId)
                print(chat.header('lsm') .. 'Created event: ' .. (created.name or eventName)
                    .. ' (id ' .. tostring(created.eventId) .. ')')
                -- Refresh the events list so the new event appears in Queued Events.
                local events, listErr = api.list_events()
                if events then
                    state.webEvents = events
                else
                    -- The create succeeded but the follow-up list call didn't.
                    -- Keep the "Queued: ..." toast (the event IS on the server)
                    -- but also surface the refresh failure so the user knows
                    -- to hit Refresh manually.
                    state.lastSyncSummary = state.lastSyncSummary
                        .. ' | Refresh failed: ' .. tostring(listErr or 'unknown')
                    print(chat.header('lsm') .. 'List events failed after create: '
                        .. tostring(listErr or 'unknown'))
                end
            else
                state.lastSyncSummary = 'Create failed: ' .. tostring(err)
                print(chat.header('lsm') .. state.lastSyncSummary)
            end
        end

        -- 2. Refresh roster directly from the entity list (no /sea).
        attendance.clear()
        attendance.gather_zone(eventName)
        attendance.add_self()
    end
end

return M
