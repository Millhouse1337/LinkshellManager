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
-- Tracks whether the settings window has been snapped to its preferred
-- size this open. Reset when the window closes so the next open re-snaps,
-- overriding any imgui.ini-saved size from a previous addon version.
local syncSettingsSizeSnapped    = false

function M.draw(is_open, state, callbacks)
    if not is_open then
        syncSettingsBoundFor = nil
        syncSettingsSizeSnapped = false
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

    -- Use ImGuiCond_Always on the first frame of each open so the preferred
    -- size beats any value persisted in imgui.ini by a previous addon
    -- version; subsequent frames let the user drag-resize freely.
    if not syncSettingsSizeSnapped then
        imgui.SetNextWindowSize({ 500, 700 }, ImGuiCond_Always)
        syncSettingsSizeSnapped = true
    else
        imgui.SetNextWindowSize({ 500, 700 }, ImGuiCond_FirstUseEver)
    end

    -- Settings-window opacity (driven by its own slider further down in
    -- the body). Mirror the main launcher's full-mode fade so the body
    -- and title bar dim while widgets stay readable. Only push when the
    -- user has actually dialed below 1.0; at full opacity we leave the
    -- Ashita theme untouched.
    local settingsStylePushed = false
    if (state.settingsAlpha or 1.0) < 1.0 then
        local mult = state.settingsAlpha or 1.0
        if mult < 0 then mult = 0 end
        imgui.PushStyleColor(ImGuiCol_WindowBg,          { 0.06, 0.06, 0.06, 0.94 * mult })
        imgui.PushStyleColor(ImGuiCol_ChildBg,           { 0.06, 0.06, 0.06, 0.30 * mult })
        imgui.PushStyleColor(ImGuiCol_TitleBg,           { 0.55, 0.18, 0.18, 1.00 * mult })
        imgui.PushStyleColor(ImGuiCol_TitleBgActive,     { 0.65, 0.22, 0.22, 1.00 * mult })
        imgui.PushStyleColor(ImGuiCol_TitleBgCollapsed,  { 0.55, 0.18, 0.18, 0.75 * mult })
        imgui.PushStyleColor(ImGuiCol_Border,            { 0.40, 0.40, 0.40, 0.50 * mult })
        settingsStylePushed = true
    end

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

        -- Per-window opacity sliders. Live-bound to state — moving the
        -- slider takes effect the same frame, no Save required. Main and
        -- Settings clamp 0..1 since their defaults are fully opaque;
        -- Compact goes up to 2 because its defaults are already faded.
        imgui.Text('Window Opacity')
        imgui.TextDisabled('  Drag a slider to fade that window. 1.00 = default look.')

        local function alpha_slider(label, key, lo, hi)
            local cur = state[key] or 1.0
            local ptr = { cur }
            imgui.Text(label)
            imgui.SameLine(160)
            imgui.PushItemWidth(-8)
            if imgui.SliderFloat('##alpha_' .. key, ptr, lo, hi, '%.2f') then
                state[key] = ptr[1]
            end
            imgui.PopItemWidth()
        end

        alpha_slider('Main window',     'launcherMainAlpha',    0.0, 1.0)
        alpha_slider('Compact window',  'launcherCompactAlpha', 0.0, 1.0)
        alpha_slider('Settings window', 'settingsAlpha',        0.0, 1.0)

        imgui.Dummy({ 0, 4 })
        if imgui.Button('Reset to default##opacityReset', { 160, 0 }) then
            state.launcherMainAlpha    = 1.0
            state.launcherCompactAlpha = 1.0
            state.settingsAlpha        = 1.0
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

        imgui.BeginChild('settMonstersList', { 0, 380 }, true)

        local builtInHnms        = (callbacks and callbacks.built_in_hnms)        or {}
        local builtInSky         = (callbacks and callbacks.built_in_sky)         or {}
        local builtInGround      = (callbacks and callbacks.built_in_ground)      or {}
        local builtInHenms       = (callbacks and callbacks.built_in_henms)       or {}
        local builtInSeaGroups   = (callbacks and callbacks.built_in_sea_groups)  or {}
        local customMonsters     = (callbacks and callbacks.custom_monsters)      or {}

        -- Helper: render a list of monster names in an absolute-X column
        -- grid. Column width is sized to the widest entry in *this* list so
        -- long names like "Tonberry Sovereign" don't bleed into the next
        -- column, and column count is capped to whatever fits in the child
        -- region (so narrow categories with long names still render cleanly).
        local function render_monster_grid(names)
            if #names == 0 then return end

            local INDENT  = 12
            local PAD     = 48   -- horizontal gap between columns
            local CHAR_PX = 10   -- per-char fallback (capital-heavy safe)

            -- Widest name in the list. Use the larger of imgui.CalcTextSize
            -- (when the binding returns one) and a per-char estimate, so we
            -- never undersize for capital-heavy names like Tonberry
            -- Sovereign — the fallback has historically been the controlling
            -- value because some host bindings of CalcTextSize return 0.
            local maxW = 0
            for _, n in ipairs(names) do
                local imguiW = 0
                pcall(function()
                    local s = imgui.CalcTextSize(tostring(n))
                    if type(s) == 'table' and s[1] then imguiW = s[1] end
                end)
                local conservativeW = #tostring(n) * CHAR_PX
                local w = math.max(imguiW, conservativeW)
                if w > maxW then maxW = w end
            end
            local colW = math.floor(maxW + PAD)

            -- Pick column count from the available content width. Fallback
            -- (600) tracks the current settings-window content width so we
            -- still end up multi-column even when GetContentRegionAvail
            -- isn't available on the host imgui binding.
            local availW = 600
            pcall(function()
                local r = imgui.GetContentRegionAvail()
                if type(r) == 'table' and r[1] and r[1] > 0 then availW = r[1] end
            end)
            local cols = math.floor((availW - INDENT) / colW)
            if cols < 2 and #names >= 2 then cols = 2 end
            if cols > 4 then cols = 4 end

            for i, n in ipairs(names) do
                local col = (i - 1) % cols
                if col == 0 then
                    imgui.TextDisabled('  ' .. tostring(n))
                else
                    imgui.SameLine(INDENT + col * colW)
                    imgui.TextDisabled(tostring(n))
                end
            end
        end

        -- Helper: renders two pre-bucketed name lists as fixed left + right
        -- columns. Used by the Sky NMs section to keep the Gods visually
        -- separated from farm pops without relying on alphabetical sort.
        -- Column width is sized to the widest name in either list.
        local function render_two_column_grid(leftNames, rightNames)
            local INDENT = 12
            local PAD    = 48

            local function measure(n)
                local imguiW = 0
                pcall(function()
                    local s = imgui.CalcTextSize(tostring(n))
                    if type(s) == 'table' and s[1] then imguiW = s[1] end
                end)
                local conservativeW = #tostring(n) * 10
                return math.max(imguiW, conservativeW)
            end

            local maxLeftW = 0
            for _, n in ipairs(leftNames) do
                local w = measure(n)
                if w > maxLeftW then maxLeftW = w end
            end
            local rightStartX = INDENT + maxLeftW + PAD

            local rows = math.max(#leftNames, #rightNames)
            for i = 1, rows do
                local left  = leftNames[i]
                local right = rightNames[i]
                if left then
                    imgui.TextDisabled('  ' .. tostring(left))
                else
                    -- Reserve a row even when only the right column has
                    -- content (defensive — current data has more left than
                    -- right entries, so this branch isn't normally hit).
                    imgui.Dummy({ 1, 0 })
                end
                if right then
                    imgui.SameLine(rightStartX)
                    imgui.TextDisabled(tostring(right))
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

        -- Built-in Sky NMs. Split into two fixed columns so the Gods stay
        -- visually separated from the farm pops — players reading the panel
        -- shouldn't have to scan across alphabetical rows to tell "Kirin"
        -- (a god) apart from "Mother Globe" (a farm pop).
        local skyAllNames = {}
        for n, _ in pairs(builtInSky) do skyAllNames[#skyAllNames + 1] = n end
        if #skyAllNames > 0 then
            local SKY_GODS = {
                Seiryu = true, Suzaku = true, Byakko = true,
                Genbu = true,  Kirin  = true
            }
            local skyFarmNames = {}
            local skyGodNames  = {}
            for _, n in ipairs(skyAllNames) do
                if SKY_GODS[n] then
                    skyGodNames[#skyGodNames + 1] = n
                else
                    skyFarmNames[#skyFarmNames + 1] = n
                end
            end
            table.sort(skyFarmNames)
            table.sort(skyGodNames)

            imgui.TextColored({ 0.7, 1.0, 0.7, 1.0 }, 'Built-in Sky NMs:')
            render_two_column_grid(skyFarmNames, skyGodNames)
            imgui.Dummy({ 0, 4 })
        end

        -- Built-in Sea NMs. Rendered as one flat alphabetical grid — the
        -- per-tier grouping in constants.SEA_NMS_GROUPS is preserved as the
        -- source of truth for the parser, but the Settings panel pools the
        -- names into a single list so the layout matches every other section.
        if #builtInSeaGroups > 0 then
            local seaNames = {}
            for _, group in ipairs(builtInSeaGroups) do
                if group.names then
                    for _, n in ipairs(group.names) do
                        seaNames[#seaNames + 1] = n
                    end
                end
            end
            table.sort(seaNames)
            if #seaNames > 0 then
                imgui.TextColored({ 0.65, 0.85, 1.0, 1.0 }, 'Built-in Sea NMs:')
                render_monster_grid(seaNames)
                imgui.Dummy({ 0, 4 })
            end
        end

        -- Built-in Other NMs (Bloodsucker, Simurgh, Arthros, ...). Sourced
        -- from constants.GROUND_NMS — the variable name kept the historical
        -- "ground" wording even though the visible header is "Other".
        local groundNames = {}
        for n, _ in pairs(builtInGround) do groundNames[#groundNames + 1] = n end
        table.sort(groundNames)
        if #groundNames > 0 then
            imgui.TextColored({ 1.0, 0.8, 0.6, 1.0 }, 'Built-in Other NMs:')
            render_monster_grid(groundNames)
            imgui.Dummy({ 0, 4 })
        end

        -- Built-in HENMs (Overlord Arthro, Ruinous Rocs, Sacred Scorpions, ...).
        local henmNames = {}
        for n, _ in pairs(builtInHenms) do henmNames[#henmNames + 1] = n end
        table.sort(henmNames)
        if #henmNames > 0 then
            imgui.TextColored({ 1.0, 0.7, 0.85, 1.0 }, 'Built-in HENMs:')
            render_monster_grid(henmNames)
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

    -- Balance the 6 PushStyleColor calls made for settings-window fade.
    -- Must run regardless of Begin's return so the style stack stays sane
    -- when the window is collapsed.
    if settingsStylePushed then
        imgui.PopStyleColor(6)
    end

    return openPtr[1]
end

return M
