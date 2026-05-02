-- ui/settings.lua
-- Extracted from ui.lua (function body byte-for-byte).
local imgui = require('imgui')

local M = {}

-- Settings popup. Draws a small floating window with the per-installation
-- DKP defaults (per-hour for Regular events, per-window for HNM events).
-- Values are pulled from / written back to a config table the caller
-- supplies; the caller is responsible for calling settings.save() when
-- closing so the values persist across addon reloads. Returns the new
-- open-state to mirror the convention used by draw_launcher.
local syncSettingsDkpHourPtr     = { '' }
local syncSettingsDkpWindowPtr   = { '' }
local syncSettingsShowIdsPtr     = { false }
local syncSettingsAddMonsterPtr  = { '' }
local syncSettingsBoundFor       = nil  -- identity tracker so we re-prime
                                         -- the inputs when the window opens

function M.draw(is_open, state, callbacks)
    if not is_open then
        syncSettingsBoundFor = nil
        return false
    end

    local cfg = (callbacks and callbacks.event_defaults) or {}

    -- Re-seed the input ptrs from cfg when the window first opens (or
    -- after a /addon reload). Use a per-frame identity check so live
    -- typing isn't clobbered.
    if syncSettingsBoundFor ~= cfg then
        syncSettingsDkpHourPtr[1]   = tostring(cfg.dkpPerHourRegular or 0)
        syncSettingsDkpWindowPtr[1] = tostring(cfg.dkpPerWindowHnm   or 0)
        syncSettingsShowIdsPtr[1]   = (cfg.showEventIds == true)   -- default false
        syncSettingsBoundFor        = cfg
    end

    imgui.SetNextWindowSize({ 460, 540 }, ImGuiCond_FirstUseEver)
    local openPtr = { is_open }
    if imgui.Begin('att Settings', openPtr) then
        imgui.Text('Default DKP rates for events created from the addon:')
        imgui.Dummy({ 0, 6 })

        -- Both DKP rate inputs share one row: label / input / spacer / label / input.
        imgui.Text('Timed - DKP / Hour')
        imgui.SameLine()
        imgui.PushItemWidth(80)
        imgui.InputText('##settDkpHour', syncSettingsDkpHourPtr, 8)
        imgui.PopItemWidth()
        imgui.SameLine()
        imgui.Dummy({ 16, 0 })
        imgui.SameLine()
        imgui.Text('HNM - DKP / Window')
        imgui.SameLine()
        imgui.PushItemWidth(80)
        imgui.InputText('##settDkpWindow', syncSettingsDkpWindowPtr, 8)
        imgui.PopItemWidth()

        imgui.Dummy({ 0, 10 })
        imgui.Separator()
        imgui.Dummy({ 0, 6 })

        imgui.Text('Display:')
        imgui.SameLine()
        if imgui.Checkbox('Show Event IDs', syncSettingsShowIdsPtr) then
            -- bound directly; nothing else to do
        end

        imgui.Dummy({ 0, 10 })
        imgui.Separator()
        imgui.Dummy({ 0, 6 })

        -- ToD Capture monster list. Built-in entries (HNMs + Testing) are
        -- read-only; user-added entries can be removed inline. Add input
        -- at the bottom; Add button auto-saves to disk so the new entry
        -- survives /addon reload.
        imgui.Text('ToD Capture monsters')
        imgui.TextDisabled('  Built-in monsters and any custom names you add are matched against incoming defeat lines.')

        imgui.BeginChild('settMonstersList', { 0, 200 }, true)

        local builtInHnms     = (callbacks and callbacks.built_in_hnms)    or {}
        local builtInSky      = (callbacks and callbacks.built_in_sky)     or {}
        local customMonsters  = (callbacks and callbacks.custom_monsters)  or {}

        -- Helper: render a list of monster names in a 3-column grid using
        -- imgui.SameLine(<x>) for absolute-X column alignment. Saves a lot
        -- of vertical space versus the previous one-per-row layout.
        local function render_monster_grid(names)
            local COLS    = 3
            local COL_W   = 140  -- pixels per column (fits up to ~16-char names)
            local INDENT  = 12
            for i, n in ipairs(names) do
                local col = (i - 1) % COLS
                if col == 0 then
                    imgui.TextDisabled('  ' .. tostring(n))
                else
                    imgui.SameLine(INDENT + col * COL_W)
                    imgui.TextDisabled(tostring(n))
                end
            end
        end

        -- Built-in HNMs.
        local hnmNames = {}
        for n, _ in pairs(builtInHnms) do hnmNames[#hnmNames + 1] = n end
        table.sort(hnmNames)
        if #hnmNames > 0 then
            imgui.TextColored({ 0.6, 0.85, 1.0, 1.0 }, 'Built-in HNMs:')
            render_monster_grid(hnmNames)
            imgui.Dummy({ 0, 4 })
        end

        -- Built-in Sky-farm NMs.
        local skyNames = {}
        for n, _ in pairs(builtInSky) do skyNames[#skyNames + 1] = n end
        table.sort(skyNames)
        if #skyNames > 0 then
            imgui.TextColored({ 0.7, 1.0, 0.7, 1.0 }, 'Built-in Sky NMs:')
            render_monster_grid(skyNames)
            imgui.Dummy({ 0, 4 })
        end

        -- User-added monsters. Each row gets a Remove button.
        imgui.TextColored({ 1.0, 0.85, 0.4, 1.0 }, 'Custom (user-added):')
        if #customMonsters == 0 then
            imgui.TextDisabled('  (none yet)')
        else
            for i, n in ipairs(customMonsters) do
                imgui.Text('  ' .. tostring(n))
                imgui.SameLine()
                if imgui.Button('Remove##settRemMonster_' .. tostring(i), { 70, 0 }) then
                    if callbacks and callbacks.on_remove_custom_monster then
                        callbacks.on_remove_custom_monster(i)
                    end
                end
            end
        end

        imgui.EndChild()

        -- Add row: text input + Add button. Auto-saves on add.
        imgui.PushItemWidth(220)
        imgui.InputText('##settAddMonster', syncSettingsAddMonsterPtr, 64)
        imgui.PopItemWidth()
        imgui.SameLine()
        if imgui.Button('Add monster##settAddMonsterBtn', { 110, 0 }) then
            local raw = syncSettingsAddMonsterPtr[1] or ''
            if raw:gsub('%s', '') ~= '' then
                if callbacks and callbacks.on_add_custom_monster then
                    callbacks.on_add_custom_monster(raw)
                end
                syncSettingsAddMonsterPtr[1] = ''
            end
        end

        imgui.Dummy({ 0, 12 })
        imgui.Separator()

        if imgui.Button('Save##settSave', { 90, 0 }) then
            cfg.dkpPerHourRegular = tonumber(syncSettingsDkpHourPtr[1])   or 0
            cfg.dkpPerWindowHnm   = tonumber(syncSettingsDkpWindowPtr[1]) or 0
            cfg.showEventIds      = syncSettingsShowIdsPtr[1] and true or false
            if callbacks and callbacks.on_settings_save then
                callbacks.on_settings_save()
            end
            openPtr[1] = false
        end
        imgui.SameLine()
        if imgui.Button('Cancel##settCancel', { 90, 0 }) then
            openPtr[1] = false
        end

        imgui.End()
    end

    return openPtr[1]
end

return M
