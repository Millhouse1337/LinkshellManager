-- HTTP client + LSManager addon API wrapper for the att addon.
-- Uses curl.exe (shipped with Windows 10 1803+ and Windows 11) instead of LuaSocket
-- because Ashita's bundled Lua does not ship LuaSocket in all builds.
-- All calls are blocking; expect a brief console flash + frame hitch on slow networks.

local json = require('json')

local api = {}

-- Caller-provided settings table { baseUrl, token, linkshellId, linkshellName, label }.
api.config = nil

-- ---------- Internal helpers ----------

local CURL_PATH = (os.getenv('SystemRoot') or 'C:\\Windows') .. '\\System32\\curl.exe'

local function get_url(path)
    if not api.config or not api.config.baseUrl or api.config.baseUrl == '' then
        return nil, 'No web server configured. Use /att server <url> first.'
    end
    local base = api.config.baseUrl:gsub('/+$', '')
    return base .. path
end

local function tmp_dir()
    local base = (os.getenv('TEMP') or os.getenv('TMP') or '.') .. '\\att-addon'
    -- Best-effort mkdir; ignore if it already exists.
    os.execute('mkdir "' .. base .. '" 2>nul')
    return base
end

local function write_file(path, content)
    local f, err = io.open(path, 'wb')
    if not f then return nil, err end
    if content and #content > 0 then f:write(content) end
    f:close()
    return true
end

local function read_file(path)
    local f = io.open(path, 'rb')
    if not f then return '' end
    local body = f:read('*a') or ''
    f:close()
    return body
end

local function delete_file(path)
    pcall(os.remove, path)
end

-- Generates a unique temp filename (no race-safety needed; addon is single-threaded).
local function tmp_name(suffix)
    return tmp_dir() .. '\\' .. tostring(os.time()) .. '-' .. tostring(math.random(100000, 999999)) .. (suffix or '.tmp')
end

local function shell_escape(s)
    -- Wrap in double quotes; escape any embedded double quotes.
    return '"' .. tostring(s):gsub('"', '\\"') .. '"'
end

-- Builds and runs a curl invocation. Returns (status_code, body_string, error_string).
local function curl_request(method, url, headers, body_string)
    -- Verify curl exists.
    local probe = io.open(CURL_PATH, 'rb')
    if not probe then
        return nil, '', 'curl.exe not found at ' .. CURL_PATH
            .. '. On Windows 10 build 1803+ and Windows 11 it ships in System32. '
            .. "Run 'where curl' in cmd to find your copy."
    end
    probe:close()

    local body_file = body_string and tmp_name('-req.json') or nil
    local resp_file = tmp_name('-resp.txt')
    local err_file  = tmp_name('-err.txt')

    if body_file then
        local ok, err = write_file(body_file, body_string)
        if not ok then return nil, '', 'temp file: ' .. tostring(err) end
    end

    -- Wrap the full command in an outer pair of double quotes because cmd /c
    -- (which io.popen invokes) strips the outermost quote pair otherwise.
    local parts = {
        shell_escape(CURL_PATH),
        '-sS',                                   -- silent but show errors on stderr
        '-X', method,
        '-o', shell_escape(resp_file),
        '-w', '"%{http_code}"',                  -- write status code to stdout
        '--max-time', '15'
    }
    for name, value in pairs(headers or {}) do
        parts[#parts + 1] = '-H'
        parts[#parts + 1] = shell_escape(name .. ': ' .. value)
    end
    if body_file then
        parts[#parts + 1] = '--data-binary'
        parts[#parts + 1] = shell_escape('@' .. body_file)
    end
    parts[#parts + 1] = shell_escape(url)
    parts[#parts + 1] = '2>'
    parts[#parts + 1] = shell_escape(err_file)

    local cmd = '"' .. table.concat(parts, ' ') .. '"'
    local pipe = io.popen(cmd, 'r')
    if not pipe then
        if body_file then delete_file(body_file) end
        delete_file(resp_file); delete_file(err_file)
        return nil, '', 'io.popen failed for curl invocation.'
    end
    local stdout = pipe:read('*a') or ''
    pipe:close()

    local body   = read_file(resp_file)
    local stderr = read_file(err_file)
    if body_file then delete_file(body_file) end
    delete_file(resp_file); delete_file(err_file)

    -- Note: string.gsub returns (result, count) — wrap separately so the count
    -- doesn't get passed as tonumber's base argument.
    local cleaned = (stdout:match('(%d%d%d)%s*$') or stdout):gsub('%s', '')
    local status = tonumber(cleaned)
    if not status then
        local hint = (#stderr > 0) and (' stderr: ' .. stderr:gsub('%s+$', ''))
                                    or  (' stdout: <' .. tostring(stdout) .. '>')
        return nil, body, 'curl returned no HTTP status.' .. hint
    end
    -- curl reports status 000 when no HTTP response was received at all (DNS fail,
    -- connection refused, TLS handshake failure, etc.). Surface stderr so the
    -- actual reason appears in chat instead of an opaque "HTTP 0".
    if status == 0 then
        local hint = (#stderr > 0) and (' ' .. stderr:gsub('%s+$', ''):gsub('curl: ', ''))
                                    or  ' (no stderr; check that the URL is reachable)'
        return nil, body, 'No response from server.' .. hint
    end
    return status, body, nil
end

local function request(method, path, body_table)
    local url, err = get_url(path)
    if not url then return nil, err end

    local body_string = body_table and json.encode(body_table) or nil

    local headers = {
        ['Accept'] = 'application/json',
        ['User-Agent'] = 'att-addon/4.1.8'
    }
    if api.config and api.config.token and api.config.token ~= '' then
        headers['Authorization'] = 'Bearer ' .. api.config.token
    end
    if body_string then
        headers['Content-Type'] = 'application/json'
    end

    local status, body, cerr = curl_request(method, url, headers, body_string)
    if not status then
        return nil, cerr or 'Network error'
    end

    local decoded
    if body and #body > 0 then
        decoded = json.decode(body)
    end

    if status >= 200 and status < 300 then
        return decoded or {}, nil, status
    end

    local err_msg = 'HTTP ' .. status
    if decoded and decoded.error then
        err_msg = err_msg .. ': ' .. tostring(decoded.error)
    elseif body and #body > 0 then
        err_msg = err_msg .. ': ' .. body:sub(1, 200)
    end
    return nil, err_msg, status
end

-- ---------- Public API (unchanged) ----------

function api.set_config(cfg) api.config = cfg end

function api.set_base_url(url)
    if not api.config then return end
    api.config.baseUrl = (url or ''):gsub('/+$', '')
end

function api.is_paired()
    return api.config and api.config.token and api.config.token ~= ''
end

function api.unpair()
    if not api.config then return end
    api.config.token = ''
    api.config.linkshellId = nil
    api.config.linkshellName = ''
    api.config.label = ''
end

function api.pair(code)
    local result, err = request('POST', '/api/addon/pair', { code = code })
    if not result then return nil, err end
    if not api.config then return nil, 'No config table provided.' end
    api.config.token = result.token
    api.config.linkshellId = result.linkshellId
    api.config.linkshellName = result.linkshellName or ''
    api.config.label = result.label or ''
    return result
end

function api.me()
    return request('GET', '/api/addon/me')
end

function api.list_events()
    local result, err = request('GET', '/api/addon/events')
    if not result then return nil, err end
    return result.events or {}
end

function api.create_event(name, etype, location)
    return request('POST', '/api/addon/events', {
        name = name,
        type = etype,
        location = location
    })
end

function api.start_event(eventId)
    return request('POST', '/api/addon/events/' .. tostring(eventId) .. '/start')
end

-- entries: array of { characterName, mainJob, subJob, zone }
function api.post_attendance(eventId, entries)
    return request('POST', '/api/addon/events/' .. tostring(eventId) .. '/attendance', {
        recordedAtUtc = os.date('!%Y-%m-%dT%H:%M:%SZ'),
        entries = entries
    })
end

return api
