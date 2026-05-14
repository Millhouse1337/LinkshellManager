-- commands.lua
-- Chat-command handlers for the addon: /lsm (web sync + alliance snapshot)
-- and /attend (launcher window toggle). linkshellmanager.lua calls
-- M.register(state, deps) once during init; this module then registers each
-- ashita event handler with closures that capture state + deps.

local M = {}

function M.register(state, deps)
    local api        = deps.api
    local attendance = deps.attendance
    local settings   = deps.settings
    local chat       = deps.chat
    local utils      = deps.utils
    local config     = deps.config

    -- /att
    ashita.events.register('command', 'lsm_command_cb', function(e)
        local args = e.command:args()
        if #args == 0 or args[1]:lower() ~= '/lsm' then return end
        e.blocked = true

        -- Web sync: /lsm server <url>
        if #args >= 3 and args[2]:lower() == 'server' then
            local url = args[3]
            for i = 4, #args do url = url .. ' ' .. args[i] end

            if not url:lower():match('^https?://') then
                print(chat.header('lsm') .. 'Invalid URL: must start with http:// or https://')
                return
            end

            api.set_base_url(url)
            settings.save()
            print(chat.header('lsm') .. 'Web server set to: ' .. (config.api.baseUrl ~= '' and config.api.baseUrl or '<empty>'))

            -- Verify the URL actually points at an LSManager server before we
            -- leave the user wondering whether the typo took. Probe is a quick
            -- GET; any HTTP response (incl. 401) means reachable.
            local ok, info = api.probe()
            if ok then
                print(chat.header('lsm') .. string.format(
                    'Server OK (HTTP %s). Use /lsm link <code> [1|2] to pair.',
                    tostring(info)))
            else
                print(chat.header('lsm') .. 'Probe FAILED: ' .. tostring(info))
                print(chat.header('lsm') .. 'The URL was saved, but the server is not responding. Check the URL and try again.')
            end
            return
        end

        -- Web sync: /lsm link <code> [1|2]
        -- Channel is the in-game pearl slot the linkshell is worn on. Defaults to 1.
        -- Pairing on a channel that already has one replaces the existing pairing.
        if #args >= 3 and args[2]:lower() == 'link' then
            local code    = args[3]
            local channel = tonumber(args[4]) or 1
            if channel ~= 1 and channel ~= 2 then
                print(chat.header('lsm') .. 'Channel must be 1 or 2.')
                return
            end
            local result, err = api.pair(code, channel)
            if result then
                settings.save()
                print(chat.header('lsm') .. string.format('Linked to %s on LS%d%s',
                    result.linkshellName or '<linkshell>',
                    channel,
                    (result.label and result.label ~= '') and (' [' .. result.label .. ']') or ''))
                state.linkedEventId = nil
                state.windowMax = 1
                state.windowSequence = 0
                state.windowRosters = {}
                state.windowStateByEvent = {}
            else
                print(chat.header('lsm') .. 'Pair failed: ' .. tostring(err))
            end
            return
        end

        -- Web sync: /lsm unlink [1|2|all]
        if #args >= 2 and args[2]:lower() == 'unlink' then
            local target = (args[3] or 'all'):lower()
            if target == 'all' then
                api.unpair()
            elseif target == '1' or target == '2' then
                api.unpair(tonumber(target))
            else
                print(chat.header('lsm') .. 'Usage: /lsm unlink [1|2|all]')
                return
            end
            settings.save()
            state.linkedEventId = nil
            state.windowMax = 1
            state.windowSequence = 0
            state.windowRosters = {}
            state.windowStateByEvent = {}
            if target == 'all' then
                print(chat.header('lsm') .. 'Unlinked all pairings. Local CSV writes still work.')
            else
                print(chat.header('lsm') .. 'Unlinked LS' .. target .. '.')
            end
            return
        end

        -- Web sync: /lsm status
        if #args == 2 and args[2]:lower() == 'status' then
            print(chat.header('lsm') .. 'Server: ' .. (config.api.baseUrl ~= '' and config.api.baseUrl or '<not set>'))
            -- Probe each saved pairing against the server before printing so
            -- entries whose tokens were revoked from the web UI fall off the
            -- list. Network errors leave the list intact (no false positives).
            local dropped = api.validate_pairings() or {}
            for _, d in ipairs(dropped) do
                print(chat.header('lsm') .. string.format(
                    '  Removed LS%s: %s (id %s) - %s',
                    tostring(d.channel or '?'),
                    d.linkshellName or '?',
                    tostring(d.linkshellId or '?'),
                    d.reason or 'invalid token'))
            end
            local pairings = api.list_pairings()
            if #pairings == 0 then
                print(chat.header('lsm') .. 'Not linked. Use /lsm link <code> [1|2] after generating one on the website.')
            else
                for _, p in ipairs(pairings) do
                    print(chat.header('lsm') .. string.format('  LS%d: %s (id %s)%s',
                        p.channel,
                        p.linkshellName or '?',
                        tostring(p.linkshellId or '?'),
                        (p.label and p.label ~= '') and (' [' .. p.label .. ']') or ''))
                end
            end
            return
        end

        -- /lsm now: one-shot alliance snapshot. Mirrors Hatberg's standalone
        -- attendance addon: walk slots 0-17, write a CSV row per active
        -- member to Ashita\addons\linkshellmanager\Snapshots\<char>_<date>_<time>.csv,
        -- and push the same payload to the LSManager web app so the
        -- Attendance Snapshots page can render it.
        if #args == 2 and args[2]:lower() == 'now' then
            local snapshot, err = attendance.list_alliance_snapshot()
            if not snapshot then
                print(chat.header('lsm') .. 'Snapshot failed: ' .. tostring(err or 'unknown error'))
                return
            end

            -- Local CSV (best-effort, mirrors Hatberg's format).
            local dir = addon.path .. 'Snapshots\\'
            os.execute('mkdir "' .. dir:gsub('\\$', '') .. '" 2>nul')
            local dateStr = os.date('%Y-%m-%d')
            local timeStr = os.date('%H-%M-%S')
            local safeName = (snapshot.capturedBy or 'unknown'):gsub('[^%w]', '_')
            local fileName = string.format('%s_%s_%s.csv', safeName, dateStr, timeStr)
            local fullPath = dir .. fileName
            local logdate = os.date('%Y-%m-%d')
            local logtime = os.date('%H:%M:%S')
            local utcSuffix = 'UTC' .. (snapshot.utcOffset or '')

            local csvOk, csvErr = true, nil
            local f, openErr = io.open(fullPath, 'a')
            if not f then
                csvOk = false
                csvErr = tostring(openErr or 'open failed')
            else
                for _, e in ipairs(snapshot.entries) do
                    local mainStr = (e.mainJob ~= '' and e.mainJob or '') ..
                                    (e.mainJobLevel and tostring(e.mainJobLevel) or '')
                    local subStr  = (e.subJob  ~= '' and e.subJob  or '') ..
                                    (e.subJobLevel  and tostring(e.subJobLevel)  or '')
                    local row = string.format('%s,%s/%s,%s,%s,%s,%s,\n',
                        e.name, mainStr, subStr, logdate, logtime, utcSuffix, e.zone or '')
                    f:write(row)
                end
                f:close()
            end

            -- Web sync (also best-effort — CSV is the local-redundancy half).
            local result, apiErr = api.post_attendance_snapshot(snapshot)

            local count = #snapshot.entries
            if csvOk and result then
                print(chat.header('lsm') .. string.format(
                    'Snapshot: %d entries logged locally + synced to web (id %s).',
                    count, tostring(result.snapshotId or '?')))
            elseif csvOk and not result then
                print(chat.header('lsm') .. string.format(
                    'Snapshot: %d entries logged locally. Web sync failed: %s.',
                    count, tostring(apiErr or 'unknown')))
            elseif (not csvOk) and result then
                print(chat.header('lsm') .. string.format(
                    'Snapshot: %d entries synced to web. Local CSV failed: %s.',
                    count, tostring(csvErr or 'unknown')))
            else
                print(chat.header('lsm') .. string.format(
                    'Snapshot failed: CSV (%s); web (%s).',
                    tostring(csvErr or 'unknown'),
                    tostring(apiErr or 'unknown')))
            end
            return
        end

        -- /lsm help: chat-based command list + quick-start. Also serves as
        -- the fallback for bare "/lsm" with no subcommand and for any
        -- unrecognised subcommand.
        local function print_chat_help(prefix)
            local hdr = chat.header('lsm')
            if prefix and prefix ~= '' then
                print(hdr .. prefix)
            end
            print(hdr .. 'Commands:')
            print(hdr .. '  /lsm server <url>           Set the LSManager web server URL.')
            print(hdr .. '  /lsm link <code> [1|2]      Redeem a pairing code on slot 1 or 2 (default 1).')
            print(hdr .. '  /lsm unlink [1|2|all]       Drop a slot pairing (or all).')
            print(hdr .. '  /lsm status                 Show pairings; auto-drops revoked tokens.')
            print(hdr .. '  /lsm now                    Capture an alliance snapshot (local CSV + web sync).')
            print(hdr .. '  /lsm help                   Show this list.')
            print(hdr .. '  /attend                     Toggle the main launcher window.')
            print(hdr .. '  /attend close               Close the launcher.')
            print(hdr .. 'Quick start: /lsm server <url> -> /lsm link <code> -> /attend.')
        end

        if #args == 2 and args[2]:lower() == 'help' then
            print_chat_help(nil)
            return
        end

        -- Unrecognised subcommand (or bare "/lsm"): print help with a hint.
        print_chat_help(string.format('Unknown command: %s. Try /lsm help.',
            table.concat(args, ' ')))
    end)

    -- /attend
    ashita.events.register('command', 'lsm_attend_cmd', function(e)
        local args = e.command:args()
        if #args == 0 or args[1]:lower() ~= '/attend' then return end
        e.blocked = true

        -- Toggle/Open logic
        if #args > 1 and args[2]:lower() == 'close' then
            state.isAttendLauncherOpen = false
        else
            state.isAttendLauncherOpen = not state.isAttendLauncherOpen
        end

        if state.isAttendLauncherOpen then
            -- Flag picked up by ui.draw_launcher to force the window back to
            -- our preferred default dimensions on every fresh /attend, so a
            -- prior manual resize (cached by imgui.ini) doesn't shrink the
            -- launcher next time the user opens it.
            state.launcherSizePending = true
            utils.update_suggestions(state, deps)
            -- Seed the launcher roster with the local player so they show up
            -- immediately, before any scan. attendance.add_self() is a no-op if
            -- the user is already in attendance.data.
            attendance.add_self()
            -- Pull the latest queued events from the web app so they appear in
            -- the Queued Events list without requiring a manual Refresh.
            if api.is_paired() then
                local events, err = api.list_events()
                if events then
                    state.webEvents = events
                    state.webEventsLoadedAt = os.time()
                elseif err then
                    print(chat.header('lsm') .. 'Could not load events: ' .. tostring(err))
                end
            end
        end
    end)

end

return M
