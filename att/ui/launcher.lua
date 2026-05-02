-- ui/launcher.lua
-- Orchestrator for the main "Att" launcher window. Owns the window
-- begin/end, the two-column layout, and the footer; all section content
-- lives in dedicated submodules so each file stays under 700 lines.
--
-- Ordering and side-effects are preserved exactly from the original
-- monolithic ui.draw_launcher.
local imgui = require('imgui')
local api   = require('api')

local header        = require('ui.launcher_header')
local create_event  = require('ui.launcher_create_event')
local event_lists   = require('ui.launcher_event_lists')
local attendance_p  = require('ui.launcher_attendance')
local break_room    = require('ui.launcher_break_room')
local action_bar    = require('ui.launcher_action_bar')
local loot_pool     = require('ui.launcher_loot_pool')
local tod_capture   = require('ui.launcher_tod_capture')

local M = {}

-- Persistent input pointers + dropdown options that previously lived as
-- file-level locals in ui.lua. Kept module-local so they survive across
-- frames (and across calls into the various sub-sections) the same way
-- they did before the split.
local syncNewEventNamePtr = { '' }
local syncStyleChosen     = { false }

-- Event Type dropdown options for the Create New Event form. Mirrors the
-- web app's Event Type list verbatim. Empty default forces the user to
-- pick one explicitly (Create Event button gated on a non-empty value).
local EVENT_TYPE_OPTIONS = { 'Sky', 'Sea', 'HNM', 'HENM', 'Limbus', 'Dynamis', 'BCNM', 'KSNM', 'Other' }
local syncNewEventType   = { '' }

-- Bundle for create_event so it can read/write the same persistent ptrs.
local createEventCtx = {
    syncNewEventNamePtr = syncNewEventNamePtr,
    syncStyleChosen     = syncStyleChosen,
    syncNewEventType    = syncNewEventType,
    EVENT_TYPE_OPTIONS  = EVENT_TYPE_OPTIONS,
}

function M.draw(is_open, state, callbacks)
    if not is_open then return false end

    -- ImGuiCond_FirstUseEver is overridden by imgui.ini's saved per-window
    -- state, so a prior manual resize would persist across opens. The
    -- launcherSizePending flag is set by the /attend command handler each
    -- time the launcher transitions to open; here we consume it once with
    -- ImGuiCond_Always to snap the window back to our preferred size. The
    -- user can still drag-resize during the session — the flag is only
    -- set on open, not every frame.
    if state.launcherSizePending then
        -- Also snap position back on a forced re-open so a launcher that
        -- got dragged off-screen on a previous session can't render
        -- invisibly the next time /attend is typed.
        imgui.SetNextWindowPos({ 80, 80 }, ImGuiCond_Always)
        imgui.SetNextWindowSize({ 1240, 640 }, ImGuiCond_Always)
        state.launcherSizePending = false
    else
        imgui.SetNextWindowPos({ 80, 80 }, ImGuiCond_FirstUseEver)
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
            -- 4. Force the next d3d_present tick to re-poll Break Room data
            --    (zeroing the timestamp short-circuits the 10s throttle).
            if state.breakRoom then
                state.breakRoom.lastFetchAt = 0
            end
        end

        -- Header row (Web Sync indicator, LS dropdown, Refresh, Settings + TZ).
        header.draw(state, callbacks, do_full_refresh)

        -- Two-column layout: left column carries the Web Sync controls
        -- (Event Presets, Queued / Active Events, Action buttons); right
        -- column carries the live work surfaces (Attendance, Loot Pool,
        -- ToD Capturing). The footer (CSV Export + Close) sits below both
        -- columns at full width. The negative bottom heights reserve
        -- ~50px at the launcher's bottom for the footer.
        local LEFT_COL_WIDTH = 470
        local COLS_BOTTOM    = -50
        imgui.BeginChild('lhsCol', { LEFT_COL_WIDTH, COLS_BOTTOM }, false)

        -- Web Sync (LSManager) — only visible when the addon is paired
        -- with a web account. Order: Create Event toggle/form, Event
        -- Presets, Queued list, Selection bar, Active list.
        if api.is_paired() then
            create_event.draw(state, callbacks, createEventCtx)
            event_lists.draw(state, callbacks)
        end

        imgui.EndChild()  -- /lhsCol
        imgui.SameLine()
        imgui.BeginChild('rhsCol', { 0, COLS_BOTTOM }, false)

        attendance_p.draw(state, callbacks)
        break_room.draw(state, callbacks)
        action_bar.draw(state, callbacks)

        imgui.Separator()

        loot_pool.draw(state, callbacks)

        imgui.Separator()

        tod_capture.draw(state, callbacks)

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

return M
