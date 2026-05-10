-- ui/launcher_loot_pool.lua
-- Loot Pool panel: drops detected for captured kills, with inline
-- Winner / DKP form. When more than one capture currently has loot
-- attached, each gets its own tab so an in-progress loot pool isn't
-- buried when a new monster is killed.
local imgui = require('imgui')

local M = {}

-- Renders the contents of one loot pool (drop rows + inline forms) for
-- a specific capture. Caller is responsible for any wrapping container
-- (BeginTabItem / BeginChild). Pulled out of M.draw so the single-pool
-- and tabbed paths share the exact same row-rendering logic.
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
