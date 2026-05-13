-- ui/launcher_loot_pool.lua
-- Loot Pool panel: drops detected for captured kills, with inline
-- Winner / DKP form. When more than one capture currently has loot
-- attached, each gets its own tab so an in-progress loot pool isn't
-- buried when a new monster is killed.
local imgui      = require('imgui')
local items      = require('items')
local attendance = require('attendance')

local M = {}

-- Scope options for the per-row "Can equip" filter. Each tuple is
-- { id, label } where id is the value stored on the drop row.
local SCOPE_OPTIONS = {
    { 'ls',    'Linkshell' },
    { 'alli',  'Alliance'  },
    { 'party', 'Party'     },
}

local function scope_label(id)
    for _, opt in ipairs(SCOPE_OPTIONS) do
        if opt[1] == id then return opt[2] end
    end
    return 'Linkshell'
end

-- Reads current main + sub job for the given party-slot index from Ashita
-- memory. Returns (name, mainAbbrev, subAbbrev) or nil if the slot is empty.
local function read_party_slot(slot, jobList)
    local pm = AshitaCore and AshitaCore:GetMemoryManager() and AshitaCore:GetMemoryManager():GetParty()
    if not pm then return nil end
    local name = pm:GetMemberName(slot)
    if not name or name == '' then return nil end
    local mj = jobList[pm:GetMemberMainJob(slot) or 0] or 'NONE'
    local sj = jobList[pm:GetMemberSubJob(slot)  or 0] or 'NONE'
    return name, mj, sj
end

local function any_allowed(allowedSet, jobAbbrev)
    return jobAbbrev and jobAbbrev ~= 'NONE' and allowedSet[jobAbbrev] == true
end

-- Tests whether any job in the player's stored jobLevels (24-int array,
-- index = jobId + 1 because JSON arrays are 0-indexed but Lua is 1-based)
-- has level > 0 AND its abbreviation is in `allowedSet`. Returns boolean.
local function jobLevels_match(allowedSet, jobLevels, jobList)
    if type(jobLevels) ~= 'table' then return false end
    -- jobLevels arrives as a JSON array; json.decode produces a Lua array
    -- whose key 1 maps to JSON index 0, so jobId N is at Lua key N+1.
    for jobId = 1, 23 do
        local lvl = jobLevels[jobId + 1] or 0
        if lvl > 0 then
            local ab = jobList[jobId]
            if ab and allowedSet[ab] == true then return true end
        end
    end
    return false
end

-- Builds a sorted, de-duplicated list of character names who can equip
-- the item under the active scope. `allowedJobs` is the list returned by
-- items.jobs_for (three-letter abbreviations). Returns an array of strings.
local function compute_candidates(state, allowedJobs, scope)
    -- O(1) membership set built once per call.
    local allowedSet = {}
    for _, ab in ipairs(allowedJobs or {}) do allowedSet[ab] = true end

    local resources = require('resources')
    local jobList   = resources.attJobList

    local cache             = state.rosterCache
    local rosterByName      = (cache and cache.displayByName)   or {}
    local jobLevelsByName   = (cache and cache.jobLevelsByName) or {}
    local lsNamesByLower    = {}
    for _, name in ipairs((cache and cache.names) or {}) do
        if type(name) == 'string' and name ~= '' then
            lsNamesByLower[name:lower()] = name
        end
    end

    local picked = {}            -- [lower] = display name
    local function add(displayName)
        if not displayName or displayName == '' then return end
        local key = displayName:lower()
        if picked[key] then return end
        picked[key] = displayName
    end

    if scope == 'ls' then
        -- All LS members whose published jobLevels include any allowed
        -- leveled job. LS members with no published jobLevels are still
        -- included if their current in-zone main/sub matches (so a member
        -- who joined before publishing isn't invisible).
        for lower, display in pairs(lsNamesByLower) do
            if jobLevels_match(allowedSet, jobLevelsByName[lower], jobList) then
                add(display)
            else
                -- Fallback: in-zone current job.
                for _, row in ipairs(attendance.data or {}) do
                    local cleaned = (row.name or ''):gsub('^X%s+', '')
                    if cleaned:lower() == lower
                       and (any_allowed(allowedSet, row.jobsMain)
                         or any_allowed(allowedSet, row.jobsSub)) then
                        add(display)
                        break
                    end
                end
            end
        end
    else
        -- 'alli' = slots 0-17, 'party' = slots 0-5. Pull the live snapshot
        -- from Ashita's party memory manager so the list reflects who is
        -- ACTUALLY grouped right now, not whoever was last scanned for an
        -- attendance gather.
        local maxSlot = (scope == 'party') and 5 or 17
        for slot = 0, maxSlot do
            local name, mj, sj = read_party_slot(slot, jobList)
            if name then
                local matched = any_allowed(allowedSet, mj) or any_allowed(allowedSet, sj)
                if not matched then
                    -- LS member with a richer published jobLevels list?
                    local levels = jobLevelsByName[name:lower()]
                    if jobLevels_match(allowedSet, levels, jobList) then
                        matched = true
                    end
                end
                if matched then
                    local display = rosterByName[name:lower()] or name
                    add(display)
                end
            end
        end
    end

    local out = {}
    for _, display in pairs(picked) do out[#out + 1] = display end
    table.sort(out, function(a, b) return a:lower() < b:lower() end)
    return out
end

-- Renders the contents of one loot pool (drop rows + inline forms) for
-- a specific capture. Caller is responsible for any wrapping container
-- (BeginTabItem / BeginChild). Pulled out of M.draw so the single-pool
-- and tabbed paths share the exact same row-rendering logic.
-- Renders the per-row scope combo + sorted "Can equip" candidate list.
-- Hidden entirely when the item has no equip restriction (consumable,
-- key item, currency, seal, etc) so non-equip drops stay compact.
local function render_candidates_block(state, drop, capIdx, dIdx, callbacks)
    local allowedJobs = items.jobs_for(drop.itemName)
    if not allowedJobs or #allowedJobs == 0 then return end

    -- Per-row scope state, default Linkshell. Drop rows persist across
    -- frames (they live on cap.lootDrops) so the user's choice sticks.
    drop.candScope = drop.candScope or 'ls'

    imgui.Indent(20)

    -- Scope combo.
    imgui.Text('Show:')
    imgui.SameLine()
    imgui.PushItemWidth(110)
    if imgui.BeginCombo(string.format('##candScope_%d_%d', capIdx, dIdx),
            scope_label(drop.candScope)) then
        for _, opt in ipairs(SCOPE_OPTIONS) do
            local selected = (opt[1] == drop.candScope)
            if imgui.Selectable(opt[2], selected) then
                drop.candScope = opt[1]
            end
            if selected then imgui.SetItemDefaultFocus() end
        end
        imgui.EndCombo()
    end
    imgui.PopItemWidth()

    -- Candidate list. Click a name to pre-fill / overwrite the Winner combo.
    local candidates = compute_candidates(state, allowedJobs, drop.candScope)
    local jobsLabel = table.concat(allowedJobs, '/')
    if #candidates == 0 then
        imgui.TextColored({ 0.7, 0.7, 0.7, 1.0 },
            string.format('Can equip (%s): (none in current scope)', jobsLabel))
    else
        imgui.TextColored({ 0.65, 0.85, 1.0, 1.0 },
            string.format('Can equip (%s):', jobsLabel))
        imgui.SameLine()
        for ci, name in ipairs(candidates) do
            if imgui.SmallButton(string.format('%s##cand_%d_%d_%d',
                    name, capIdx, dIdx, ci)) then
                drop.draftOpen = true
                drop.postError = nil
                drop.draft     = drop.draft or {}
                drop.draft.winner = name
                if callbacks.on_load_roster then
                    callbacks.on_load_roster(false)
                end
            end
            if ci < #candidates then imgui.SameLine() end
        end
    end

    imgui.Unindent(20)
end

local function render_pool_drops(state, callbacks, cap, capIdx)
    local drops = cap.lootDrops or {}
    if #drops == 0 then
        imgui.TextDisabled('  Drops will appear here as items fall in chat.')
        return
    end
    for dIdx, drop in ipairs(drops) do
        if drop.posted then
            -- Posted state: greyed item + green confirmation.
            imgui.Text('  ' .. tostring(drop.itemName or '?'))
            imgui.SameLine()
            imgui.TextColored({ 0.4, 1.0, 0.4, 1.0 }, string.format(
                'Posted: %s -- %s DKP',
                tostring(drop.posted.itemWinner or '?'),
                tostring(drop.posted.winningDkpSpent or '?')))
        elseif drop.draftOpen then
            -- Inline form. Winner combo + DKP int input + Save/Cancel.
            imgui.Text('  ' .. tostring(drop.itemName or '?'))
            imgui.Indent(20)
            local cache = state.rosterCache
            local names = (cache and cache.names) or {}
            local lootStruct = (cache and cache.lootStructure) or 'Dkp'

            imgui.Text('Winner:')
            imgui.SameLine()
            imgui.PushItemWidth(160)
            if #names == 0 then
                local winnerPtr = { drop.draft.winner or '' }
                if imgui.InputText(string.format('##winner_%d_%d', capIdx, dIdx),
                        winnerPtr, 64) then
                    drop.draft.winner = winnerPtr[1] or ''
                end
            else
                local current = drop.draft.winner or ''
                if imgui.BeginCombo(string.format('##winnerCombo_%d_%d', capIdx, dIdx),
                        current ~= '' and current or 'Select winner') then
                    for _, name in ipairs(names) do
                        local selected = (name == current)
                        if imgui.Selectable(name, selected) then
                            drop.draft.winner = name
                        end
                        if selected then imgui.SetItemDefaultFocus() end
                    end
                    imgui.EndCombo()
                end
            end
            imgui.PopItemWidth()
            imgui.SameLine()

            -- DKP field varies by loot structure:
            --   * Loot Council → no DKP tracking; input row omitted.
            --   * Percentage Based ('Hybrid') → "Deduction %:" label.
            --   * DKP → flat DKP value.
            if lootStruct ~= 'LootCouncil' then
                local dkpLabel = (lootStruct == 'Hybrid') and 'Deduction %:' or 'DKP Spent:'
                imgui.Text(dkpLabel)
                imgui.SameLine()
                imgui.PushItemWidth(80)
                local dkpPtr = { tostring(drop.draft.dkpSpent or '') }
                if imgui.InputText(string.format('##dkp_%d_%d', capIdx, dIdx),
                        dkpPtr, 16) then
                    drop.draft.dkpSpent = dkpPtr[1] or ''
                end
                imgui.PopItemWidth()
                imgui.SameLine()
            end

            if drop.posting then
                imgui.TextDisabled('Saving...')
            else
                if imgui.Button(string.format('Save##save_%d_%d', capIdx, dIdx),
                        { 60, 0 }) then
                    if callbacks.on_post_loot then
                        callbacks.on_post_loot(capIdx, dIdx)
                    end
                end
                imgui.SameLine()
                if imgui.Button(string.format('Cancel##cancel_%d_%d', capIdx, dIdx),
                        { 60, 0 }) then
                    drop.draftOpen = false
                    drop.postError = nil
                end
            end

            if drop.postError then
                imgui.TextColored({ 1.0, 0.5, 0.5, 1.0 },
                    'Post failed: ' .. tostring(drop.postError))
            end
            imgui.Unindent(20)

            render_candidates_block(state, drop, capIdx, dIdx, callbacks)
        else
            -- Default state: item + Post Loot button. The server endpoint
            -- still requires a parent Tod row, so on_post_loot auto-posts
            -- a ToD first when cap.posted is nil.
            imgui.Text('  ' .. tostring(drop.itemName or '?'))
            imgui.SameLine()
            if imgui.Button(string.format('Post Loot##postLoot_%d_%d',
                    capIdx, dIdx), { 110, 0 }) then
                drop.draftOpen = true
                drop.postError = nil
                if callbacks.on_load_roster then
                    callbacks.on_load_roster(false)
                end
            end

            render_candidates_block(state, drop, capIdx, dIdx, callbacks)
        end
    end
end

-- Picks which captures should currently have a visible loot pool. Walks
-- state.todCaptures newest-first and includes:
--   * the most recent capture (always — so a fresh kill has an empty
--     pool ready to receive drops),
--   * any older capture that still has loot attached (so an in-progress
--     pool isn't lost when the user kills a new monster), and
--   * the capture matching state.pendingEventName when a preset was
--     clicked (covers the "pivot back via Event Preset" case).
local function collect_pools(state)
    local captures = state.todCaptures or {}
    local pools = {}
    local target = state.pendingEventName
    if target == '' then target = nil end
    for idx, cap in ipairs(captures) do
        local hasDrops      = cap.lootDrops and #cap.lootDrops > 0
        local matchesPreset = target and cap.monster == target
        if idx == 1 or hasDrops or matchesPreset then
            pools[#pools + 1] = { cap = cap, idx = idx }
        end
    end
    return pools
end

function M.draw(state, callbacks)
    do
        local pools = collect_pools(state)

        -- Title text. Single pool keeps the historical "<Monster> Loot
        -- Pool" wording; multi-pool collapses to a generic header so the
        -- per-tab labels carry the monster names.
        local titleMonster
        if #pools > 1 then
            titleMonster = 'Loot Pools (' .. tostring(#pools) .. ')'
        elseif pools[1] then
            titleMonster = tostring(pools[1].cap.monster or '?') .. ' Loot Pool'
        else
            titleMonster = (state.pendingEventName or '<none>') .. ' Loot Pool'
        end

        imgui.Text(titleMonster)

        -- Right-aligned Clear. Single pool: clears that pool's drops.
        -- Multi pool: clears every visible pool's drops in one shot
        -- (per-tab discard would require ImGui tab close-buttons which
        -- the host imgui binding doesn't reliably expose).
        do
            local LOOT_CLEAR_W = 70
            local lootWindowWidth = 600
            pcall(function()
                local ww = imgui.GetWindowWidth()
                if type(ww) == 'number' then lootWindowWidth = ww end
            end)
            imgui.SameLine(lootWindowWidth - LOOT_CLEAR_W - 16)
            if imgui.Button('Clear##lootClear', { LOOT_CLEAR_W, 0 }) then
                for _, pool in ipairs(pools) do
                    pool.cap.lootDrops = {}
                end
            end
        end

        imgui.BeginChild('lootPool', { 0, 160 }, false)

        if #pools == 0 then
            imgui.TextDisabled('  Pick an Event Preset to see its loot pool.')
        elseif #pools == 1 then
            render_pool_drops(state, callbacks, pools[1].cap, pools[1].idx)
        else
            -- Tabs: one per pool, monster name on the tab. Tab IDs include
            -- the capture's index so distinct same-name captures (rare but
            -- possible after a re-pop) don't collide.
            if imgui.BeginTabBar('lootPoolTabs') then
                for _, pool in ipairs(pools) do
                    local tabLabel = tostring(pool.cap.monster or '?')
                        .. '##lootPoolTab_' .. tostring(pool.idx)
                    if imgui.BeginTabItem(tabLabel) then
                        render_pool_drops(state, callbacks, pool.cap, pool.idx)
                        imgui.EndTabItem()
                    end
                end
                imgui.EndTabBar()
            end
        end

        if state.rosterError then
            imgui.TextColored({ 1.0, 0.5, 0.5, 1.0 },
                '  Roster error: ' .. tostring(state.rosterError))
        end

        imgui.EndChild()
    end
end

return M
