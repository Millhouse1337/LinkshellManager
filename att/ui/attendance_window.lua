-- ui/attendance_window.lua
-- Extracted from ui.lua (function body byte-for-byte).
local imgui     = require('imgui')
local helpers   = require('helpers')
local resources = require('resources')

local M = {}

-- Color used to highlight the local player's row in any roster table.
local SELF_COLOR = { 1.0, 0.85, 0.3, 1.0 } -- warm gold

local function get_self_name()
    local pm = AshitaCore and AshitaCore:GetMemoryManager() and AshitaCore:GetMemoryManager():GetParty()
    return pm and pm:GetMemberName(0) or nil
end

local function is_self_row(row, selfKey)
    if not selfKey then return false end
    return (row.name or ''):gsub('^X%s+', ''):lower() == selfKey
end

function M.draw(is_open, att_module, state, callbacks)
    if not is_open then return false end

    imgui.SetNextWindowSize({ 1050, 600 }, ImGuiCond_FirstUseEver)

    local openPtr = { is_open }
    if imgui.Begin('Attendance Results', openPtr) then

        imgui.Text('Select Mode:')
        imgui.SameLine()
        if imgui.RadioButton('HNM', state.selectedMode == 'HNM') then
            state.selectedMode = 'HNM'
        end
        imgui.SameLine()
        if imgui.RadioButton('Event', state.selectedMode == 'Event') then
            state.selectedMode = 'Event'
        end

        imgui.Separator()
        imgui.Text('Attendance for: ' .. (state.pendingEventName or ''))

        local znames = resources.attCreditNames[state.pendingEventName]
        imgui.Text('Zone: ' .. (((znames and #znames > 0) and table.concat(znames, ', ')) or 'UnknownZone'))
        imgui.Separator()

        imgui.Text('Attendees: ' .. #att_module.data)

        if imgui.Button('Party Only') then
            if callbacks.on_party_only then callbacks.on_party_only() end
        end
        imgui.SameLine()

        if imgui.Button('Rescan') then
            att_module.gather_zone(state.pendingEventName)
        end
        imgui.SameLine()

        -- Scan Letter logic
        if not state.scanNextLetter then
             -- Simple heuristic if none set
             local last = att_module.data[#att_module.data]
             local ch = (last and last.name) and last.name:match('^X?%s*(%a)') or 'A'
             state.scanNextLetter = ch:upper()
        end

        if imgui.Button('Scan ' .. state.scanNextLetter) then
             if callbacks.on_scan_letter then callbacks.on_scan_letter(state.scanNextLetter) end
             state.scanNextLetter = helpers.get_next_letter(state.scanNextLetter)
        end

        imgui.Separator()

        imgui.BeginChild('att_list', { 0, -50 }, true)
        local selfKey = (get_self_name() or ''):lower()
        if selfKey == '' then selfKey = nil end
        local i = 1
        while i <= #att_module.data do
            local r = att_module.data[i]
            if imgui.Button('Remove##' .. i) then
                table.remove(att_module.data, i)
            else
                imgui.SameLine()
                local line = string.format('%s (%s | %s/%s)', r.name, r.zone, r.jobsMain, r.jobsSub)
                if is_self_row(r, selfKey) then
                    imgui.TextColored(SELF_COLOR, line)
                else
                    imgui.Text(line)
                end
                i = i + 1
            end
        end
        imgui.EndChild()
        imgui.Separator()

        if imgui.Button('Write') then
             if callbacks.on_write then callbacks.on_write(false) end
        end
        imgui.SameLine()
        if imgui.Button('Write & Close') then
             if callbacks.on_write then callbacks.on_write(true) end
             openPtr[1] = false
        end
        imgui.SameLine()
        if imgui.Button('Cancel') then
            openPtr[1] = false
        end

        imgui.End()
    end

    return openPtr[1]
end

return M
