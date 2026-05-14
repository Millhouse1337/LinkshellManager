-- helpers.lua
local helpers = {}

function helpers.trim(s)
    return (tostring(s or ''):gsub('^%s*(.-)%s*$', '%1'))
end

function helpers.endswith(s, suf)
    s   = tostring(s or '')
    suf = tostring(suf or '')
    return suf == '' or s:sub(-#suf) == suf
end

function helpers.trimend(s, ch)
    s = tostring(s or '')
    local patt = (ch and ch ~= '') and (ch:gsub('(%W)', '%%%1')) or '%s'
    return (s:gsub('[' .. patt .. ']+$', ''))
end

function helpers.strip_colors_if_any(s)
    if type(s) ~= 'string' then return '' end
    if s.strip_colors   then s = s:strip_colors() end
    if s.strip_translate then s = s:strip_translate(true) end
    s = s:gsub('\031.', ''):gsub('\030.', '')
    return s
end

function helpers.clean_str(str)
    if not str then return '' end
    local cm = AshitaCore and AshitaCore:GetChatManager()
    if cm and cm.ParseAutoTranslate then
        str = cm:ParseAutoTranslate(str, true)
    end
    str = helpers.strip_colors_if_any(str)
    while helpers.endswith(str, '\n') or helpers.endswith(str, '\r') do
        str = helpers.trimend(helpers.trimend(str, '\n'), '\r')
    end
    return str:gsub(string.char(0x07), '\n')
end

function helpers.norm(s)
    s = helpers.trim(s or ''):lower()
    s = s:gsub("[%s%-%.'’_]+", "")
    return s
end

function helpers.is_plausible_name(s)
    s = tostring(s or '')
    return #s >= 2
       and #s <= 15
       and (not s:find('[%z\001-\008\011\012\014-\031]'))
end

function helpers.sanitize_name(raw)
    if not raw then return '' end
    raw = raw:gsub('%z+$', ''):gsub('[\r\n]+', '')
    return helpers.trim(raw)
end

-- Scans the in-zone entity table for an entity whose display name matches
-- targetName (exact, case-sensitive — chat names are canonical) and returns
-- its server ID. Used to disambiguate mobs that share a chat-line name but
-- have unique server IDs (e.g. the three Ix'aern variants).
--
-- When multiple matches exist, prefer one with HP <= 0 / nil (just died);
-- fall back to any alive match. Returns nil if nothing found or if the
-- Ashita entity API isn't available on this client.
function helpers.find_mob_id_by_name(targetName)
    if type(targetName) ~= 'string' or targetName == '' then return nil end

    local entMgr
    pcall(function()
        entMgr = AshitaCore:GetMemoryManager():GetEntity()
    end)
    if not entMgr then return nil end

    local matchDead, matchAlive = nil, nil

    -- FFXI's entity table is 0..2303; mobs typically live in 1024..1791.
    -- Iteration is cheap so we scan the full range to avoid missing variant
    -- placements on private servers.
    for i = 0, 2303 do
        local name, sid, hpp
        pcall(function()
            name = entMgr:GetName(i)
            sid  = entMgr:GetServerId(i)
            hpp  = entMgr:GetHPPercent(i)
        end)
        if name == targetName and sid and sid > 0 then
            if not hpp or hpp == 0 then
                matchDead = sid
            elseif not matchAlive then
                matchAlive = sid
            end
        end
    end

    return matchDead or matchAlive
end

function helpers.parse_time_string(timeStr)
    if not timeStr then return 0 end
    local m, s = timeStr:match('^(%d+):(%d+)$')
    if m and s then return tonumber(m) * 60 + tonumber(s) end
    local mins = tonumber(timeStr)
    if mins then return mins * 60 end
    return 0
end

function helpers.clamp_0_99(n)
    n = tonumber(n) or 0
    if n < 0  then return 0 end
    if n > 99 then return 99 end
    return n
end

function helpers.get_next_letter(ch)
    if not ch or ch == '' then return 'A' end
    ch = tostring(ch):upper()
    local b = string.byte(ch)
    if not b or b < 65 or b > 90 then return 'A' end
    b = b + 1
    if b > 90 then b = 65 end
    return string.char(b)
end

return helpers
