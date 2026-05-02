-- migrations.lua
-- One-time, idempotent migrations applied to the persisted config table on
-- addon load. Extracted verbatim from att.lua. Each migration is a `do ... end`
-- block guarded by a condition that makes a second run a no-op.
--
-- Caller: att.lua passes the loaded `config` table and a `settings` reference
-- so this module can save when a migration actually changed something.

local M = {}

-- Apply all migrations in order. Safe to call exactly once at addon load.
function M.run(config, settings)
    -- One-time migration from the legacy single-pairing schema (token, linkshellId,
    -- linkshellName, label living directly on config.api) into the pairings list.
    -- Reason: the legacy fields may still exist on disk from a previous version even
    -- though they're no longer in the defaults; preserve the user's pairing instead
    -- of forcing them to /att link again.
    do
        local apiCfg = config.api
        if apiCfg.token and apiCfg.token ~= '' and #(apiCfg.pairings or {}) == 0 then
            apiCfg.pairings = T{
                T{
                    token         = apiCfg.token,
                    linkshellId   = apiCfg.linkshellId or 0,
                    linkshellName = apiCfg.linkshellName or '',
                    label         = apiCfg.label or '',
                    channel       = 1,
                }
            }
            apiCfg.token         = nil
            apiCfg.linkshellId   = nil
            apiCfg.linkshellName = nil
            apiCfg.label         = nil
            settings.save()
        end
    end

    -- One-time migration: bump older 0-valued DKP defaults to the new 1
    -- baseline. The schema default already says 1 for new installs, but
    -- existing settings.xml files written under the previous default of 0
    -- need a nudge so the Settings popup doesn't keep showing 0. Treats 0
    -- as "unset" -- anything else (incl. user-chosen 0) wouldn't match here
    -- because users explicitly setting 0 would still be using the default.
    do
        local d = config.eventDefaults
        if d then
            local migrated = false
            if (d.dkpPerHourRegular or 0) <= 0 then
                d.dkpPerHourRegular = 1
                migrated = true
            end
            if (d.dkpPerWindowHnm or 0) <= 0 then
                d.dkpPerWindowHnm = 1
                migrated = true
            end
            if migrated then settings.save() end
        end
    end
end

return M
