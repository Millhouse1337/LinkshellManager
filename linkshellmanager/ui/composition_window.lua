-- ui/composition_window.lua
-- Extracted from ui.lua (function body byte-for-byte).
local imgui = require('imgui')

local M = {}

-- Persistent filter state (was a file-level local in ui.lua).
local filterPtr = { '' }

function M.draw(is_open, comp_module)
    if not is_open or not comp_module.results then return false end

    -- Calculate Required Width
    -- Req Column (300) + Padding (~30) + Alliances (Count * (310 + 8 padding))
    local allianceCount = (comp_module.party_results and comp_module.party_results.alliances) and #comp_module.party_results.alliances or 1
    local targetWidth = 340 + (allianceCount * 320)
    if targetWidth < 800 then targetWidth = 800 end -- Min width

    -- Dynamic Resize if count changed
    comp_module.uiState = comp_module.uiState or {}
    if comp_module.uiState.lastAllianceCount ~= allianceCount then
        imgui.SetNextWindowSize({ targetWidth, 600 }, ImGuiCond_Always)
        comp_module.uiState.lastAllianceCount = allianceCount
    else
        imgui.SetNextWindowSize({ targetWidth, 600 }, ImGuiCond_FirstUseEver)
    end

    local openPtr = { is_open }
    if imgui.Begin('Composition Check: ' .. (comp_module.currentEvent or 'Unknown'), openPtr) then

        local res = comp_module.results

        -- LEFT COLUMN: Requirements
        imgui.BeginChild('col_req', { 300, -40 }, true)
        imgui.Text('Requirements')
        imgui.Separator()

        local function draw_section(title, data)
            imgui.TextColored({0.4, 1.0, 0.4, 1.0}, title)

            for _, entry in ipairs(data) do
                local have = #entry.filled
                local need = entry.needed
                local color = (have >= need) and {0.6, 1.0, 0.6, 1.0} or {1.0, 0.4, 0.4, 1.0}

                imgui.TextColored(color, string.format('[%d/%d] %s', have, need, entry.role))
                if have > 0 then
                    for _, p in ipairs(entry.filled) do
                        imgui.Indent(15)
                        -- Selectable Name
                        local is_selected = (comp_module.selected_player == p.name)
                        if imgui.Selectable(string.format('%s (%s/%s)', p.name, p.jobMain, p.jobSub), is_selected) then
                            -- Toggle selection
                            if is_selected then
                                comp_module.selected_player = nil
                            else
                                comp_module.selected_player = p.name
                            end
                        end
                        imgui.Unindent(15)
                    end
                end
            end
            imgui.Spacing()
        end

        draw_section('Required', res.required)
        draw_section('Suggested', res.suggested)

        imgui.Separator()
        imgui.Separator()

        -- Persistent buffers
        comp_module.uiState.newName = comp_module.uiState.newName or { '' }

        -- Header
        imgui.Text('Unassigned Pool')
        imgui.Separator()

        -- Search Filter
        imgui.InputText('Filter', filterPtr, 64)
        local filter_str = (filterPtr[1] or ''):lower()

        imgui.Separator()

        -- Gather and Sort from Dynamic Party Results
        local display_list = {}
        local source_pool = (comp_module.party_results and comp_module.party_results.unassigned) or res.unassigned

        for _, p in ipairs(source_pool) do
            local text = string.format('%s %s/%s', p.name, p.jobMain, p.jobSub):lower()
            if filter_str == '' or text:find(filter_str) then
                table.insert(display_list, p)
            end
        end
        table.sort(display_list, function(a,b) return a.name < b.name end)

        -- 4. Render List
        imgui.BeginChild('unassigned_list_inner', { 0, 0 }, true) -- Fill remaining height
        for _, p in ipairs(display_list) do
            local label = string.format('%s (%s/%s)', p.name, p.jobMain, p.jobSub)
            local is_selected = (comp_module.selected_player == p.name)

            if imgui.Selectable(label, is_selected) then
                if is_selected then
                    comp_module.selected_player = nil
                else
                    comp_module.selected_player = p.name
                end
            end
        end

        -- Clickable "Blank Space" to Unassign
        -- Use { -1, -1 } to fill remaining content region
        -- Make it transparent/background color
        imgui.PushStyleColor(ImGuiCol_Button, {0,0,0,0})
        imgui.PushStyleColor(ImGuiCol_ButtonHovered, {0,0,0,0})
        imgui.PushStyleColor(ImGuiCol_ButtonActive, {0,0,0,0})
        imgui.PushStyleColor(ImGuiCol_Border, {0,0,0,0})

        if imgui.Button('##drop_unassign', { -1, -1 }) then
             if comp_module.selected_player then
                comp_module.unassign_player(comp_module.selected_player)
                comp_module.selected_player = nil
             end
        end
        imgui.PopStyleColor(4)

        imgui.EndChild()
        imgui.EndChild()

        imgui.SameLine()

        -- RIGHT COLUMN: Parties
        imgui.BeginChild('col_party', { 0, -40 }, true)
        if imgui.Button('Create Alliance') then
            comp_module.add_group(comp_module.currentEvent)
        end
        imgui.Separator()

        if comp_module.party_results and comp_module.party_results.alliances then
            for aIdx, alliance in ipairs(comp_module.party_results.alliances) do
                if aIdx > 1 then imgui.SameLine() end

                -- Use Sub-Window for Alliance
                imgui.BeginChild('alliance_' .. aIdx, { 310, 0 }, true)
                imgui.TextColored({0.4, 0.8, 1.0, 1.0}, alliance.name)
                imgui.SameLine()
                if imgui.SmallButton('Delete##del_all_' .. aIdx) then
                    comp_module.remove_alliance(aIdx)
                end
                imgui.Separator()

                for pIdx, p in ipairs(alliance.parties) do
                    imgui.Text(p.name)
                    for i = 1, 6 do
                        if p.members[i] then
                            local m = p.members[i]
                            if m.empty then
                                -- Empty Slot
                                local label = string.format('%d. [%s] ---##%d-%d-%d', i, m.role, aIdx, pIdx, i)
                                if imgui.Selectable(label) then
                                    if comp_module.selected_player then
                                        comp_module.manual_assign(comp_module.selected_player, aIdx, pIdx, i)
                                        comp_module.selected_player = nil
                                    end
                                end
                            else
                                -- Filled Slot
                                local label = string.format('%d. [%s] %s (%s)##%d-%d-%d', i, m.role, m.name, m.jobMain, aIdx, pIdx, i)
                                local is_selected = (comp_module.selected_player == m.name)
                                if imgui.Selectable(label, is_selected) then
                                    if comp_module.selected_player and comp_module.selected_player ~= m.name then
                                        -- Swap
                                        comp_module.manual_assign(comp_module.selected_player, aIdx, pIdx, i)
                                        comp_module.selected_player = nil
                                    else
                                        -- Select
                                        if is_selected then
                                            comp_module.selected_player = nil
                                        else
                                            comp_module.selected_player = m.name
                                        end
                                    end
                                end
                            end
                        else
                             imgui.TextDisabled(string.format('%d. ---', i))
                        end
                    end
                    imgui.Separator()
                end

                imgui.EndChild() -- End Alliance Window
            end

        else
            imgui.TextDisabled('No parties built yet. Click Create Alliance.')
        end

        imgui.EndChild()

        -- Footer Buttons
        imgui.Separator()

        if imgui.Button('Close') then openPtr[1] = false end
        imgui.SameLine()
        if imgui.Button('Refresh Roster') then
             -- 1. Gather (Async-like but immediate call here)
             if att_module and comp_module.currentEvent then
                att_module.gather_zone(comp_module.currentEvent)
                -- 2. Update Comp Pool
                comp_module.refresh_unassigned(att_module.data)
             end
        end

        imgui.End()
    end

    return openPtr[1]
end

return M
