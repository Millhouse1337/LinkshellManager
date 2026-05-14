-- ui.lua
-- Public dispatcher. The actual rendering for each window has been moved
-- into focused submodules under att/ui/. Each call here is a thin
-- forwarder so existing callers (att.lua) keep working unchanged:
--
--   ui.draw_attendance_window(is_open, att_module, state, callbacks)
--   ui.draw_launcher          (is_open, state, callbacks)
--   ui.draw_composition_window(is_open, comp_module)
--   ui.draw_settings          (is_open, state, callbacks)

local ui = {}

local attendance_window  = require('ui.attendance_window')
local launcher           = require('ui.launcher')
local composition_window = require('ui.composition_window')
local settings_window    = require('ui.settings')

function ui.draw_attendance_window(is_open, att_module, state, callbacks)
    return attendance_window.draw(is_open, att_module, state, callbacks)
end

function ui.draw_launcher(is_open, state, callbacks)
    return launcher.draw(is_open, state, callbacks)
end

function ui.draw_composition_window(is_open, comp_module)
    return composition_window.draw(is_open, comp_module)
end

function ui.draw_settings(is_open, state, callbacks)
    return settings_window.draw(is_open, state, callbacks)
end

return ui
