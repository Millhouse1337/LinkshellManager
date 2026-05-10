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

    -- Posts a captured ToD to the web app, OR updates an already-posted ToD's
    -- claim status. The capture record sits in state.todCaptures[index]; we
    -- mark it posting=true while the HTTP call is in flight so the buttons
    -- can grey themselves, then write the server's response back so the UI
    -- re-renders.
    --
    -- Two routes:
    --   * cap.posted is nil   -> POST /api/addon/tod (creates the row)
    --   * cap.posted exists   -> POST /api/addon/tod/{id}/claim (update only)
    --
    -- The second route exists because the loot-pool flow auto-creates the ToD
    -- with claim=nil (Not Specified) before the user has decided; clicking
    -- "Post (Claimed)" or "Post (Unclaimed)" on that row settles the status
    -- without recreating the row (which would orphan the loot rows).
    -- `claimed` is the user's explicit true/false from the launcher.
    out.on_post_tod = function(captureIndex, claimed)
        local cap = state.todCaptures and state.todCaptures[captureIndex]
        if not cap then return end
        if cap.posting then return end
        if not api.is_paired() then
            cap.postError = 'Not paired with web. Use /att link <code>.'
            return
        end

        if cap.posted and cap.posted.todId then
            cap.posting   = true
            cap.postError = nil
            local result, err = api.update_tod_claim(cap.posted.todId, claimed)
            cap.posting = false
            if not result then
                cap.postError = tostring(err or 'unknown error')
                print(chat.header('att') .. 'Update claim failed: ' .. cap.postError)
                return
            end
            cap.posted.claim = (result.claim == true) or false
            print(chat.header('att') .. string.format('ToD claim updated: %s -> %s',
                tostring(cap.monster),
                cap.posted.claim and 'Claimed' or 'Unclaimed'))
            return
        end

        cap.posting   = true
        cap.postError = nil
        local result, err = api.post_tod(cap.monster, cap.callbackSec, cap.message, claimed)
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

        -- claim is tri-state on the wire: true / false / nil. Mirror it on
        -- the cap record so the launcher can render "Not Specified" when the
        -- user hasn't decided yet (loot-first auto-post). Distinguish with
        -- ~= nil rather than a `result.claim and true or false` collapse so
        -- nil doesn't get coerced into false.
        local claimValue
        if result.claim == true or result.claim == false then
            claimValue = result.claim
        else
            claimValue = nil
        end
        cap.posted = {
            todId          = result.todId,
            repopTimeUtc   = result.repopTimeUtc,
            repopFormatted = repopFormatted,
            cooldown       = result.cooldown,
            interval       = result.interval,
            claim          = claimValue,
        }
        local claimLabel
        if claimValue == true then claimLabel = 'Claimed'
        elseif claimValue == false then claimLabel = 'Unclaimed'
        else claimLabel = 'Not Specified' end
        print(chat.header('att') .. string.format('Posted ToD: %s (id %s, %s)',
            tostring(cap.monster), tostring(result.todId), claimLabel))
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
        local altsByName = {}
        if type(result.roster) == 'table' then
            for _, entry in ipairs(result.roster) do
                if type(entry) == 'table'
                   and type(entry.characterName) == 'string'
                   and entry.characterName ~= '' then
                    local key = entry.characterName:lower()
                    local alts = {}
                    if type(entry.alts) == 'table' then
                        for _, alt in ipairs(entry.alts) do
                            if type(alt) == 'string' and alt ~= '' then
                                alts[#alts + 1] = alt
                            end
                        end
                    end
                    if #alts > 0 then
                        altsByName[key] = alts
                    end
                end
            end
        end

        state.rosterCache = {
            fetchedAt     = os.time(),
            names         = result.characterNames or {},
            altsByName    = altsByName,
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
        -- detection time. The auto-post leaves claim=nil (Not Specified)
        -- so the user can settle Claimed/Unclaimed afterward via the
        -- buttons in the ToD Capturing panel — the launcher keeps both
        -- buttons live as long as cap.posted.claim is nil.
        local todId = cap.posted and cap.posted.todId
        if not todId then
            drop.posting   = true
            drop.postError = nil
            local todResult, todErr = api.post_tod(cap.monster, cap.callbackSec, cap.message, nil)
            drop.posting = false
            if not todResult or not todResult.todId then
                drop.postError = 'ToD auto-post failed: ' .. tostring(todErr or 'unknown error')
                return
            end
            local repopLocalTable = constants.parse_iso_utc_to_local_table(todResult.repopTimeUtc)
            -- Preserve nil for claim — this auto-post path explicitly leaves
            -- claim Not Specified so the launcher buttons stay live.
            local autoClaim
            if todResult.claim == true or todResult.claim == false then
                autoClaim = todResult.claim
            else
                autoClaim = nil
            end
            cap.posted = {
                todId          = todResult.todId,
                repopTimeUtc   = todResult.repopTimeUtc,
                repopFormatted = repopLocalTable
                    and constants.format_posted_at(repopLocalTable)
                    or  tostring(todResult.repopTimeUtc),
                cooldown       = todResult.cooldown,
                interval       = todResult.interval,
                claim          = autoClaim,
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
