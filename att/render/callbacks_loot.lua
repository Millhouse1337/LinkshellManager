-- render/callbacks_loot.lua
-- ToD / loot callbacks: on_post_tod, on_load_roster, on_post_loot. Bodies are
-- byte-for-byte from the original att.lua callbacks table.
--
-- ROSTER_CACHE_TTL_SEC is the local module constant used by on_load_roster.
-- Originally a top-level local in att.lua; relocated here because nothing
-- else references it.

local M = {}

local ROSTER_CACHE_TTL_SEC = 300  -- refetch roster after 5 min of staleness

function M.install(out, state, deps)
    local api       = deps.api
    local chat      = deps.chat
    local constants = deps.constants

    -- Posts a captured ToD to the web app. The capture record sits in
    -- state.todCaptures[index]; we mark it posting=true while the HTTP
    -- call is in flight so the button can grey itself, then write the
    -- server's response onto the record so the UI can render Repop time.
    -- Synchronous (api.request blocks) but the call is small and fast.
    out.on_post_tod = function(captureIndex)
        local cap = state.todCaptures and state.todCaptures[captureIndex]
        if not cap then return end
        if cap.posting or cap.posted then return end
        if not api.is_paired() then
            cap.postError = 'Not paired with web. Use /att link <code>.'
            return
        end

        cap.posting   = true
        cap.postError = nil
        local result, err = api.post_tod(cap.monster, cap.callbackSec, cap.message)
        cap.posting = false

        if not result then
            cap.postError = tostring(err or 'unknown error')
            print(chat.header('att') .. 'Post ToD failed: ' .. cap.postError)
            return
        end

        -- Format repop time in the same "April 29th 2026 5:45:58 PM"
        -- style as Captured at: parse the server's ISO-8601 UTC string
        -- to a local-time table, then run through format_posted_at. Fall
        -- back to the raw string if parsing somehow fails.
        local repopLocalTable = constants.parse_iso_utc_to_local_table(result.repopTimeUtc)
        local repopFormatted  = repopLocalTable
            and constants.format_posted_at(repopLocalTable)
            or  tostring(result.repopTimeUtc)

        cap.posted = {
            todId          = result.todId,
            repopTimeUtc   = result.repopTimeUtc,
            repopFormatted = repopFormatted,
            cooldown       = result.cooldown,
            interval       = result.interval,
        }
        print(chat.header('att') .. string.format('Posted ToD: %s (id %s)',
            tostring(cap.monster), tostring(result.todId)))
    end

    -- Lazy-fetches the linkshell roster + loot structure used to
    -- populate the Winner combo on the Loot Pool panel. force=true
    -- bypasses the TTL check (used by an explicit Refresh button).
    out.on_load_roster = function(force)
        if state.rosterFetching then return end
        local cache = state.rosterCache
        if not force and cache and cache.fetchedAt
           and (os.time() - cache.fetchedAt) < ROSTER_CACHE_TTL_SEC then
            return
        end
        if not api.is_paired() then
            state.rosterError = 'Not paired with web. Use /att link <code>.'
            return
        end
        state.rosterFetching = true
        state.rosterError    = nil
        local result, err = api.list_roster()
        state.rosterFetching = false
        if not result then
            state.rosterError = tostring(err or 'roster fetch failed')
            return
        end
        state.rosterCache = {
            fetchedAt     = os.time(),
            names         = result.characterNames or {},
            lootStructure = result.lootStructure or 'Dkp',
        }
    end

    -- Posts a single drop's loot detail to the server. The drop record
    -- on the capture's lootDrops carries draft state (winner, dkpSpent)
    -- which the UI binds to imgui inputs; this callback reads them,
    -- calls api.post_loot, and writes the response back onto the drop
    -- so the UI can render "Posted: <winner> for <dkp> DKP".
    out.on_post_loot = function(captureIndex, dropIndex)
        local cap = state.todCaptures and state.todCaptures[captureIndex]
        if not cap then return end
        local drop = cap.lootDrops and cap.lootDrops[dropIndex]
        if not drop then return end
        if drop.posting or drop.posted then return end

        if not api.is_paired() then
            drop.postError = 'Not paired with web. Use /att link <code>.'
            return
        end

        -- Auto-post the parent ToD if the user hasn't already done so.
        -- The server requires a parent Tod row for every TodLootDetail,
        -- so we transparently create one using the capture's monster +
        -- detection time. The result is stashed back on cap.posted so
        -- the ToD Capturing panel updates (Posted to web! + repop time)
        -- the same way as if the user had clicked Post ToD manually.
        local todId = cap.posted and cap.posted.todId
        if not todId then
            drop.posting   = true
            drop.postError = nil
            local todResult, todErr = api.post_tod(cap.monster, cap.callbackSec, cap.message)
            drop.posting = false
            if not todResult or not todResult.todId then
                drop.postError = 'ToD auto-post failed: ' .. tostring(todErr or 'unknown error')
                return
            end
            local repopLocalTable = constants.parse_iso_utc_to_local_table(todResult.repopTimeUtc)
            cap.posted = {
                todId          = todResult.todId,
                repopTimeUtc   = todResult.repopTimeUtc,
                repopFormatted = repopLocalTable
                    and constants.format_posted_at(repopLocalTable)
                    or  tostring(todResult.repopTimeUtc),
                cooldown       = todResult.cooldown,
                interval       = todResult.interval,
            }
            todId = todResult.todId
            print(chat.header('att') .. string.format(
                'Auto-posted ToD for loot: %s (id %s)',
                tostring(cap.monster), tostring(todId)))
        end

        local winner = (drop.draft.winner or ''):gsub('^%s+', ''):gsub('%s+$', '')
        local dkpStr = tostring(drop.draft.dkpSpent or '')
        local dkp    = tonumber(dkpStr)
        if winner == '' then
            drop.postError = 'Pick a winner.'
            return
        end

        -- Loot Council linkshells skip DKP entirely -- the addon hides
        -- the input and the server's AdjustTodLootDkpAsync no-ops the
        -- ledger writes for them, so passing 0 here is safe and lets
        -- the user record loot allocation (winner only) without a value.
        local lootStruct = (state.rosterCache and state.rosterCache.lootStructure) or 'Dkp'
        if lootStruct == 'LootCouncil' then
            dkp = dkp or 0
        elseif not dkp or dkp <= 0 then
            local label = (lootStruct == 'Hybrid') and 'percentage' or 'DKP value'
            drop.postError = 'Enter a positive ' .. label .. '.'
            return
        end

        drop.posting   = true
        drop.postError = nil
        local result, err = api.post_loot(todId, drop.itemName, winner, dkp)
        drop.posting = false

        if not result then
            drop.postError = tostring(err or 'unknown error')
            print(chat.header('att') .. 'Post Loot failed: ' .. drop.postError)
            return
        end

        drop.posted = {
            lootDetailId      = result.lootDetailId,
            itemWinner        = result.itemWinner,
            winningDkpSpent   = result.winningDkpSpent,
            actualDeductedDkp = result.actualDeductedDkp,
        }
        drop.draftOpen = false
        print(chat.header('att') .. string.format(
            'Posted loot: %s -> %s for %s DKP',
            tostring(drop.itemName),
            tostring(result.itemWinner),
            tostring(result.winningDkpSpent)))
    end
end

return M
