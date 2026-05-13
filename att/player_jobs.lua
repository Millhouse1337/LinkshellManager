-- player_jobs.lua
-- Scans the local player's job-level array (one entry per FFXI job id, 0..23)
-- and publishes it to the server via api.post_self_jobs when it changes.
--
-- Indexing convention on the wire: a 24-length JSON array where index i
-- corresponds to FFXI job id i (0 = NONE, 1 = WAR, ..., 22 = RUN, 23 = MON).
-- We mirror this in Lua by filling positions 1..24 of the table; once JSON
-- encoded this becomes [NONE_level, WAR_level, ...].
local api = require('api')

local M = {}

local JOB_ID_COUNT = 24 -- 0..23 inclusive; matches Resources/jobs.csv

-- Cached signature of the last successfully published array so reload-loops
-- and re-scans don't hammer the endpoint when nothing changed.
local last_published_sig = nil
local last_published_name = nil

local function fingerprint(levels)
    local parts = {}
    for i = 1, JOB_ID_COUNT do
        parts[i] = tostring(levels[i] or 0)
    end
    return table.concat(parts, ',')
end

-- Returns a 24-int table (Lua keys 1..24) where index (jobId+1) = level,
-- or nil if the local player can't be read (zoning, not logged in, etc).
function M.scan_self_job_levels()
    local mem = AshitaCore and AshitaCore:GetMemoryManager()
    if not mem then return nil end
    local player = mem:GetPlayer()
    if not player then return nil end

    -- IPlayer:GetJobLevel(jobId) returns the player's level for that job
    -- (0 if unleveled). Guard with pcall in case the binding is missing
    -- on an older Ashita build.
    local levels = {}
    local ok = true
    for jobId = 0, JOB_ID_COUNT - 1 do
        local lvl = 0
        local status = pcall(function()
            lvl = player:GetJobLevel(jobId) or 0
        end)
        if not status then ok = false; break end
        levels[jobId + 1] = lvl
    end
    if not ok then return nil end

    -- Quick sanity check: a logged-in player has SOME job > 0; if the entire
    -- array is zero we're probably reading from a pre-login state. Skip.
    local any = false
    for i = 1, JOB_ID_COUNT do
        if (levels[i] or 0) > 0 then any = true; break end
    end
    if not any then return nil end

    return levels
end

-- Publishes the current job levels to the server if (a) we can read them
-- and (b) the values changed since the last successful publish. No-op
-- otherwise. Safe to call repeatedly (e.g. after each attendance scan).
function M.publish_if_changed()
    local pm = AshitaCore and AshitaCore:GetMemoryManager() and AshitaCore:GetMemoryManager():GetParty()
    local name = pm and pm:GetMemberName(0)
    if not name or name == '' then return end

    local levels = M.scan_self_job_levels()
    if not levels then return end

    local sig = fingerprint(levels)
    if sig == last_published_sig and name == last_published_name then
        return
    end

    local _, err = api.post_self_jobs(name, levels)
    if err then return end

    last_published_sig  = sig
    last_published_name = name
end

-- Allows external callers (e.g. unit-test stubs, /att jobspublish command)
-- to clear the throttle so the next publish_if_changed always fires.
function M.invalidate_cache()
    last_published_sig = nil
    last_published_name = nil
end

return M
