-- items.lua
-- Item -> equippable-jobs lookup, backed by Ashita's resource manager.
-- Memoized per-item-name so the loot panel can call this every frame for
-- every drop row without recomputing the bitmask decode each time.
--
-- API: items.jobs_for(itemName) -> { 'WAR','PLD','DRK', ... } | nil
--   nil  = item not found OR item has no equip mask (consumable, key item,
--          currency, seal, etc).
local resources = require('resources')

local M = {}

local cache = {}        -- [lower-cased item name] = { jobs..., __none = true }
local ALL_JOBS_MASK = 0 -- computed lazily from resources.attJobList

-- FFXI's item.Jobs field is a 32-bit bitmask where bit N corresponds to the
-- job with internal id N (bit 0 = NONE/all-jobs marker but in practice
-- equippable items set bit 1..22 to mark WAR/MNK/.../RUN). The decoded list
-- is a stable, sorted (by job id) array of three-letter abbreviations from
-- Resources/jobs.csv.
local function decode_jobs_mask(mask)
    if not mask or mask == 0 then return nil end
    local out = {}
    -- Iterate through known job ids (1..23). Skip id 0 (NONE).
    for jobId = 1, 23 do
        local bit = 2 ^ jobId
        -- bitwise AND emulated with math.floor and modulo for Lua 5.1
        -- compatibility (Ashita uses Lua 5.1).
        if math.floor(mask / bit) % 2 >= 1 then
            local abbrev = resources.attJobList[jobId]
            if abbrev and abbrev ~= 'NONE' then
                out[#out + 1] = abbrev
            end
        end
    end
    if #out == 0 then return nil end
    return out
end

local function compute_all_jobs_mask()
    if ALL_JOBS_MASK ~= 0 then return ALL_JOBS_MASK end
    local m = 0
    for jobId = 1, 23 do
        if resources.attJobList[jobId] and resources.attJobList[jobId] ~= 'NONE' then
            m = m + (2 ^ jobId)
        end
    end
    ALL_JOBS_MASK = m
    return m
end

-- Returns the list of three-letter job abbreviations that can equip the
-- named item, or nil if no equip restriction applies (non-equip item or
-- unknown to the resource manager).
function M.jobs_for(itemName)
    if type(itemName) ~= 'string' or itemName == '' then return nil end
    local key = itemName:lower()
    local hit = cache[key]
    if hit ~= nil then
        return hit.__none and nil or hit
    end

    local rm = AshitaCore and AshitaCore:GetResourceManager()
    if not rm then
        cache[key] = { __none = true }
        return nil
    end

    -- Try the case-insensitive name lookup first; fall back to log-name
    -- variant (which uses the singular form on some items).
    local item = nil
    local status = pcall(function()
        -- Args: (name, languageId). 0 = English on Ashita.
        item = rm:GetItemByName(itemName, 0)
        if not item then
            item = rm:GetItemByName(itemName, 2)
        end
    end)
    if not status or not item then
        cache[key] = { __none = true }
        return nil
    end

    local mask = 0
    pcall(function() mask = item.Jobs or 0 end)

    -- Some items (food, currency, seals, gil, key items) have Jobs == 0 OR
    -- the full-jobs sentinel (every bit set on jobs 1..22). Treat the
    -- all-jobs sentinel as "no useful filter" so the UI hides the row.
    if mask == 0 or mask == compute_all_jobs_mask() then
        cache[key] = { __none = true }
        return nil
    end

    local list = decode_jobs_mask(mask)
    if not list then
        cache[key] = { __none = true }
        return nil
    end

    cache[key] = list
    return list
end

-- Clears the memo cache. Call after a /reload of resources or for testing.
function M.invalidate_cache()
    cache = {}
end

return M
