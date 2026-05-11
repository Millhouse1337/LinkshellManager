-- HTTP client + LSManager addon API wrapper for the att addon.
-- Uses curl.exe (shipped with Windows 10 1803+ and Windows 11) instead of LuaSocket
-- because Ashita's bundled Lua does not ship LuaSocket in all builds.
-- All calls are blocking; expect a brief console flash + frame hitch on slow networks.

local json = require('json')

local api = {}

-- Caller-provided settings table:
--   {
--     baseUrl  = '',
--     pairings = {                                          -- Model A multi-LS auth
--       { token, linkshellId, linkshellName, label, channel },  -- channel = 1 or 2
--       ...
--     },
--   }
-- Authenticated requests use the "primary" pairing (channel 1 preferred, else first
-- in the list). Per-pairing routing for events/attendance is Phase 2.
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
    local primary = api.get_primary_pairing and api.get_primary_pairing() or nil
    if primary and primary.token and primary.token ~= '' then
        headers['Authorization'] = 'Bearer ' .. primary.token
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

    -- Only index `decoded.error` when decoded is actually a table. JSON bodies
    -- can legally decode to a number or string primitive, and Sugar attaches a
    -- strict-index metatable to numbers/strings that errors on unknown keys
    -- ("Math namespace does not contain a definition for: error").
    local err_msg = 'HTTP ' .. status
    if type(decoded) == 'table' and decoded.error then
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

-- Hits /api/addon/me on the configured server to verify it's reachable and
-- exposes the LSManager addon route table. Returns (true, status_code) on
-- any HTTP response (server is reachable; the actual status code indicates
-- auth state rather than reachability). Returns (false, error_message) on
-- network failure or HTTP 404 (URL is up but not an LSManager server).
function api.probe()
    local _, err, status = request('GET', '/api/addon/me')
    if status == nil then
        return false, err or 'no response'
    end
    if status == 404 then
        return false, 'HTTP 404 - URL is reachable but does not look like an LSManager server.'
    end
    return true, status
end

-- Returns the list of paired linkshells. Always returns a table (possibly empty).
function api.list_pairings()
    if not api.config or not api.config.pairings then return {} end
    return api.config.pairings
end

-- Returns the pairing on the given in-game channel (1 or 2), or nil.
function api.get_pairing_by_channel(channel)
    for _, p in ipairs(api.list_pairings()) do
        if p.channel == channel then return p end
    end
    return nil
end

-- The "primary" pairing used for event/attendance ops:
-- channel 1 if paired, else first in list, else nil.
function api.get_primary_pairing()
    return api.get_pairing_by_channel(1) or api.list_pairings()[1]
end

function api.is_paired()
    return #api.list_pairings() > 0
end

-- Removes a pairing. channel = 1, 2, or nil/'all' to remove every pairing.
function api.unpair(channel)
    if not api.config or not api.config.pairings then return end
    if channel == nil or channel == 'all' then
        for k in pairs(api.config.pairings) do api.config.pairings[k] = nil end
        return
    end
    channel = tonumber(channel)
    for i = #api.config.pairings, 1, -1 do
        if api.config.pairings[i].channel == channel then
            table.remove(api.config.pairings, i)
        end
    end
end

-- Adds (or replaces) a pairing on the given in-game channel (1 or 2, defaults 1).
-- Calls the server to redeem the pair code; on success writes the pairing into config.
function api.pair(code, channel)
    if not api.config then return nil, 'No config table provided.' end
    channel = tonumber(channel) or 1
    if channel ~= 1 and channel ~= 2 then return nil, 'Channel must be 1 or 2.' end

    local result, err = request('POST', '/api/addon/pair', { code = code })
    if not result then return nil, err end

    local pairing = {
        token         = result.token,
        linkshellId   = result.linkshellId,
        linkshellName = result.linkshellName or '',
        label         = result.label or '',
        channel       = channel,
    }

    if not api.config.pairings then api.config.pairings = {} end
    -- Replace existing pairing on the same channel if any.
    for i, p in ipairs(api.config.pairings) do
        if p.channel == channel then
            api.config.pairings[i] = pairing
            return pairing
        end
    end
    table.insert(api.config.pairings, pairing)
    return pairing
end

function api.me()
    return request('GET', '/api/addon/me')
end

function api.list_events()
    local result, err = request('GET', '/api/addon/events')
    if not result then return nil, err end
    return result.events or {}
end

function api.create_event(name, etype, location, dkpPerHour, windowCount)
    return request('POST', '/api/addon/events', {
        name        = name,
        type        = etype,
        location    = location,
        dkpPerHour  = tonumber(dkpPerHour),
        -- Optional. Set by the launcher's "Claim/Kill" style so user-named
        -- events get the 2-post UI without the server having to recognise
        -- the name. Server treats any value <=1 as "no override".
        windowCount = tonumber(windowCount)
    })
end

function api.start_event(eventId)
    return request('POST', '/api/addon/events/' .. tostring(eventId) .. '/start')
end

-- Ends a running event. Server writes EventHistory + DkpLedgerEntry rows
-- and removes the live Event / Jobs / participants / loot details. Returns
-- { eventId, eventName } on success.
function api.end_event(eventId)
    return request('POST', '/api/addon/events/' .. tostring(eventId) .. '/end')
end

-- Cancels (deletes) a queued event that hasn't started yet. Server returns 400
-- if the event is already live.
function api.cancel_event(eventId)
    return request('DELETE', '/api/addon/events/' .. tostring(eventId))
end

-- Break-room support. Mirrors the activity flow: any participant can act on
-- themselves; only token issuers with CanModerateLiveEvent can act on others
-- or verify/deny pending self-returns. The server enforces both rules — these
-- helpers just shape the request bodies.

-- Returns { canModerateLiveEvent, participants } where each participant has
-- id / characterName / jobName / subJobName / isOnBreak / pauseTime / resumeTime
-- / isSelf / pendingReturnLedgerId / pendingReturnAt.
function api.list_participants(eventId)
    return request('GET', '/api/addon/events/' .. tostring(eventId) .. '/participants')
end

function api.take_break(eventId, participantId)
    return request('POST', '/api/addon/events/' .. tostring(eventId) .. '/break',
        { participantId = tonumber(participantId) })
end

function api.return_from_break(eventId, participantId)
    return request('POST', '/api/addon/events/' .. tostring(eventId) .. '/break/return',
        { participantId = tonumber(participantId) })
end

function api.verify_return(eventId, ledgerEntryId)
    return request('POST', '/api/addon/events/' .. tostring(eventId) .. '/verify-return',
        { ledgerEntryId = tonumber(ledgerEntryId) })
end

function api.deny_return(eventId, ledgerEntryId)
    return request('POST', '/api/addon/events/' .. tostring(eventId) .. '/deny-return',
        { ledgerEntryId = tonumber(ledgerEntryId) })
end

-- Fetches a single event including its posted HNM attendance windows + attendees.
-- Used to rehydrate the addon's per-event window cache after an addon reload, so
-- the user sees their previously-posted windows on re-selection.
function api.get_event(eventId)
    return request('GET', '/api/addon/events/' .. tostring(eventId))
end

-- Removes a single character from an already-posted HNM window. Used by the
-- Remove button on each row of a frozen window tab in the launcher; the server
-- drops the AppUserEventWindow row and its matching ledger entry.
function api.remove_window_attendee(eventId, windowSequence, characterName)
    -- urlencode the character name to handle apostrophes / spaces / non-ASCII.
    local encoded = (characterName or ''):gsub('([^%w%-%_%.%~])', function (c)
        return string.format('%%%02X', string.byte(c))
    end)
    return request('DELETE', string.format(
        '/api/addon/events/%d/windows/%d/attendees/%s',
        tonumber(eventId) or 0, tonumber(windowSequence) or 0, encoded))
end

-- entries: array of { characterName, mainJob, subJob, zone }
-- windowSequence: optional 1-based HNM window number. Pass nil for non-HNM events
-- (server will treat the post as the legacy single-window verify).
-- selfCharacterName: the addon's currently logged-in character. Server uses
-- this to backfill the token issuer's linkshell membership / AppUser
-- CharacterName when those columns are still blank, so the user's own
-- attendance entry doesn't silently fall into the unmatched bucket.
function api.post_attendance(eventId, entries, windowSequence, selfCharacterName)
    local body = {
        recordedAtUtc  = os.date('!%Y-%m-%dT%H:%M:%SZ'),
        entries        = entries,
        windowSequence = windowSequence,
    }
    if selfCharacterName and selfCharacterName ~= '' then
        body.selfCharacterName = selfCharacterName
    end
    return request('POST', '/api/addon/events/' .. tostring(eventId) .. '/attendance', body)
end

-- Posts a captured HNM ToD to the web app's existing ToD list. Server fills
-- in cooldown / interval / repop time from per-monster defaults so the addon
-- only needs to send what it observed: the monster name, the UTC kill time,
-- the verbatim chat line for audit, and the user-supplied claim status
-- (true=Claimed, false=Unclaimed, nil=Not Specified — the server stores the
-- value as-is). Not Specified is the loot-pool auto-post path; the launcher
-- keeps both Post (Claimed) / Post (Unclaimed) buttons live so the user can
-- settle the status afterward via api.update_tod_claim. Returns
-- { todId, monsterName, defeatedAtUtc, repopTimeUtc, cooldown, interval, claim }.
function api.post_tod(monsterName, defeatedAtEpochSec, capturedMessage, claimed)
    local epoch = tonumber(defeatedAtEpochSec) or os.time()
    local body = {
        monsterName     = monsterName,
        defeatedAtUtc   = os.date('!%Y-%m-%dT%H:%M:%SZ', epoch),
        capturedMessage = capturedMessage
    }
    -- Only attach `claim` when the caller passed an explicit true/false.
    -- Omitting it keeps the server-side bool? at null (Not Specified).
    if claimed == true or claimed == false then
        body.claim = claimed
    end
    return request('POST', '/api/addon/tod', body)
end

-- Updates the claim flag on an existing ToD that the addon previously posted
-- (e.g. one auto-created by the loot-pool flow before the user picked a claim
-- status). `claimed` must be true or false — the endpoint also accepts null
-- if the caller wants to revert to Not Specified, but the launcher only
-- emits true/false. Returns { todId, claim } on success.
function api.update_tod_claim(todId, claimed)
    return request('POST', '/api/addon/tod/' .. tostring(todId) .. '/claim', {
        claim = claimed
    })
end

-- Returns { characterNames = {...}, roster = { { characterName, alts = {...} }, ... },
--           lootStructure = 'Dkp' | 'Hybrid' | 'LootCouncil' }
-- for the linkshell the addon is paired to. `roster` carries each member's
-- linked alt character names (max 2, account-level). Older addon installs
-- can keep using the flat `characterNames` list; newer code prefers `roster`.
function api.list_roster()
    return request('GET', '/api/addon/roster')
end

-- Posts a single TodLootDetail row attached to an existing Tod. Server creates
-- the DkpLedgerEntry (LootSpent) and updates the winner's LinkshellDkp. Returns
-- { lootDetailId, itemName, itemWinner, winningDkpSpent, actualDeductedDkp }.
function api.post_loot(todId, itemName, itemWinner, winningDkpSpent)
    return request('POST', '/api/addon/tod/' .. tostring(todId) .. '/loot', {
        itemName        = itemName,
        itemWinner      = itemWinner,
        winningDkpSpent = tonumber(winningDkpSpent)
    })
end

return api
