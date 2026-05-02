-- ui/launcher_break_room.lua
-- Break Room collapsible section. Extracted from ui.lua byte-for-byte.
local imgui = require('imgui')
local api   = require('api')
local constants = require('constants')

local M = {}

function M.draw(state, callbacks)
    -- Break Room: server-side participant list with break/return controls.
    -- Self-actions are always allowed; force-actions and verify/deny are
    -- gated to officers (state.breakRoom.canModerate, sourced from the
    -- /participants response). Polling is handled in att.lua's d3d_present
    -- hook every 10s, plus the launcher's top-row Refresh button forces a
    -- repoll, so this section doesn't need its own Refresh control.
    if state.linkedEventId and state.breakRoom and state.breakRoom.loaded then
        local br = state.breakRoom
        local onBreakCount = 0
        local pendingCount = 0
        for _, p in ipairs(br.participants) do
            if p.isOnBreak then onBreakCount = onBreakCount + 1 end
            if p.pendingReturnLedgerId then pendingCount = pendingCount + 1 end
        end

        -- CollapsingHeader gives the same red-accented preset-style header
        -- the Event Presets section uses. We track the open/closed state
        -- in br.expanded (default false) and force it onto imgui every
        -- frame via SetNextItemOpen — that way the header reliably starts
        -- collapsed each time the launcher opens, regardless of whatever
        -- imgui.ini remembered from a prior session. The autoExpandRequested
        -- one-shot flips br.expanded the first time someone goes on break
        -- or has a pending return; manual clicks update it via the
        -- CollapsingHeader return value.
        if br.autoExpandRequested then
            br.expanded = true
            br.autoExpandRequested = false
        end
        pcall(imgui.SetNextItemOpen, br.expanded and true or false)
        local header = string.format('Break Room (%d on break, %d pending)##brHeader',
            onBreakCount, pendingCount)
        local headerOpen = imgui.CollapsingHeader(header)
        br.expanded = headerOpen and true or false

        if headerOpen then
            imgui.BeginChild('breakRoom', { 0, 110 }, true)
            -- Only members currently on break belong here. Active members
            -- and "return pending" rows render in the Attendance roster
            -- above with their action buttons inline. The Break Room is
            -- strictly the people who are AFK right now.
            local onBreakList = {}
            for _, p in ipairs(br.participants) do
                if p.isOnBreak then onBreakList[#onBreakList + 1] = p end
            end
            if #onBreakList == 0 then
                imgui.TextDisabled('No one is on break.')
            else
                for _, p in ipairs(onBreakList) do
                    local name = p.characterName or '?'
                    local jobs = string.format('%s/%s', p.jobName or '?', p.subJobName or '?')
                    local since = ''
                    if type(p.pauseTime) == 'string' and p.pauseTime ~= '' then
                        local t = constants.parse_iso_utc_to_epoch(p.pauseTime)
                        if t then
                            local mins = math.max(0, math.floor((os.time() - t) / 60))
                            since = string.format(' (%dm)', mins)
                        end
                    end
                    imgui.TextColored({ 1.0, 0.85, 0.2, 1.0 },
                        string.format('%s (%s) - On break%s', name, jobs, since))

                    -- Self -> Return; officers -> Force resume on anyone.
                    if p.isSelf then
                        imgui.SameLine()
                        if imgui.SmallButton('Return##brSelfRet_' .. tostring(p.id)) then
                            local _, err = api.return_from_break(state.linkedEventId, p.id)
                            if err then
                                state.lastSyncSummary = 'Return failed: ' .. tostring(err)
                            else
                                state.lastSyncSummary = 'Returned from break.'
                                br.lastFetchAt = 0
                            end
                        end
                    elseif br.canModerate then
                        imgui.SameLine()
                        if imgui.SmallButton('Force resume##brFR_' .. tostring(p.id)) then
                            local _, err = api.return_from_break(state.linkedEventId, p.id)
                            if err then
                                state.lastSyncSummary = 'Force resume failed: ' .. tostring(err)
                            else
                                state.lastSyncSummary = 'Resumed ' .. name .. '.'
                                br.lastFetchAt = 0
                            end
                        end
                    end
                end
            end
            imgui.EndChild()
        end
    end
end

return M
