-- render/callbacks_event.lua
-- Event-lifecycle callbacks: on_start_and_post, on_end_event. Bodies are
-- byte-for-byte from the original att.lua callbacks table.

local M = {}

function M.install(out, state, deps)
    local api        = deps.api
    local attendance = deps.attendance
    local chat       = deps.chat
    local constants  = deps.constants
    local messages   = deps.messages
    local utils      = deps.utils

    -- Launcher: combined Start/Create+Start + post attendance + LS msgs + optional CSV.
    -- opts: { eventId, eventName, isAutoCreate, csvOnStart }
    -- Returns a one-line summary string for state.lastSyncSummary.
    out.on_start_and_post = function(opts)
        if not api.is_paired() then
            return 'Not paired with web. Use /att link <code>.'
        end

        local eventId = opts.eventId
        local eventName = opts.eventName

        -- 1. Make sure the event exists, then start it.
        if not eventId then
            -- HNM and Claim/Kill events both take per-window DKP and use
            -- a multi-post pattern; everything else takes per-hour. The
            -- Claim/Kill style is sent as type='HNM' with an explicit
            -- windowCount=2 so the server-side filter still treats it
            -- like a multi-post HNM. HNM Style sends windowCount=24 so
            -- user-named long-pop events get the full 24-slot setup
            -- without depending on the server's curated name list.
            local mode = state.selectedMode
            local isMultiPost = (mode == 'HNM') or (mode == 'ClaimKill')
            local dkpRate = isMultiPost and opts.dkpPerWindow or opts.dkpPerHour
            local typeForServer = (mode == 'ClaimKill') and 'HNM' or mode
            local windowCount = nil
            if mode == 'HNM' then windowCount = 24
            elseif mode == 'ClaimKill' then windowCount = 2 end
            local created, cerr = api.create_event(eventName, typeForServer, nil, dkpRate, windowCount)
            if not created or not created.eventId then
                return 'Create failed: ' .. tostring(cerr)
            end
            eventId = created.eventId
            eventName = created.name or eventName
            state.linkedEventId = eventId
            state.linkedEventName = eventName
        end

        local r, serr = api.start_event(eventId)
        if not r then return 'Start failed: ' .. tostring(serr) end
        local startedFresh = not r.alreadyStarted
        eventName = r.name or eventName

        -- 2. Build entries list from current roster (skip pending X-prefixed).
        local entries = {}
        for _, row in ipairs(attendance.data) do
            if not row.name:match('^X ') then
                entries[#entries + 1] = {
                    characterName = row.name,
                    mainJob = row.jobsMain,
                    subJob = row.jobsSub,
                    zone = row.zone
                }
            end
        end

        -- 3. Determine windowSequence (nil for non-HNM single-window events).
        -- For HNMs the addon increments per Post; the server pins the batch to
        -- that sequence number so it lands on its own attendance tab.
        local windowMax = state.windowMax or 1
        local nextSequence = nil
        if windowMax > 1 then
            nextSequence = (state.windowSequence or 0) + 1
            if nextSequence > windowMax then
                return string.format('All %d windows already posted for %s.', windowMax, eventName)
            end
        end

        -- 4. Post attendance (if there's anyone to post).
        local syncSummary = 'No roster entries.'
        if #entries > 0 then
            local result, perr = api.post_attendance(eventId, entries, nextSequence)
            if result then
                local unmatched = result.unmatched or {}
                local windowTag = nextSequence and (' [window ' .. nextSequence .. ']') or ''
                syncSummary = string.format('Synced %d / Reported %d - %d unmatched%s',
                    result.matched or 0, #entries, #unmatched, windowTag)
                if #unmatched > 0 then
                    local sample = {}
                    for i = 1, math.min(5, #unmatched) do sample[i] = unmatched[i] end
                    syncSummary = syncSummary .. ': ' .. table.concat(sample, ', ')
                    if #unmatched > 5 then syncSummary = syncSummary .. ', ...' end
                end

                -- For windowed events: print a per-attendee DKP summary so
                -- members get the same "you earned X DKP" feedback that timed
                -- events get at end-of-event. Server returns creditedAttendees
                -- (only the ones newly credited for THIS window — already-
                -- credited re-posts are excluded) plus the per-window rate.
                local credited = (type(result.creditedAttendees) == 'table')
                    and result.creditedAttendees or {}
                local rate = tonumber(result.dkpPerWindow)
                if nextSequence and rate and #credited > 0 then
                    local hdr = chat.header('att')
                    print(hdr .. string.format('Window %d posted - %g DKP awarded to %d:',
                        nextSequence, rate, #credited))
                    for _, c in ipairs(credited) do
                        local function asStr(v)
                            if type(v) == 'string' and v ~= '' then return v end
                            return nil
                        end
                        print(hdr .. string.format('  %s (%s/%s) +%g DKP',
                            asStr(c.characterName) or '?',
                            asStr(c.jobName) or '?',
                            asStr(c.subJobName) or '?',
                            tonumber(c.dkpEarned) or rate))
                    end
                end

                -- For HNMs: snapshot the just-posted roster into windowRosters,
                -- bump the sequence, and clear the live roster so the next /sea
                -- builds the next window from scratch.
                if nextSequence then
                    local snapshot = {}
                    for i, row in ipairs(attendance.data) do snapshot[i] = row end
                    state.windowRosters[nextSequence] = snapshot
                    state.windowSequence = nextSequence
                    -- Mirror the new state into the per-event map so re-selecting
                    -- this event (after navigating away) still shows the posted
                    -- windows + when each one was posted.
                    state.windowStateByEvent = state.windowStateByEvent or {}
                    local entry = state.windowStateByEvent[state.linkedEventId]
                    if not entry then
                        entry = {
                            max      = state.windowMax,
                            sequence = nextSequence,
                            rosters  = state.windowRosters,
                            postedAt = {},
                        }
                        state.windowStateByEvent[state.linkedEventId] = entry
                    else
                        entry.max      = state.windowMax
                        entry.sequence = nextSequence
                        entry.rosters  = state.windowRosters
                        entry.postedAt = entry.postedAt or {}
                    end
                    entry.postedAt[nextSequence] = constants.format_posted_at(os.date('*t'))
                    attendance.clear()
                    attendance.add_self()
                end
            else
                syncSummary = 'Sync failed: ' .. tostring(perr)
            end
        end

        -- 5. Optional CSV. Surface any write_file error inline so a missing
        -- "HNM Logs\" / "Event Logs\" folder or other I/O failure isn't
        -- swallowed (the other write_file call sites already log; this one
        -- used to silently drop the result).
        if opts.csvOnStart and #entries > 0 then
            local count, csvMsg = attendance.write_file(addon.path, state.selectedMode, eventName)
            if not count then
                local hdr = chat.header('att')
                print(hdr .. 'CSV export failed: ' .. tostring(csvMsg or 'unknown error'))
                state.lastSyncSummary = (state.lastSyncSummary or '')
                    .. ' | CSV export failed.'
            end
        end

        -- 6. Refresh the cached events list so the launcher's Queued/Active
        -- panels reflect the new live state without requiring a manual Refresh.
        -- Best-effort; if the call fails the user can still hit Refresh.
        local refreshed = api.list_events()
        if refreshed then state.webEvents = refreshed end

        -- 7. LS chat announcements (broadcast to every selected linkshell).
        -- Stagger the second message: FFXI rejects rapid back-to-back /l
        -- commands ("A command error occurred"), so when both messages
        -- need to fire we send "Event started" now and defer the
        -- attendance line through pendingLSMessage. d3d_present picks it
        -- up after the delay and broadcasts then.
        if startedFresh then
            utils.broadcast_to_selected_ls(state, string.format(messages.EVENT_STARTED, eventName))
        end
        if #entries > 0 then
            local takenTpl = (state.selectedMode == 'HNM') and messages.HNM_TAKEN or messages.EVENT_TAKEN
            local takenMsg = string.format(takenTpl, eventName)
            if startedFresh then
                local delay = tonumber(state.attendDelaySec) or 2
                if delay < 1 then delay = 1 end
                state.pendingLSMessage = {
                    message = takenMsg,
                    fireAt  = os.clock() + delay,
                }
            else
                utils.broadcast_to_selected_ls(state, takenMsg)
            end
        end

        return (startedFresh and 'Started: ' or 'Already live: ') .. eventName .. '. ' .. syncSummary
    end

    -- Ends a running event. Mirrors the web app's End flow: server
    -- writes EventHistory + DkpLedgerEntry rows and removes the live
    -- Event / Jobs / participants / loot. After a successful end we
    -- clear the local linked-event/window state and refresh the
    -- cached events list so the launcher's Active Events drops it.
    out.on_end_event = function()
        if not state.linkedEventId then return end
        if not api.is_paired() then
            state.lastSyncSummary = 'Not paired with web. Use /att link <code>.'
            return
        end
        local eventId = state.linkedEventId
        local eventName = state.linkedEventName or '?'
        local result, err = api.end_event(eventId)
        if not result then
            state.lastSyncSummary = 'End failed: ' .. tostring(err)
            print(chat.header('att') .. state.lastSyncSummary)
            return
        end
        -- The detailed "=== Event Ended ===" block below is sufficient
        -- chat output; just update the launcher's status line so the
        -- toast above the lists reflects the most recent action.
        state.lastSyncSummary = 'Ended: ' .. eventName

        -- Final summary: event details + per-participant DKP earned. Read
        -- straight from the end-event response so what's printed is what
        -- the server actually committed. JSON nulls round-trip as a
        -- sentinel TABLE in this Lua JSON lib, so guard every field with
        -- a string type-check before formatting -- otherwise a missing
        -- Location prints as "table: 0x...".
        do
            local hdr = chat.header('att')
            local function asStr(v)
                if type(v) == 'string' and v ~= '' then return v end
                return nil
            end
            print(hdr .. '=== Event Ended ===')
            print(hdr .. 'Event: ' .. (asStr(result.eventName) or tostring(eventName)))
            local etype = asStr(result.eventType)
            if etype then print(hdr .. 'Type: ' .. etype) end
            local eloc = asStr(result.eventLocation)
            if eloc then print(hdr .. 'Location: ' .. eloc) end
            local startStr = asStr(result.commencementStartTime)
            if startStr then
                local started = constants.parse_iso_utc_to_local_table(startStr)
                print(hdr .. 'Started: ' .. (constants.format_posted_at(started) or startStr))
            end
            local endStr = asStr(result.endTime)
            if endStr then
                local ended = constants.parse_iso_utc_to_local_table(endStr)
                print(hdr .. 'Ended:   ' .. (constants.format_posted_at(ended) or endStr))
            end
            -- Windowed events (HNM Style / Claim/Kill) report DKP per window
            -- attended; timed events report DKP per hour. Server tells us
            -- which mode applies via windowCount and the dkpPerWindow /
            -- dkpPerHour fields (only one is non-null at a time).
            local windowCount = tonumber(result.windowCount) or 1
            local isWindowed = windowCount > 1
            if isWindowed and tonumber(result.dkpPerWindow) then
                print(hdr .. string.format('DKP rate: %g / window (%d windows)',
                    tonumber(result.dkpPerWindow), windowCount))
            elseif tonumber(result.dkpPerHour) then
                print(hdr .. string.format('DKP rate: %g / hour', tonumber(result.dkpPerHour)))
            end
            local participants = (type(result.participants) == 'table') and result.participants or {}
            if #participants == 0 then
                print(hdr .. 'Participants: <none>')
            else
                local totalDkp = 0
                print(hdr .. string.format('Participants (%d):', #participants))
                for _, p in ipairs(participants) do
                    local jobs = string.format('%s/%s',
                        asStr(p.jobName) or '?',
                        asStr(p.subJobName) or '?')
                    local earned = tonumber(p.dkpEarned) or 0
                    totalDkp = totalDkp + earned
                    if isWindowed then
                        print(hdr .. string.format('  %s (%s) - %d window(s) - %g DKP',
                            asStr(p.characterName) or '?',
                            jobs,
                            tonumber(p.windowsAttended) or 0,
                            earned))
                    else
                        print(hdr .. string.format('  %s (%s) - %.2fh - %g DKP',
                            asStr(p.characterName) or '?',
                            jobs,
                            tonumber(p.durationHours) or 0,
                            earned))
                    end
                end
                if isWindowed then
                    print(hdr .. string.format('Total DKP awarded: %g', totalDkp))
                end
            end
            print(hdr .. '====================')
        end

        -- End-of-event CSV: gated on the same CSV Export checkbox the per-post
        -- write uses. Source is the server's end-event response (the chat
        -- block above prints the same data) so the file matches what got
        -- committed. We capture selectedMode BEFORE the cleanup block below
        -- nukes it so HNM events still route to "HNM Logs\".
        if state.launcherCsvOnStart then
            local modeForCsv = state.selectedMode
            local count, csvMsg = attendance.write_end_event_file(
                addon.path, modeForCsv, eventName, result)
            local hdr = chat.header('att')
            if count then
                print(hdr .. string.format('CSV summary: %d row(s). %s',
                    count, tostring(csvMsg or '')))
            else
                print(hdr .. 'CSV summary failed: ' .. tostring(csvMsg or 'unknown error'))
            end
        end

        if state.windowStateByEvent then
            state.windowStateByEvent[eventId] = nil
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

return M
