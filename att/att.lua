-- att.lua (Refactored entry point)
-- All addon behavior lives in submodules under att/. This file is now just
-- the addon header, requires, single state-table init, config + api wiring,
-- and the four ashita event registrations -- each of which delegates to a
-- module's entry function.
--
-- Module layout:
--   att/migrations.lua       - one-time config migrations
--   att/state_defaults.lua   - default state table factory
--   att/utils.lua            - small helpers (ls_prefix, broadcasts, etc.)
--   att/commands.lua         - /att, /attend, /comp, /findoffset, /apidump
--   att/text_parser.lua      - text_in HNM defeat / loot detector
--   att/render_pump.lua      - d3d_present orchestrator
--   att/render/poll.lua      - per-frame timer & poll dispatch
--   att/render/callbacks.lua - launcher callback table builder
--   att/render/callbacks_attendance.lua / _event.lua / _loot.lua
--                            - the actual callback bodies, split by domain.

addon.name    = 'att'
addon.author  = 'Nils'
addon.version = '4.1.8'
addon.desc    = 'Attendance manager (Modular)'

require('common')

-- Setup package path to include the current directory (New Att)
-- Assuming this file is in .../att/New Att/
local folderPath = addon.path .. 'New Att\\'
package.path = package.path .. ';' .. folderPath .. '?.lua'

-- External / shared libraries.
local imgui      = require('imgui')
local chat       = require('chat')
local struct     = require('struct')
local resources  = require('resources')
local memory     = require('memory')
local attendance = require('attendance')
local helpers    = require('helpers')
local ui         = require('ui')
local constants  = require('constants')
local comp       = require('comp')
local messages   = require('messages')
local settings   = require('settings')
local api        = require('api')

-- Addon submodules.
local migrations     = require('migrations')
local state_defaults = require('state_defaults')
local utils          = require('utils')
local commands       = require('commands')
local text_parser    = require('text_parser')
local render_pump    = require('render_pump')

local config = settings.load(T{
    api = T{
        baseUrl  = '',
        pairings = T{},
    },
    -- Per-installation DKP defaults applied to events created from the
    -- addon. Edited via the gear-icon settings popup. Persisted to disk
    -- so the values survive /addon reload.
    eventDefaults = T{
        dkpPerHourRegular = 1,
        dkpPerWindowHnm   = 1,
        -- Whether the Queued Events / Active Events rows include the
        -- "(id N)" suffix. Toggle from the Settings popup.
        showEventIds      = false,
    },
    -- User-added monster names that the ToD / loot listener should match
    -- in addition to the built-in HNM and Testing tables. Edited from the
    -- Settings popup. Stored as a list of strings; persisted to disk.
    customMonsters = T{},
})

-- Apply one-time migrations against the persisted config (legacy single-pairing
-- schema, DKP-default 0 -> 1 bumps). Idempotent.
migrations.run(config, settings)

-- Hand the API client a reference to the persisted config block so
-- pair() / unpair() update the same table that gets saved to disk.
api.set_config(config.api)

-- Global state. Single table, initialized once. Modules receive it explicitly.
local state = state_defaults.create()

-- deps table: every dep any submodule might need. Modules destructure what
-- they use at the top of register()/install() functions for readability.
-- Adding a new dep is local to this table, so submodules don't grow new
-- top-level requires of their own.
local deps = {
    imgui      = imgui,
    chat       = chat,
    struct     = struct,
    resources  = resources,
    memory     = memory,
    attendance = attendance,
    helpers    = helpers,
    ui         = ui,
    constants  = constants,
    comp       = comp,
    messages   = messages,
    settings   = settings,
    api        = api,
    utils      = utils,
    config     = config,
}

--------------------------------------------------------------------------------
-- INITIALIZATION
--------------------------------------------------------------------------------
ashita.events.register('load', 'att_load_cb', function()
    resources.load(addon.path)

    -- Alias display labels to existing credit zone entries so the launcher
    -- can show user-friendly names while the scan/lookup still works.
    -- Pattern: { displayName, sourceName }
    local aliases = {
        { 'Sky',                       'Sky/Kirin'    },
        { 'Fafnir/Nidhogg',            'Nidhogg'      },
        { 'Behemoth/King Behemoth',    'King Behemoth'},
        { 'Adamantoise/Aspidochelone', 'Aspidochelone'},
    }
    for _, pair in ipairs(aliases) do
        local newName, src = pair[1], pair[2]
        if resources.attCreditNames and resources.attCreditNames[src] then
            resources.attCreditNames[newName] = resources.attCreditNames[src]
        end
        if resources.attCreditZoneIds and resources.attCreditZoneIds[src] then
            resources.attCreditZoneIds[newName] = resources.attCreditZoneIds[src]
        end
        if resources.attSearchArea and resources.attSearchArea[src] then
            resources.attSearchArea[newName] = resources.attSearchArea[src]
        end
    end
end)

--------------------------------------------------------------------------------
-- COMMAND HANDLERS
--------------------------------------------------------------------------------
-- All chat commands (/att, /attend, /comp, /findoffset, /apidump) are
-- registered inside this call. The original file inlined four separate
-- ashita.events.register('command', ...) blocks; commands.register reproduces
-- them in the same order with identical handler bodies.
commands.register(state, deps)

--------------------------------------------------------------------------------
-- TEXT_IN: HNM defeat-message detector (ToD Capture)
--------------------------------------------------------------------------------
ashita.events.register('text_in', 'att_tod_capture_cb', function(e)
    text_parser.handle(state, deps, e)
end)

--------------------------------------------------------------------------------
-- D3D PRESENT
--------------------------------------------------------------------------------
ashita.events.register('d3d_present', 'att_present_cb', function()
    render_pump.draw(state, deps)
end)
