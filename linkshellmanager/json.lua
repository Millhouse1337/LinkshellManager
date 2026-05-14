-- Minimal JSON encoder/decoder for the att addon's API needs.
-- Handles: strings, numbers, booleans, nil/null, arrays (1-indexed), and string-keyed tables.
-- Not a general-purpose library: keeps the addon self-contained without vendoring dkjson.

local json = {}

local NULL = {}
json.null = NULL

-- ---------- Encoder ----------

local function encode_string(s)
    -- Escape per RFC 8259.
    s = s:gsub('\\', '\\\\'):gsub('"', '\\"')
        :gsub('\b', '\\b'):gsub('\f', '\\f')
        :gsub('\n', '\\n'):gsub('\r', '\\r'):gsub('\t', '\\t')
    -- Strip control chars 0x00..0x1F we did not already escape.
    s = s:gsub('[%z\1-\8\11\14-\31]', function(c)
        return string.format('\\u%04x', string.byte(c))
    end)
    return '"' .. s .. '"'
end

local function is_array(t)
    local n = 0
    for k, _ in pairs(t) do
        if type(k) ~= 'number' then return false end
        n = n + 1
    end
    return n == #t
end

local encode_value
encode_value = function(v)
    local tv = type(v)
    if v == NULL then
        return 'null'
    elseif tv == 'nil' then
        return 'null'
    elseif tv == 'boolean' then
        return v and 'true' or 'false'
    elseif tv == 'number' then
        if v ~= v or v == math.huge or v == -math.huge then
            return 'null' -- JSON has no NaN/Inf
        end
        return tostring(v)
    elseif tv == 'string' then
        return encode_string(v)
    elseif tv == 'table' then
        if is_array(v) then
            local parts = {}
            for i = 1, #v do parts[i] = encode_value(v[i]) end
            return '[' .. table.concat(parts, ',') .. ']'
        else
            local parts = {}
            for k, val in pairs(v) do
                if type(k) == 'string' then
                    parts[#parts + 1] = encode_string(k) .. ':' .. encode_value(val)
                end
            end
            return '{' .. table.concat(parts, ',') .. '}'
        end
    end
    return 'null'
end

function json.encode(v)
    return encode_value(v)
end

-- ---------- Decoder ----------

local function skip_ws(s, i)
    local _, e = s:find('^[ \n\r\t]+', i)
    return e and (e + 1) or i
end

local decode_value

local function decode_string(s, i)
    -- s[i] == '"'
    local out = {}
    i = i + 1
    while i <= #s do
        local c = s:sub(i, i)
        if c == '"' then
            return table.concat(out), i + 1
        elseif c == '\\' then
            local n = s:sub(i + 1, i + 1)
            if n == '"' or n == '\\' or n == '/' then
                out[#out + 1] = n
                i = i + 2
            elseif n == 'b' then out[#out + 1] = '\b'; i = i + 2
            elseif n == 'f' then out[#out + 1] = '\f'; i = i + 2
            elseif n == 'n' then out[#out + 1] = '\n'; i = i + 2
            elseif n == 'r' then out[#out + 1] = '\r'; i = i + 2
            elseif n == 't' then out[#out + 1] = '\t'; i = i + 2
            elseif n == 'u' then
                local hex = s:sub(i + 2, i + 5)
                local code = tonumber(hex, 16) or 0
                -- Best-effort UTF-8 encoding for BMP code points.
                if code < 0x80 then
                    out[#out + 1] = string.char(code)
                elseif code < 0x800 then
                    out[#out + 1] = string.char(0xC0 + math.floor(code / 0x40), 0x80 + (code % 0x40))
                else
                    out[#out + 1] = string.char(
                        0xE0 + math.floor(code / 0x1000),
                        0x80 + math.floor(code / 0x40) % 0x40,
                        0x80 + (code % 0x40))
                end
                i = i + 6
            else
                error('Invalid escape at position ' .. i)
            end
        else
            out[#out + 1] = c
            i = i + 1
        end
    end
    error('Unterminated string')
end

local function decode_number(s, i)
    local _, e, num = s:find('^(-?%d+%.?%d*[eE]?[+-]?%d*)', i)
    if not num then error('Invalid number at position ' .. i) end
    return tonumber(num), e + 1
end

local function decode_array(s, i)
    local out = {}
    i = i + 1
    i = skip_ws(s, i)
    if s:sub(i, i) == ']' then return out, i + 1 end
    while true do
        local v
        v, i = decode_value(s, i)
        out[#out + 1] = v
        i = skip_ws(s, i)
        local c = s:sub(i, i)
        if c == ',' then
            i = skip_ws(s, i + 1)
        elseif c == ']' then
            return out, i + 1
        else
            error('Expected , or ] at position ' .. i)
        end
    end
end

local function decode_object(s, i)
    local out = {}
    i = i + 1
    i = skip_ws(s, i)
    if s:sub(i, i) == '}' then return out, i + 1 end
    while true do
        i = skip_ws(s, i)
        if s:sub(i, i) ~= '"' then error('Expected string key at position ' .. i) end
        local key
        key, i = decode_string(s, i)
        i = skip_ws(s, i)
        if s:sub(i, i) ~= ':' then error('Expected : at position ' .. i) end
        i = skip_ws(s, i + 1)
        local val
        val, i = decode_value(s, i)
        out[key] = val
        i = skip_ws(s, i)
        local c = s:sub(i, i)
        if c == ',' then
            i = skip_ws(s, i + 1)
        elseif c == '}' then
            return out, i + 1
        else
            error('Expected , or } at position ' .. i)
        end
    end
end

decode_value = function(s, i)
    i = skip_ws(s, i)
    local c = s:sub(i, i)
    if c == '"' then return decode_string(s, i)
    elseif c == '{' then return decode_object(s, i)
    elseif c == '[' then return decode_array(s, i)
    elseif c == 't' and s:sub(i, i + 3) == 'true' then return true, i + 4
    elseif c == 'f' and s:sub(i, i + 4) == 'false' then return false, i + 5
    elseif c == 'n' and s:sub(i, i + 3) == 'null' then return NULL, i + 4
    elseif c == '-' or (c >= '0' and c <= '9') then return decode_number(s, i)
    end
    error('Unexpected character "' .. tostring(c) .. '" at position ' .. tostring(i))
end

function json.decode(s)
    if type(s) ~= 'string' or #s == 0 then return nil end
    local ok, result = pcall(function()
        local v, _ = decode_value(s, 1)
        return v
    end)
    if not ok then return nil, result end
    return result
end

return json
