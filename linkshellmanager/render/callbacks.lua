-- render/callbacks.lua
-- Builds the callbacks table that ui.draw_launcher consumes. Each launcher
-- callback is implemented in a sibling module (callbacks_attendance,
-- callbacks_event, callbacks_loot) and installed onto a single shared table.
-- The original att.lua built this table inline inside the d3d_present hook;
-- this aggregator preserves the same surface (same field names, same call
-- signatures, same closure-captured deps) without changing any behavior.

local attendance_cbs = require('render.callbacks_attendance')
local event_cbs      = require('render.callbacks_event')
local loot_cbs       = require('render.callbacks_loot')

local M = {}

function M.build(state, deps)
    local out = {
        -- Persisted settings the launcher reads directly (DKP rates etc.)
        event_defaults = deps.config.eventDefaults,
    }
    attendance_cbs.install(out, state, deps)
    event_cbs.install(out, state, deps)
    loot_cbs.install(out, state, deps)
    return out
end

return M
