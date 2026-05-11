-- ui/launcher_attendance.lua
-- Right-column attendance area of draw_launcher: header (event name + count
-- + scanning timer + event timer), per-window tab selector for HNM events,
-- live and frozen roster renderers. Extracted from ui.lua byte-for-byte.
local imgui      = require('imgui')
local api        = require('api')
local constants  = require('constants')
local attendance = require('attendance')
local common     = require('ui.common')

local M = {}

local SELF_COLOR    = common.SELF_COLOR
local get_self_name = common.get_self_name
local is_self_row   = common.is_self_row

function M.draw(state, callbacks)
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

        -- Event Timer: counts up from CommencementStartTime for any
        -- live single-window event (Timed / Sky / Sea / Dynamis / Limbus
        -- / etc). Multi-window HNMs use the per-window timestamps in
        -- their tab strip instead, so we skip the global timer for them.
        -- Pulled from the cached webEvents list; updates each frame.
        do
            if state.linkedEventId and state.webEvents then
                local linkedEv
                for _, ev in ipairs(state.webEvents) do
                    if ev.id == state.linkedEventId then linkedEv = ev; break end
                end
                local isSingleWindow = (tonumber(linkedEv and linkedEv.windowCount) or 1) <= 1
                if linkedEv and linkedEv.commencementStartTime
                   and linkedEv.commencementStartTime ~= ''
                   and isSingleWindow then
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

        -- Scope filter (HNM Style / Claim-Kill only). Radio row narrows
        -- which entities the next zone-scan counts as attendees:
        --   Party    → just the player's party (slots 0-5)
        --   Alliance → the full alliance (slots 0-17)
        --   Zone     → everyone in the credit zone (historical behavior)
        -- The selection is mirrored into attendance.set_scope every frame
        -- so the gather functions read a fresh value without needing to
        -- know about state. Non-HNM events hide the picker entirely and
        -- the scope is forced back to 'zone' so single-window scans never
        -- silently narrow their roster based on a leftover HNM choice.
        if isHnmEvent then
            state.attendanceScopeFilter = state.attendanceScopeFilter or 'zone'
            -- Keep the scope picker on the same row as "Attendance for: ..."
            -- so the header stays a single visual strip instead of stacking.
            imgui.SameLine()
            imgui.TextDisabled('  Scope:')
            imgui.SameLine()
            if imgui.RadioButton('Party##attScopeParty',
                    state.attendanceScopeFilter == 'party') then
                state.attendanceScopeFilter = 'party'
            end
            imgui.SameLine()
            if imgui.RadioButton('Alliance##attScopeAlliance',
                    state.attendanceScopeFilter == 'alliance') then
                state.attendanceScopeFilter = 'alliance'
            end
            imgui.SameLine()
            if imgui.RadioButton('Zone##attScopeZone',
                    state.attendanceScopeFilter == 'zone') then
                state.attendanceScopeFilter = 'zone'
            end
            attendance.set_scope(state.attendanceScopeFilter)
        else
            attendance.set_scope('zone')
        end
        -- Negative BeginChild height = fill to within N pixels of the
        -- launcher's bottom. Reserve room for: Loot Pool panel
        -- (~150px: separator + header + scrollable content child +
        -- separator), ToD panel (~116px), plus the original footer
        -- (40px non-HNM / 64px HNM for "Post New Window" + window-
        -- status text on top of CSV/Close).
        -- Roster height is fixed (positive) so adding/removing the Break
        -- Room section below doesn't squish the Attendance roster. Whatever
        -- comes after just flows downward inside the right column. Pick a
        -- value that comfortably fits ~6 rows; the BeginChild lets it
        -- scroll internally if the roster grows past that.
        local rosterHeight = isHnmEvent and 200 or 180

        local function render_live_roster()
            -- Warm the roster cache on first render so the per-row Alts subtitle
            -- can populate. Subsequent frames skip the call — the TTL inside
            -- on_load_roster would no-op anyway, but render callbacks should
            -- stay read-only after warmup.
            if state.rosterCache == nil and callbacks.on_load_roster then
                callbacks.on_load_roster(false)
            end

            local selfKey = (get_self_name() or ''):lower()
            if selfKey == '' then selfKey = nil end

            -- Look up the linked event once for shared math: rate +
            -- commencement epoch + non-HNM gate. Per-row time / DKP
            -- below uses each participant's accumulatedHours plus the
            -- live segment so the displayed numbers diverge correctly
            -- when individuals take breaks.
            local linkedEv, eventRate, eventCommencedEpoch
            local showPerRowMeta = false
            local windowMaxLive = state.windowMax or 1
            local isWindowedLive = windowMaxLive > 1
            if state.linkedEventId and state.webEvents
               and not isWindowedLive then
                for _, ev in ipairs(state.webEvents) do
                    if ev.id == state.linkedEventId then linkedEv = ev; break end
                end
                if linkedEv and linkedEv.commencementStartTime
                   and linkedEv.commencementStartTime ~= '' then
                    eventCommencedEpoch = constants.parse_iso_utc_to_epoch(
                        tostring(linkedEv.commencementStartTime))
                    eventRate = tonumber(linkedEv.dkpPerHour)
                    showPerRowMeta = (eventCommencedEpoch ~= nil)
                end
            end

            -- Loot Council linkshells don't track DKP at all, so the
            -- per-row DKP suffix is suppressed entirely. The accumulated
            -- timer still renders since duration is meaningful regardless
            -- of whether DKP gets awarded for it.
            local lootStructForRoster = (state.rosterCache and state.rosterCache.lootStructure) or 'Dkp'
            local showRowDkp = (lootStructForRoster ~= 'LootCouncil')

            -- Falls back to the global event timer for rows that aren't
            -- (yet) committed as server participants — no individual
            -- accumulated time exists for them, so they share the global.
            local globalDkpSuffix
            if showPerRowMeta and showRowDkp and eventRate and eventRate > 0 then
                local hours = math.max(0, (os.time() - eventCommencedEpoch) / 3600)
                local rounded = math.floor(hours * 4 + 0.5) / 4
                globalDkpSuffix = string.format(' [%g DKP]', rounded * eventRate)
            end

            -- Windowed events (HNM Style / Claim/Kill / NMs) award a flat
            -- per-window rate at post time, not duration × rate. Surface that
            -- on the live roster too so members see the credit they're about
            -- to earn before the post button is clicked. dkpPerHour is reused
            -- as DkpPerWindow for windowed events (server keeps both names
            -- straight; the addon's cached row only carries dkpPerHour).
            local windowedDkpSuffix
            if isWindowedLive and showRowDkp and state.linkedEventId
               and state.webEvents then
                for _, ev in ipairs(state.webEvents) do
                    if ev.id == state.linkedEventId then
                        local rate = tonumber(ev.dkpPerHour)
                        if rate and rate > 0 then
                            windowedDkpSuffix = string.format(' [+%g DKP]', rate)
                        end
                        break
                    end
                end
            end

            -- Build a name -> server participant index so we can fold
            -- break/return state into each roster row. Names are matched
            -- case-insensitively after stripping the local "X " prefix
            -- the addon uses for opt-out rows.
            local serverByName = {}
            local br = state.breakRoom
            if br and br.loaded then
                for _, p in ipairs(br.participants or {}) do
                    local key = (p.characterName or ''):lower()
                    if key ~= '' then serverByName[key] = p end
                end
            end

            local i = 1
            while i <= #attendance.data do
                local r = attendance.data[i]
                local cleanName = (r.name or ''):gsub('^X%s+', '')
                local key = cleanName:lower()
                local serverP = serverByName[key]

                -- People currently on break disappear from the Attendance
                -- list — they show up in the Break Room instead.
                if serverP and serverP.isOnBreak then
                    i = i + 1
                else
                    local isLocalSelf = is_self_row(r, selfKey)

                    -- Per-row accumulated time + DKP. accumulatedHours from
                    -- the server covers prior segments (pre-break); the
                    -- live segment is from the latest resumeTime (or the
                    -- original startTime, or the event commencement) to
                    -- now. When the row has no matched server participant
                    -- (e.g. a zone-scanned member who hasn't been posted
                    -- yet) we fall back to the global event timer.
                    local timeSuffix = ''
                    local dkpSuffix  = windowedDkpSuffix or globalDkpSuffix or ''
                    if showPerRowMeta and serverP then
                        local accumulated = tonumber(serverP.accumulatedHours) or 0
                        local segmentStartEpoch = nil
                        if type(serverP.resumeTime) == 'string' and serverP.resumeTime ~= '' then
                            segmentStartEpoch = constants.parse_iso_utc_to_epoch(serverP.resumeTime)
                        elseif type(serverP.startTime) == 'string' and serverP.startTime ~= '' then
                            segmentStartEpoch = constants.parse_iso_utc_to_epoch(serverP.startTime)
                        end
                        segmentStartEpoch = segmentStartEpoch or eventCommencedEpoch
                        local segmentHours = 0
                        if segmentStartEpoch then
                            segmentHours = math.max(0, (os.time() - segmentStartEpoch) / 3600)
                        end
                        local liveHours = math.max(0, accumulated + segmentHours)
                        local h = math.floor(liveHours)
                        local m = math.floor((liveHours - h) * 60)
                        local s = math.floor(((liveHours - h) * 60 - m) * 60)
                        timeSuffix = string.format(' [%02d:%02d:%02d]', h, m, s)
                        if showRowDkp and eventRate and eventRate > 0 then
                            local rounded = math.floor(liveHours * 4 + 0.5) / 4
                            dkpSuffix = string.format(' [%g DKP]', rounded * eventRate)
                        end
                    end

                    local line = string.format('%s (%s | %s/%s)%s%s',
                        r.name, r.zone or '?', r.jobsMain or '?', r.jobsSub or '?',
                        timeSuffix, dkpSuffix)
                    if isLocalSelf then
                        imgui.TextColored(SELF_COLOR, line)
                    else
                        imgui.Text(line)
                    end

                    -- Account-linked alts (max 2). Shown as a dim subtitle so
                    -- the linkshell can recognize who's behind the character.
                    -- Actions remain attributed to the main character server-side.
                    local altsByName = state.rosterCache and state.rosterCache.altsByName
                    if altsByName then
                        local memberAlts = altsByName[key]
                        if memberAlts and #memberAlts > 0 then
                            imgui.TextDisabled('    Alts: ' .. table.concat(memberAlts, ', '))
                        end
                    end

                    -- Break/return action buttons live next to the name.
                    -- Self: only the Take break button — Verify/Deny/Remove
                    -- never apply to your own row (you can't moderate yourself
                    -- or remove yourself from the local pre-post filter).
                    -- Officers acting on others: Force break, plus Verify /
                    -- Deny when the server has a pending self-return on file.
                    if serverP then
                        if serverP.isSelf then
                            imgui.SameLine()
                            if imgui.SmallButton('Take break##arBrk_' .. i) then
                                local _, err = api.take_break(state.linkedEventId, serverP.id)
                                if err then
                                    state.lastSyncSummary = 'Break failed: ' .. tostring(err)
                                else
                                    state.lastSyncSummary = 'On break.'
                                    state.breakRoom.lastFetchAt = 0
                                end
                            end
                        elseif br.canModerate and not isLocalSelf then
                            imgui.SameLine()
                            if imgui.SmallButton('Force break##arFB_' .. i) then
                                local _, err = api.take_break(state.linkedEventId, serverP.id)
                                if err then
                                    state.lastSyncSummary = 'Force break failed: ' .. tostring(err)
                                else
                                    state.lastSyncSummary = 'Sent ' .. cleanName .. ' to break.'
                                    state.breakRoom.lastFetchAt = 0
                                end
                            end
                        end

                        if br.canModerate and not isLocalSelf and serverP.pendingReturnLedgerId then
                            imgui.SameLine()
                            if imgui.SmallButton('Verify##arV_' .. i) then
                                local _, err = api.verify_return(state.linkedEventId, serverP.pendingReturnLedgerId)
                                if err then
                                    state.lastSyncSummary = 'Verify failed: ' .. tostring(err)
                                else
                                    state.lastSyncSummary = "Verified " .. cleanName .. "'s return."
                                    state.breakRoom.lastFetchAt = 0
                                end
                            end
                            imgui.SameLine()
                            if imgui.SmallButton('Deny##arD_' .. i) then
                                local _, err = api.deny_return(state.linkedEventId, serverP.pendingReturnLedgerId)
                                if err then
                                    state.lastSyncSummary = 'Deny failed: ' .. tostring(err)
                                else
                                    state.lastSyncSummary = "Denied " .. cleanName .. "'s return."
                                    state.breakRoom.lastFetchAt = 0
                                end
                            end
                        end
                    end

                    -- Remove never applies to the local player — you can't
                    -- accidentally drop yourself from the pre-post roster.
                    if isLocalSelf then
                        i = i + 1
                    else
                        imgui.SameLine()
                        if imgui.SmallButton('Remove##arRem' .. i) then
                            table.remove(attendance.data, i)
                        else
                            i = i + 1
                        end
                    end
                end
            end
        end

        -- Per-window DKP rate for the active event. For windowed events the
        -- server reuses the dkpPerHour column as DkpPerWindow (same column,
        -- different semantic), so we read it straight off the cached event
        -- row. Nil/0 → don't append the "+N DKP" suffix to roster rows.
        local function dkp_per_window_for_active_event()
            if not state.linkedEventId or not state.webEvents then return nil end
            for _, ev in ipairs(state.webEvents) do
                if ev.id == state.linkedEventId then
                    local n = tonumber(ev.dkpPerHour)
                    if n and n > 0 then return n end
                    return nil
                end
            end
            return nil
        end

        -- Frozen roster for a posted window. Each row gets a Remove button so
        -- accidental posts can be undone server-side; the local snapshot is
        -- pruned in lock-step so the UI reflects the change immediately.
        local function render_frozen_roster(seq, snapshot)
            if not snapshot or #snapshot == 0 then
                imgui.TextDisabled('No entries posted for this window.')
                return
            end
            local dkpPerWindow = dkp_per_window_for_active_event()
            local i = 1
            while i <= #snapshot do
                local r = snapshot[i]
                local name = r.name or ''
                -- "X "-prefixed entries are local-only ignores; treat the bare name
                -- as the canonical character name when calling the server.
                local cleanName = name:gsub('^X%s+', '')
                local dkpSuffix = dkpPerWindow
                    and string.format('  +%g DKP', dkpPerWindow) or ''
                imgui.Text(string.format('%s (%s | %s/%s)%s',
                    name, r.zone or '?', r.jobsMain or '?', r.jobsSub or '?', dkpSuffix))
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
                local labelText = constants.window_label(state.linkedEventName, seqForLabel, windowMax)
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
                -- Per-window DKP summary: per-attendee rate × posted entries.
                -- Mirrors the per-attendee "+N DKP" suffix below so users see
                -- both the unit rate and the window's total at a glance.
                local snap = state.windowRosters[active]
                local rate = dkp_per_window_for_active_event()
                if rate and snap and #snap > 0 then
                    imgui.TextDisabled(string.format(
                        '%g DKP / attendee × %d = %g DKP awarded this window',
                        rate, #snap, rate * #snap))
                end
                render_frozen_roster(active, snap)
            end
        end
        imgui.EndChild()
    end
end

return M
