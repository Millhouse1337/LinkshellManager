-- ui/launcher_loot_pool.lua
-- Loot Pool panel: drops detected for the active capture, with inline
-- Winner / DKP form. Extracted from ui.lua byte-for-byte.
local imgui = require('imgui')

local M = {}

function M.draw(state, callbacks)
    -- Loot Pool panel: shows items detected in chat ("You find X on the
    -- <monster>.") for the most recent capture matching the selected
    -- preset (state.pendingEventName). Each row carries a "Post Loot"
    -- button that opens an inline Winner / DKP Spent form bound to the
    -- per-drop draft state set up in att.lua's text_in handler.
    do
        -- Loot pool tracks the *most recent* capture by default. If the
        -- user explicitly clicked an Event Preset, prefer a capture
        -- matching that monster (so clicking a different preset can
        -- pivot back to that monster's loot pool while it's still in
        -- the ring buffer). Otherwise fall through to the newest
        -- capture so kills made without pre-selecting a preset still
        -- surface their drops.
        local function find_active_capture()
            local captures = state.todCaptures or {}
            local target = state.pendingEventName
            if target and target ~= '' then
                for idx, cap in ipairs(captures) do
                    if cap.monster == target then return cap, idx end
                end
            end
            if captures[1] then return captures[1], 1 end
            return nil, -1
        end

        local activeCap, activeIdx = find_active_capture()
        local titleMonster = (activeCap and activeCap.monster)
            or state.pendingEventName or '<none>'

        imgui.Text(titleMonster .. ' Loot Pool')

        -- Right-aligned Clear button on the title row. Always visible
        -- (mirrors the ToD Capture Clear) so the user has a consistent
        -- spot to dismiss the section regardless of whether drops have
        -- landed yet. Wipes only the drops for the active capture, never
        -- the parent ToD entry — clearing the loot pool shouldn't make
        -- the matching ToD line disappear.
        do
            local LOOT_CLEAR_W = 70
            local lootWindowWidth = 600
            pcall(function()
                local ww = imgui.GetWindowWidth()
                if type(ww) == 'number' then lootWindowWidth = ww end
            end)
            imgui.SameLine(lootWindowWidth - LOOT_CLEAR_W - 16)
            if imgui.Button('Clear##lootClear', { LOOT_CLEAR_W, 0 }) then
                if activeCap then
                    activeCap.lootDrops = {}
                end
            end
        end

        imgui.BeginChild('lootPool', { 0, 110 }, false)

        if not activeCap then
            imgui.TextDisabled('  Pick an Event Preset to see its loot pool.')
        else
            local drops = activeCap.lootDrops or {}
            if #drops == 0 then
                imgui.TextDisabled('  Drops will appear here as items fall in chat.')
            else
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

                        -- Winner combo. Falls back to a text input when
                        -- the roster cache is empty (initial fetch in
                        -- flight or fetch failed) so the user can still
                        -- type a name manually.
                        imgui.Text('Winner:')
                        imgui.SameLine()
                        imgui.PushItemWidth(160)
                        if #names == 0 then
                            local winnerPtr = { drop.draft.winner or '' }
                            if imgui.InputText(string.format('##winner_%d_%d', activeIdx, dIdx),
                                    winnerPtr, 64) then
                                drop.draft.winner = winnerPtr[1] or ''
                            end
                        else
                            local current = drop.draft.winner or ''
                            if imgui.BeginCombo(string.format('##winnerCombo_%d_%d', activeIdx, dIdx),
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
                        --   * Loot Council → no DKP tracking at all, so
                        --     the input row is omitted entirely; the
                        --     server-side AdjustTodLootDkpAsync skips
                        --     ledger writes for these linkshells anyway.
                        --   * Percentage Based (server enum still 'Hybrid')
                        --     → label as "Deduction %:" so the user knows
                        --     to type 0-100, not a flat DKP amount.
                        --   * DKP → flat DKP value as before.
                        if lootStruct ~= 'LootCouncil' then
                            local dkpLabel = (lootStruct == 'Hybrid') and 'Deduction %:' or 'DKP Spent:'
                            imgui.Text(dkpLabel)
                            imgui.SameLine()
                            imgui.PushItemWidth(80)
                            local dkpPtr = { tostring(drop.draft.dkpSpent or '') }
                            if imgui.InputText(string.format('##dkp_%d_%d', activeIdx, dIdx),
                                    dkpPtr, 16) then
                                drop.draft.dkpSpent = dkpPtr[1] or ''
                            end
                            imgui.PopItemWidth()
                            imgui.SameLine()
                        end

                        if drop.posting then
                            imgui.TextDisabled('Saving...')
                        else
                            if imgui.Button(string.format('Save##save_%d_%d', activeIdx, dIdx),
                                    { 60, 0 }) then
                                if callbacks.on_post_loot then
                                    callbacks.on_post_loot(activeIdx, dIdx)
                                end
                            end
                            imgui.SameLine()
                            if imgui.Button(string.format('Cancel##cancel_%d_%d', activeIdx, dIdx),
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
                        -- Default state: item + Post Loot button. The
                        -- server endpoint still requires a parent Tod
                        -- row, so on_post_loot auto-posts a ToD first
                        -- when cap.posted is nil. From the UI's POV,
                        -- Post Loot is always available.
                        imgui.Text('  ' .. tostring(drop.itemName or '?'))
                        imgui.SameLine()
                        if imgui.Button(string.format('Post Loot##postLoot_%d_%d',
                                activeIdx, dIdx), { 80, 0 }) then
                            drop.draftOpen = true
                            drop.postError = nil
                            if callbacks.on_load_roster then
                                callbacks.on_load_roster(false)
                            end
                        end
                    end
                end
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
