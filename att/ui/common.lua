-- ui/common.lua
-- Shared helpers / constants used by multiple ui submodules.
local M = {}

-- Color used to highlight the local player's row in any roster table.
M.SELF_COLOR = { 1.0, 0.85, 0.3, 1.0 } -- warm gold

function M.get_self_name()
    local pm = AshitaCore and AshitaCore:GetMemoryManager() and AshitaCore:GetMemoryManager():GetParty()
    return pm and pm:GetMemberName(0) or nil
end

function M.is_self_row(row, selfKey)
    if not selfKey then return false end
    return (row.name or ''):gsub('^X%s+', ''):lower() == selfKey
end

return M
