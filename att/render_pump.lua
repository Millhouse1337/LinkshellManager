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
    local api         = deps.api

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
            built_in_ground    = constants.GROUND_NMS,
            built_in_henms     = constants.HENMS,
            built_in_sea_groups = constants.SEA_NMS_GROUPS,
            built_in_testing   = constants.TESTING_MONSTERS,
            custom_monsters    = config.customMonsters,
            on_add_custom_monster = function(name)
                if type(name) ~= 'string' then return end
                local trimmed = name:gsub('^%s+', ''):gsub('%s+$', '')
                if trimmed == '' then return end
                -- De-dup against itself + the built-in tables (case-insensitive).
                local lower = trimmed:lower()
                if constants.HNM_WINDOW_COUNTS[trimmed]
                    or constants.TESTING_MONSTERS[trimmed]
                    or constants.SKY_FARM_NMS[trimmed]
                    or constants.GROUND_NMS[trimmed]
                    or constants.HENMS[trimmed]
                    or constants.SEA_NMS[trimmed] then
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

                -- If an HNM/Timed event is currently linked in the launcher,
                -- push the just-saved default DKP rate to that event so the
                -- next attendance window posted credits attendees at the
                -- new rate. Server reads eventEntity.DkpPerHour at post
                -- time; without this call it would keep crediting at the
                -- value set when the event was originally created.
                --
                -- HNM (windowed) events use dkpPerWindowHnm; time-based
                -- events use dkpPerHourRegular. Both flow into the same
                -- DkpPerHour column server-side — semantics depend on
                -- event type. No-op when nothing is linked, or when the
                -- linked event has been closed since last refresh.
                local linkedId = state.linkedEventId
                if linkedId and state.webEvents then
                    local linkedEv
                    for _, ev in ipairs(state.webEvents) do
                        if ev.id == linkedId then linkedEv = ev; break end
                    end
                    if linkedEv and linkedEv.isLive then
                        local isHnm = (linkedEv.windowCount or 1) > 1
                        local newRate = isHnm
                            and (config.eventDefaults.dkpPerWindowHnm or 0)
                            or  (config.eventDefaults.dkpPerHourRegular or 0)
                        -- Idempotent: skip the network call when the stored
                        -- value already matches what the user just saved.
                        if (linkedEv.dkpPerHour or 0) ~= newRate then
                            local _, err = api.update_event_dkp(linkedId, newRate)
                            if err then
                                print(chat.header('att') .. 'Event DKP update failed: ' .. tostring(err))
                            else
                                linkedEv.dkpPerHour = newRate
                                print(chat.header('att') .. string.format(
                                    'Linked event DKP updated to %d (%s).',
                                    newRate,
                                    isHnm and 'per window' or 'per hour'))
                            end
                        end
                    end
                end
            end,
        })
    end

    if comp.isOpen then
        comp.isOpen = ui.draw_composition_window(comp.isOpen, comp, attendance)
    end

    -- Debug window call removed
end

return M
