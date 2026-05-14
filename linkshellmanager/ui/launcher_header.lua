-- ui/launcher_header.lua
-- Header row of draw_launcher: Web Sync indicator, Linkshells dropdown,
-- top-right Refresh button, Settings button + centered Timezone label,
-- separator. Extracted from ui.lua byte-for-byte.
local imgui = require('imgui')
local api   = require('api')

local M = {}

-- `do_full_refresh` is supplied by the caller because it captures the
-- launcher's local state and the persistent input pointers. Passing it in
-- keeps the section pure UI code.
function M.draw(state, callbacks, do_full_refresh)
    -- Compact mode keeps only the Compact checkbox + Refresh button on the
    -- header row so the launcher reads as a clean stack of section chevrons.
    -- Everything else (Web Sync tag, LS dropdown, Settings, Timezone) is
    -- restored as soon as the user unchecks Compact.
    if state.launcherCompact then
        local REFRESH_BTN_W = 90
        local COMPACT_W     = 100  -- checkbox + label combined width
        local windowWidth = 600
        pcall(function()
            local ww = imgui.GetWindowWidth()
            if type(ww) == 'number' then windowWidth = ww end
        end)
        -- SetCursorPosX (not SameLine) because nothing has been rendered on
        -- this row yet — SameLine with no preceding item is undefined.
        imgui.SetCursorPosX(math.max(8, windowWidth - REFRESH_BTN_W - COMPACT_W - 24))
        local compactPtr = { state.launcherCompact and true or false }
        if imgui.Checkbox('Compact##launcherCompact', compactPtr) then
            state.launcherCompact = compactPtr[1]
        end
        imgui.SameLine()
        if imgui.Button('Refresh##topRefresh', { REFRESH_BTN_W, 0 }) then
            do_full_refresh()
        end
        imgui.Separator()
        return
    end

    -- Web Sync indicator on the far left of the header row (when paired).
    if api.is_paired() then
        imgui.TextColored({ 0.5, 0.85, 1.0, 1.0 }, '[Web Sync Activated]')
        imgui.SameLine()
    end

    -- Linkshells dropdown: lists each paired LS by name with its channel.
    -- Slots without a pairing show "(not paired)" and are non-interactive.
    do
        local p1 = api.get_pairing_by_channel(1)
        local p2 = api.get_pairing_by_channel(2)

        -- Auto-clear a channel flag if its pairing was removed, so we don't
        -- keep trying to /l1 or /l2 on a slot the user has unlinked.
        if not p1 and state.lsChannels.ls1 then state.lsChannels.ls1 = false end
        if not p2 and state.lsChannels.ls2 then state.lsChannels.ls2 = false end

        local label1 = p1 and p1.linkshellName or 'LS1 (not paired)'
        local label2 = p2 and p2.linkshellName or 'LS2 (not paired)'
        local sel = {}
        if state.lsChannels.ls1 then table.insert(sel, p1 and p1.linkshellName or 'LS1') end
        if state.lsChannels.ls2 then table.insert(sel, p2 and p2.linkshellName or 'LS2') end
        local preview = (#sel > 0) and table.concat(sel, ', ') or 'None'

        -- Center the dropdown between the [Web Sync Activated] tag on the
        -- left and the Refresh button on the right by setting cursor X to
        -- (windowWidth - comboWidth) / 2, all on the same header line.
        local COMBO_W = 180
        local windowWidth = 600
        pcall(function()
            local ww = imgui.GetWindowWidth()
            if type(ww) == 'number' then windowWidth = ww end
        end)
        imgui.SameLine(math.max(8, (windowWidth - COMBO_W) / 2))

        imgui.PushItemWidth(COMBO_W)
        if imgui.BeginCombo('##lsCombo', preview) then
            if p1 then
                local pp = { state.lsChannels.ls1 }
                if imgui.Checkbox(label1, pp) then state.lsChannels.ls1 = pp[1] end
            else
                imgui.TextDisabled(label1)
            end
            if p2 then
                local pp = { state.lsChannels.ls2 }
                if imgui.Checkbox(label2, pp) then state.lsChannels.ls2 = pp[1] end
            else
                imgui.TextDisabled(label2)
            end
            imgui.EndCombo()
        end
        imgui.PopItemWidth()
    end

    -- Refresh button at the top right of the launcher header, with a
    -- "Compact" checkbox immediately to its left. The checkbox toggles
    -- state.launcherCompact, which the launcher uses to hide everything
    -- except Attendance / Loot Pool / ToD Capturing.
    do
        local REFRESH_BTN_W = 90
        local COMPACT_W     = 100  -- checkbox + label combined width
        local windowWidth = 600
        pcall(function()
            local ww = imgui.GetWindowWidth()
            if type(ww) == 'number' then windowWidth = ww end
        end)

        imgui.SameLine(windowWidth - REFRESH_BTN_W - COMPACT_W - 24)
        local compactPtr = { state.launcherCompact and true or false }
        if imgui.Checkbox('Compact##launcherCompact', compactPtr) then
            state.launcherCompact = compactPtr[1]
        end

        imgui.SameLine(windowWidth - REFRESH_BTN_W - 16)
        if imgui.Button('Refresh##topRefresh', { REFRESH_BTN_W, 0 }) then
            do_full_refresh()
        end
    end

    -- Settings button on the left + centered Timezone label on the
    -- same row. The button toggles the settings popup; the label is
    -- centered horizontally based on the launcher's current width.
    do
        if imgui.Button('Settings##openSettings', { 80, 0 }) then
            state.isSettingsWindowOpen = not state.isSettingsWindowOpen
        end

        local tzLabel = os.date('%Z') or ''
        if tzLabel == '' or tzLabel:match('^%s*$') then
            local nowLocal = os.time()
            local nowUtc = os.date('!*t', nowLocal)
            local offset = nowLocal - os.time(nowUtc)
            local sign = (offset >= 0) and '+' or '-'
            local abs = math.abs(offset)
            tzLabel = string.format('UTC%s%02d:%02d', sign,
                math.floor(abs / 3600), math.floor((abs % 3600) / 60))
        end
        local tzText = 'Timezone: ' .. tzLabel
        local windowWidth = 600
        pcall(function()
            local ww = imgui.GetWindowWidth()
            if type(ww) == 'number' then windowWidth = ww end
        end)
        local textWidth = 0
        pcall(function()
            local s = imgui.CalcTextSize(tzText)
            if type(s) == 'table' and s[1] then textWidth = s[1] end
        end)
        if textWidth <= 0 then textWidth = #tzText * 7 end
        imgui.SameLine(math.max(96, (windowWidth - textWidth) / 2))
        imgui.TextDisabled(tzText)
    end
    imgui.Separator()
end

return M
