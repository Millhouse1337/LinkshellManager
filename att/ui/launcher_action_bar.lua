-- ui/launcher_action_bar.lua
-- Action bar below the Break Room: Start & Post / End Event / Post Window /
-- Join Event. Extracted from ui.lua byte-for-byte (with the Join button as
-- the only non-byte-for-byte addition).
local imgui      = require('imgui')
local api        = require('api')
local constants  = require('constants')
local attendance = require('attendance')

local M = {}

-- Returns true when the breakRoom poll already shows the token issuer
-- attached to the linked event (a "self" participant row exists). Used
-- to gate the Join Event button so it only renders for members who
-- aren't credited yet, and disappears the moment the next poll confirms
-- the join landed server-side.
local function caller_is_attached(state)
    local br = state.breakRoom
    if not br or not br.loaded then return false end
    for _, p in ipairs(br.participants or {}) do
        if p.isSelf then return true end
    end
    return false
end

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
            -- Officer-only End Event (server still rejects non-officers, but
            -- moderators see the canonical action; everyone else gets the
            -- Join button below). canModerate comes from the /participants
            -- response so it's accurate to the token issuer's role.
            local br = state.breakRoom
            local canModerate = br and br.canModerate or false
            local attached = caller_is_attached(state)

            if canModerate then
                if imgui.Button('End Event: ' .. (state.linkedEventName or '?') .. '##syncEndEvent', { SINGLE_BTN_W, 0 }) then
                    if callbacks.on_end_event then
                        callbacks.on_end_event()
                    end
                end
            elseif not attached then
                -- Late-join for regular members: the addon owner isn't on
                -- the participant list yet, so offer a single-click join.
                -- Jobs are read from the party memory manager at click time
                -- so the payload always reflects the player's CURRENT main /
                -- sub, even if attendance.data is stale.
                if imgui.Button('Join Event: ' .. (state.linkedEventName or '?') .. '##syncJoinEvent', { SINGLE_BTN_W, 0 }) then
                    local mj, sj = attendance.get_self_jobs()
                    local result, jerr = api.join_event(state.linkedEventId, mj, sj, nil)
                    if jerr then
                        state.lastSyncSummary = 'Join failed: ' .. tostring(jerr)
                    elseif result then
                        state.lastSyncSummary = string.format(
                            'Joined %s as %s/%s.',
                            state.linkedEventName or '?',
                            mj or '?', sj or '?')
                        -- Force the next d3d_present tick to re-poll the
                        -- participants list so the Join button disappears
                        -- and the per-row timer / break controls show up.
                        if state.breakRoom then state.breakRoom.lastFetchAt = 0 end
                    end
                end
            else
                -- Member who's already attached — show a status line so
                -- they know the join is recorded and the action bar isn't
                -- empty. Disabled style matches the "All N windows posted"
                -- text in the HNM branch below.
                imgui.TextDisabled('Joined ' .. (state.linkedEventName or '?'))
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
        local POST_BTN_W   = 280
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
