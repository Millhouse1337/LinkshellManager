-- ui/launcher_action_bar.lua
-- Action bar below the Break Room: Start & Post / End Event / Post Window.
-- Extracted from ui.lua byte-for-byte.
local imgui     = require('imgui')
local constants = require('constants')

local M = {}

function M.draw(state, callbacks)
    -- Action bar: lives below the Break Room so the End Event /
    -- Start & Post / Post Window controls are anchored at the bottom
    -- of the Attendance area, just above the Loot Pool separator.
    -- Non-HNM events get a single right-aligned button (Start & Post
    -- → End Event once live). HNM events get a two-row bar (Post per
    -- window + End Event once at least one window has been posted).
    -- Recompute isHnmEvent here because the outer do-block that scoped
    -- the original local has already ended above.
    local actionIsHnmEvent = (state.windowMax or 1) > 1
    if state.linkedEventId and not actionIsHnmEvent then
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

    if actionIsHnmEvent and state.linkedEventId then
        local POST_BTN_W   = 220
        local END_BTN_W    = 120
        local barWindowWidth = 600
        pcall(function()
            local ww = imgui.GetWindowWidth()
            if type(ww) == 'number' then barWindowWidth = ww end
        end)

        local windowMax     = state.windowMax or 1
        local windowSeq     = state.windowSequence or 0

        if windowSeq < windowMax then
            local nextSeq = windowSeq + 1
            local prefix = (nextSeq == 1) and 'Start & Post' or 'Post'
            local label = string.format('%s: %s (%d/%d)##postWindow',
                prefix,
                constants.window_label(state.linkedEventName, nextSeq, windowMax),
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

return M
