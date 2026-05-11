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

-- Preferred launcher dimensions. Compact mode is sized to fit a column of
-- three CollapsingHeader chevrons with the footer below; the user can drag
-- to expand if they pop a section open and want more room.
local LAUNCHER_FULL_W    = 1240
local LAUNCHER_FULL_H    = 640
local LAUNCHER_COMPACT_W = 420
local LAUNCHER_COMPACT_H = 260

-- Tracks the Compact toggle across frames so we can snap the window size
-- when the user flips it. Stored at module scope (not on `state`) because
-- it's pure UI bookkeeping.
local lastCompactState = nil

function M.draw(is_open, state, callbacks)
    if not is_open then return false end

    -- Flip in Compact ↔ Full triggers the same size-pending path the
    -- /attend command uses, so the window resizes immediately instead of
    -- staying at whatever the previous mode used.
    if lastCompactState == nil then
        lastCompactState = state.launcherCompact and true or false
    elseif lastCompactState ~= (state.launcherCompact and true or false) then
        lastCompactState = state.launcherCompact and true or false
        state.launcherSizePending = true
    end

    local prefW = state.launcherCompact and LAUNCHER_COMPACT_W or LAUNCHER_FULL_W
    local prefH = state.launcherCompact and LAUNCHER_COMPACT_H or LAUNCHER_FULL_H

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
        imgui.SetNextWindowSize({ prefW, prefH }, ImGuiCond_Always)
        state.launcherSizePending = false
    else
        imgui.SetNextWindowPos({ 80, 80 }, ImGuiCond_FirstUseEver)
        imgui.SetNextWindowSize({ prefW, prefH }, ImGuiCond_FirstUseEver)
    end
    -- Compact mode renders as a translucent overlay — barely visible window
    -- panel + faded red CollapsingHeader rows so the launcher reads as a
    -- soft HUD that doesn't fight the game scene behind it. Colors mimic
    -- the addon's default red theme but at low alpha; the user can still
    -- see and click everything. SetNextWindowBgAlpha is not exposed by the
    -- Ashita imgui binding, so transparency is driven via the style-color
    -- stack (same approach composition_window uses). Must be balanced with
    -- a matching PopStyleColor(9) after End.
    -- Opacity style-color overrides driven by sliders in the Settings
    -- window. Two distinct push paths so each mode can fade the elements
    -- that make sense for it: compact fades everything (panel reads as a
    -- translucent HUD), full mode only fades the window/title bg so the
    -- widgets stay solid. Both gated on alpha < 1 to keep Ashita's theme
    -- untouched at the default value.
    local compactStylePushed = false
    local mainStylePushed    = false
    if state.launcherCompact and (state.launcherCompactAlpha or 1.0) < 1.0 then
        local mult = state.launcherCompactAlpha or 1.0
        if mult < 0 then mult = 0 end
        if mult > 1 then mult = 1 end
        -- Recipe normalized so mult = 1.0 would be fully opaque, but we
        -- only enter this branch when the user has dialed below 1.0 — at
        -- exactly 1.0 we skip the push entirely so Ashita's default theme
        -- shows through and the compact window matches the main / settings
        -- windows visually (same grey body, same red headers).
        imgui.PushStyleColor(ImGuiCol_WindowBg,          { 0.06, 0.06, 0.06, 0.94 * mult })
        imgui.PushStyleColor(ImGuiCol_ChildBg,           { 0.06, 0.06, 0.06, 0.30 * mult })
        -- Title bar (red theme).
        imgui.PushStyleColor(ImGuiCol_TitleBg,           { 0.55, 0.18, 0.18, 1.00 * mult })
        imgui.PushStyleColor(ImGuiCol_TitleBgActive,     { 0.65, 0.22, 0.22, 1.00 * mult })
        imgui.PushStyleColor(ImGuiCol_TitleBgCollapsed,  { 0.55, 0.18, 0.18, 0.75 * mult })
        imgui.PushStyleColor(ImGuiCol_Border,            { 0.40, 0.40, 0.40, 0.50 * mult })
        -- CollapsingHeader rows (the red drop-downs). At mult=1 they
        -- read as solid red; the slider fades them in lock-step with
        -- the rest of the panel.
        imgui.PushStyleColor(ImGuiCol_Header,            { 0.65, 0.22, 0.22, 1.00 * mult })
        imgui.PushStyleColor(ImGuiCol_HeaderHovered,     { 0.75, 0.28, 0.28, 1.00 * mult })
        imgui.PushStyleColor(ImGuiCol_HeaderActive,      { 0.80, 0.30, 0.30, 1.00 * mult })
        -- Buttons (Refresh in the header + buttons inside expanded
        -- sections).
        imgui.PushStyleColor(ImGuiCol_Button,            { 0.65, 0.22, 0.22, 1.00 * mult })
        imgui.PushStyleColor(ImGuiCol_ButtonHovered,     { 0.75, 0.28, 0.28, 1.00 * mult })
        imgui.PushStyleColor(ImGuiCol_ButtonActive,      { 0.80, 0.30, 0.30, 1.00 * mult })
        compactStylePushed = true
    elseif (state.launcherMainAlpha or 1.0) < 1.0 then
        -- Main (full) launcher fade: only the window panel + title bar
        -- backgrounds dim. Widgets, headers, buttons stay at their
        -- Ashita-theme defaults so they remain clearly readable. Push
        -- only happens when the slider is moved below 1.0, leaving the
        -- default theme completely untouched at full opacity.
        local mult = state.launcherMainAlpha or 1.0
        if mult < 0 then mult = 0 end
        imgui.PushStyleColor(ImGuiCol_WindowBg,          { 0.06, 0.06, 0.06, 0.94 * mult })
        imgui.PushStyleColor(ImGuiCol_ChildBg,           { 0.06, 0.06, 0.06, 0.30 * mult })
        imgui.PushStyleColor(ImGuiCol_TitleBg,           { 0.55, 0.18, 0.18, 1.00 * mult })
        imgui.PushStyleColor(ImGuiCol_TitleBgActive,     { 0.65, 0.22, 0.22, 1.00 * mult })
        imgui.PushStyleColor(ImGuiCol_TitleBgCollapsed,  { 0.55, 0.18, 0.18, 0.75 * mult })
        imgui.PushStyleColor(ImGuiCol_Border,            { 0.40, 0.40, 0.40, 0.50 * mult })
        mainStylePushed = true
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

        -- Header row (Web Sync indicator, LS dropdown, Compact toggle,
        -- Refresh, Settings + TZ).
        header.draw(state, callbacks, do_full_refresh)

        -- Bottom reserve: full mode keeps ~50px for the CSV / Close footer;
        -- compact mode drops the footer entirely (close via the window's
        -- title-bar [x]) so we use the full available height for sections.
        local COLS_BOTTOM    = state.launcherCompact and 0 or -50

        if state.launcherCompact then
            -- Compact view: hide the left column entirely (Create Event,
            -- Event Presets, Queued / Active Events) along with the right
            -- column's Break Room and Action Bar — only Attendance, Loot
            -- Pool, and ToD Capturing remain. Each section sits behind a
            -- CollapsingHeader so the panel can shrink down to just the
            -- three chevron rows; the user expands a section on demand.
            -- CollapsingHeader defaults to closed (no DefaultOpen flag),
            -- which is the requested "compressed by default" behaviour.
            imgui.BeginChild('compactCol', { 0, COLS_BOTTOM }, false)
            if imgui.CollapsingHeader('Attendance##compactAttHeader') then
                attendance_p.draw(state, callbacks)
            end
            if imgui.CollapsingHeader('Loot Pool##compactLootHeader') then
                loot_pool.draw(state, callbacks)
            end
            if imgui.CollapsingHeader('ToD Capturing##compactTodHeader') then
                tod_capture.draw(state, callbacks)
            end
            imgui.EndChild()  -- /compactCol
        else
            -- Two-column layout: left column carries the Web Sync controls
            -- (Event Presets, Queued / Active Events, Action buttons); right
            -- column carries the live work surfaces (Attendance, Loot Pool,
            -- ToD Capturing). The footer (CSV Export + Close) sits below both
            -- columns at full width. The negative bottom heights reserve
            -- ~50px at the launcher's bottom for the footer.
            local LEFT_COL_WIDTH = 470
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
        end

        -- Footer (CSV Export + Close) is omitted in compact mode so the
        -- panel stays as tight as possible. The user closes the launcher
        -- via the window title-bar [x] (driven by openPtr) when compact.
        if not state.launcherCompact then
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
        end

        imgui.End()
    end
    -- Balance the PushStyleColor calls made for the opacity overrides.
    -- Compact mode pushes 12 (window/title/header/button); full mode
    -- pushes 6 (window/title/border only). Must run regardless of Begin's
    -- return so the style stack stays sane when the window is collapsed.
    if compactStylePushed then
        imgui.PopStyleColor(12)
    elseif mainStylePushed then
        imgui.PopStyleColor(6)
    end
    return openPtr[1]
end

return M
