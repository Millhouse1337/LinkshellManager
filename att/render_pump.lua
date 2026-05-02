-- render_pump.lua
-- d3d_present orchestrator. The original att.lua had a single d3d_present
-- handler ~830 lines long; that body is now split across:
--   * render.poll        - per-frame timer / poll dispatch (break-room,
--                          pendingAttend, pendingGather, ...)
--   * render.callbacks   - builds the callbacks table for ui.draw_launcher
--                          (subdivided into callbacks_attendance / _event /
--                          _loot to keep each file under 700 lines).
--
-- M.draw(state, deps) is what att.lua's d3d_present hook calls on every frame.

local poll      = require('render.poll')
local callbacks = require('render.callbacks')

local M = {}

function M.draw(state, deps)
    local ui          = deps.ui
    local comp        = deps.comp
    local attendance  = deps.attendance
    local constants   = deps.constants
    local chat        = deps.chat
    local settings    = deps.settings
    local config      = deps.config

    -- Per-frame timers, polls, and zone-change detection.
    poll.tick(state, deps)

    -- Build the launcher callback table. Same surface and same closure
    -- semantics as the original inline table.
    local cbs = callbacks.build(state, deps)

    -- Standalone Attendance Results window has been merged into the launcher.
    -- Force-suppress it so the /att <name> chat path no longer pops a second window.
    state.isAttendanceWindowOpen = false

    if state.isAttendLauncherOpen then
        state.isAttendLauncherOpen = ui.draw_launcher(state.isAttendLauncherOpen, state, cbs)
    end

    if state.isSettingsWindowOpen then
        -- Hand the settings popup the persisted eventDefaults table plus
        -- a save callback so it can persist edits to disk on Save click.
        -- The custom-monster list is also exposed (with auto-saving add /
        -- remove helpers) so the user can extend the ToD listener without
        -- a code change.
        state.isSettingsWindowOpen = ui.draw_settings(state.isSettingsWindowOpen, state, {
            event_defaults     = config.eventDefaults,
            built_in_hnms      = constants.HNM_WINDOW_COUNTS,
            built_in_sky       = constants.SKY_FARM_NMS,
            built_in_testing   = constants.TESTING_MONSTERS,
            custom_monsters    = config.customMonsters,
            on_add_custom_monster = function(name)
                if type(name) ~= 'string' then return end
                local trimmed = name:gsub('^%s+', ''):gsub('%s+$', '')
                if trimmed == '' then return end
                -- De-dup against itself + the built-in tables (case-insensitive).
                local lower = trimmed:lower()
                if constants.HNM_WINDOW_COUNTS[trimmed] or constants.TESTING_MONSTERS[trimmed] then
                    return
                end
                for _, existing in ipairs(config.customMonsters) do
                    if existing:lower() == lower then return end
                end
                table.insert(config.customMonsters, trimmed)
                settings.save()
            end,
            on_remove_custom_monster = function(index)
                if type(index) ~= 'number' then return end
                if index < 1 or index > #config.customMonsters then return end
                table.remove(config.customMonsters, index)
                settings.save()
            end,
            on_settings_save  = function()
                settings.save()
                print(chat.header('att') .. 'Settings saved.')
            end,
        })
    end

    if comp.isOpen then
        comp.isOpen = ui.draw_composition_window(comp.isOpen, comp, attendance)
    end

    -- Debug window call removed
end

return M
