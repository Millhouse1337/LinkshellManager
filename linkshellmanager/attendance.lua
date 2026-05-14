-- attendance.lua
local attendance = {}
local attendance = {}

local resources = require('resources')
local memory    = require('memory')
local helpers   = require('helpers')
local helpers   = require('helpers')
local constants = require('constants')
local messages  = require('messages')

attendance.data = {} -- entries: { name, jobsMain, jobsSub, zone, zid, time }

-- Scope filter for multi-window (HNM / Claim-Kill) scans. Set by the
-- launcher attendance panel via attendance.set_scope; consumed by
-- gather_zone / gather_current_zone to narrow zone-scan hits to just the
-- player's party (slots 0-5), the full alliance (slots 0-17), or everyone
-- in the credit zone (the historical behavior, 'zone'). Non-HNM scans
-- ignore this and always treat everyone in-credit as eligible.
attendance.scope = 'zone'

function attendance.set_scope(scope)
    if scope == 'party' or scope == 'alliance' or scope == 'zone' then
        attendance.scope = scope
    end
end

-- Builds a name-set for the active scope. Returns nil for 'zone' (and any
-- unrecognised value) so callers can short-circuit the filter cheaply when
-- no narrowing is needed.
local function build_scope_set(scope)
    if scope ~= 'party' and scope ~= 'alliance' then return nil end
    local pm = AshitaCore:GetMemoryManager():GetParty()
    if not pm then return nil end
    local names = {}
    local hi = (scope == 'party') and 5 or 17
    for i = 0, hi do
        local n = pm:GetMemberName(i)
        if n and n ~= '' then names[n:lower()] = true end
    end
    return names
end

-- Helper: Check if zid is in event credit
local function zid_in_credit(eventName, zid)
    -- DEBUG
    local set = resources.attCreditZoneIds[eventName]
    -- print(string.format('[att-debug] Check: Ev="%s" ZID=%s InSet=%s', eventName, tostring(zid), tostring(set and set[zid])))

    if not eventName then return false end
    if eventName == 'Global Search' then return true end

    -- Try the name as-is, then again with the launcher's "D<n>" day-suffix
    -- stripped. Lets day-tagged HNM events ("Fafnir/Nidhogg D234") fall
    -- back to the canonical credit zone keys.
    local canon = constants.canonical_event_name(eventName)
    local set = resources.attCreditZoneIds[eventName]
    if not (set and next(set) ~= nil) and canon ~= eventName then
        set = resources.attCreditZoneIds[canon]
    end
    if set and next(set) ~= nil then
        return set[zid] == true
    end

    -- Fallback: compare normalized names
    local zname = resources.attZoneList[zid] or 'UnknownZone'
    local list  = resources.attCreditNames[eventName]
        or (canon ~= eventName and resources.attCreditNames[canon])
        or nil
    if not list then return false end

    local nz = helpers.norm(zname)
    for _, s in ipairs(list) do
        if helpers.norm(s) == nz then return true end
    end

    return false
end

function attendance.clear()
    attendance.data = {}
end

function attendance.sort()
    table.sort(attendance.data, function(a, b)
        local an = (a.name or ''):gsub('^X%s+', ''):lower()
        local bn = (b.name or ''):gsub('^X%s+', ''):lower()
        return an < bn
    end)

    -- Pin the local player to row 1 so they're always visible without scrolling.
    local pm = AshitaCore and AshitaCore:GetMemoryManager() and AshitaCore:GetMemoryManager():GetParty()
    local selfName = pm and pm:GetMemberName(0)
    if selfName and selfName ~= '' then
        local selfKey = selfName:lower()
        for i, row in ipairs(attendance.data) do
            if (row.name or ''):gsub('^X%s+', ''):lower() == selfKey then
                if i ~= 1 then
                    table.remove(attendance.data, i)
                    table.insert(attendance.data, 1, row)
                end
                return
            end
        end
    end
end

-- Returns the local player's current main / sub job names (the strings
-- the addon uses internally — "WAR", "BLM", "NONE", etc.) read straight
-- off the party-memory manager. Used by the Join-event action-bar
-- handler so the late-join payload always reflects the player's CURRENT
-- jobs at click time, even if attendance.data is stale or hasn't been
-- gathered yet for the linked event.
function attendance.get_self_jobs()
    local pm = AshitaCore:GetMemoryManager():GetParty()
    if not pm then return 'NONE', 'NONE' end
    local mj_id = pm:GetMemberMainJob(0) or 0
    local sj_id = pm:GetMemberSubJob(0)  or 0
    local mj = resources.attJobList[mj_id] or 'NONE'
    local sj = resources.attJobList[sj_id] or 'NONE'
    return mj, sj
end

-- Adds the local player to the roster if they aren't already in it.
-- Always called at the end of a gather so the user themselves shows up
-- regardless of whether the entity scan picked them up.
function attendance.add_self()
    local pm = AshitaCore:GetMemoryManager():GetParty()
    if not pm then return end
    local name = pm:GetMemberName(0)
    if not name or name == '' then return end

    local key = name:lower()
    for _, row in ipairs(attendance.data) do
        if row.name:gsub('^X%s+', ''):lower() == key then return end
    end

    local mj  = pm:GetMemberMainJob(0) or 0
    local sj  = pm:GetMemberSubJob(0)  or 0
    local zid = pm:GetMemberZone(0)    or 0
    attendance.add_entry(name, mj, sj, zid)
    attendance.sort()
end

function attendance.add_entry(name, mj_id, sj_id, zid, force_time)
    local zname = resources.attZoneList[zid] or 'UnknownZone'
    local jobsMain = resources.attJobList[mj_id] or 'NONE'
    local jobsSub  = resources.attJobList[sj_id] or 'NONE'
    
    table.insert(attendance.data, {
        name     = name,
        jobsMain = jobsMain,
        jobsSub  = jobsSub,
        zone     = zname,
        zid      = zid,
        time     = force_time or os.date('%H:%M:%S')
    })
end

-- function attendance.gather_alliance(eventName) -- Removed
-- end

function attendance.gather_zone(eventName)
    local entries = memory.scan_zone_list()
    -- Pre-build the scope name-set so the per-entry filter is an O(1)
    -- table lookup. Nil means "zone" (no narrowing — historical behavior).
    local scopeSet = build_scope_set(attendance.scope)
    local seen = {}
    for _, row in ipairs(attendance.data) do
        seen[row.name:gsub('^X%s+', ''):lower()] = true
    end

    local added = 0
    for name, info in pairs(entries) do
        local key = name:lower()
        local is_seen = seen[key]
        local is_credit = zid_in_credit(eventName, info.zid)
        local in_scope = (scopeSet == nil) or scopeSet[key] == true

        if attendance.debug then
            print(string.format('[att-dbg] Candidate: "%s" (ZID:%d) | Seen:%s | Credit:%s | Scope[%s]:%s',
                name, info.zid, tostring(is_seen), tostring(is_credit),
                tostring(attendance.scope), tostring(in_scope)))
        end

        if not is_seen and is_credit and in_scope then
            attendance.add_entry(name, info.mj, info.sj, info.zid)
            seen[key] = true
            added = added + 1
        end
    end
    attendance.sort()
    attendance.add_self()
    return added
end

-- Variant for the launcher's auto-create flow: there's no event-name -> credit-zone
-- mapping for a user-typed event, so we just take everyone in the user's CURRENT zone.
function attendance.gather_current_zone()
    local self_zid = memory.get_current_zone_id()
    local entries = memory.scan_zone_list()
    -- Same party/alliance narrowing the credit-zone scan applies. 'zone'
    -- (the default) leaves scopeSet nil so every entity in the user's
    -- current zone is eligible, matching the historical behavior.
    local scopeSet = build_scope_set(attendance.scope)
    local seen = {}
    for _, row in ipairs(attendance.data) do
        seen[row.name:gsub('^X%s+', ''):lower()] = true
    end

    local added = 0
    for name, info in pairs(entries) do
        local key = name:lower()
        local in_scope = (scopeSet == nil) or scopeSet[key] == true
        if not seen[key] and info.zid == self_zid and in_scope then
            attendance.add_entry(name, info.mj, info.sj, info.zid)
            seen[key] = true
            added = added + 1
        end
    end
    attendance.sort()
    attendance.add_self()
    print(string.format('[att] gather (current zone): added %d', added))
    return added
end

-- Live preview helper used by the launcher attendance panel.
-- Returns the list of linkshell roster members currently visible in the
-- player's FFXI entity list (zone) under the active attendance.scope filter.
-- Pure read — does not mutate attendance.data, does not post anything.
-- Each row is { name=<canonical roster name>, main=<job abbrev>, sub=<job abbrev> }
-- with '?' substituted when a job id isn't resolvable.
function attendance.list_inzone_lsmembers(rosterNames)
    if not rosterNames or #rosterNames == 0 then return {} end

    local entries = memory.scan_zone_list()
    if not entries then return {} end

    -- Build case-insensitive roster set so we can match the (case-sensitive)
    -- entity name back to the canonical spelling stored in the roster.
    local rosterByLower = {}
    for _, n in ipairs(rosterNames) do
        if n and n ~= '' then rosterByLower[n:lower()] = n end
    end

    local scopeSet = build_scope_set(attendance.scope)
    local out = {}
    for name, info in pairs(entries) do
        local key = name:lower()
        local canonical = rosterByLower[key]
        local in_scope = (scopeSet == nil) or scopeSet[key] == true
        if canonical and in_scope then
            local mainAb = resources.attJobList[tonumber(info.mj) or -1] or '?'
            local subAb  = resources.attJobList[tonumber(info.sj) or -1] or '?'
            out[#out + 1] = { name = canonical, main = mainAb, sub = subAb }
        end
    end

    table.sort(out, function(a, b) return a.name < b.name end)
    return out
end

-- Captures a single point-in-time alliance snapshot for /lsm now.
-- Walks party slots 0-17 and pulls each active member's name, main/sub
-- job + level, and zone via Ashita's party memory manager. Pure read.
-- Returns:
--   { capturedAtUtc, capturedAtLocal, utcOffset, capturedBy, entries }
-- where each entry is { name, mainJob, mainJobLevel, subJob, subJobLevel, zone }
-- with job abbreviations resolved via resources.attJobList and zone names
-- via resources.attZoneList. '---' jobs render as empty strings to match
-- Hatberg's CSV format exactly.
function attendance.list_alliance_snapshot()
    local pm = AshitaCore and AshitaCore:GetMemoryManager()
                                       and AshitaCore:GetMemoryManager():GetParty()
    if not pm then
        return nil, 'Party memory not available (zoning or not logged in).'
    end

    local nowUtc = os.date('!%Y-%m-%dT%H:%M:%SZ')
    local nowLocal = os.date('%Y-%m-%d %H:%M:%S')
    local offset = os.date('%z') or ''

    local capturedBy = pm:GetMemberName(0) or ''
    if capturedBy == '' then
        return nil, 'Local character name unavailable.'
    end

    local entries = {}
    for i = 0, 17 do
        local active = pm:GetMemberIsActive(i)
        if active and active ~= 0 then
            local name = pm:GetMemberName(i) or ''
            if name ~= '' then
                local mainId = pm:GetMemberMainJob(i) or 0
                local subId  = pm:GetMemberSubJob(i)  or 0
                local mainAb = resources.attJobList[mainId] or '?'
                local subAb  = resources.attJobList[subId]  or '?'
                local mainLv = (mainAb ~= '---') and pm:GetMemberMainJobLevel(i) or nil
                local subLv  = (subAb  ~= '---') and pm:GetMemberSubJobLevel(i)  or nil
                local zoneId = pm:GetMemberZone(i) or 0
                local zoneName = resources.attZoneList[zoneId] or ''

                entries[#entries + 1] = {
                    name         = name,
                    mainJob      = (mainAb == '---') and '' or mainAb,
                    mainJobLevel = mainLv,
                    subJob       = (subAb  == '---') and '' or subAb,
                    subJobLevel  = subLv,
                    zone         = zoneName,
                }
            end
        end
    end

    if #entries == 0 then
        return nil, 'No active alliance members found.'
    end

    return {
        capturedAtUtc   = nowUtc,
        capturedAtLocal = nowLocal,
        utcOffset       = offset,
        capturedBy      = capturedBy,
        entries         = entries,
    }, nil
end

function attendance.write_file(addon_path, mode, eventName)
    local dateStr = os.date('%A %d %B %Y')
    local timeStr = os.date('%H.%M.%S')
    local dir, msg

    if mode == 'HNM' then
        dir = addon_path .. 'HNM Logs\\'
        msg = string.format(messages.HNM_TAKEN, eventName)
    else
        dir = addon_path .. 'Event Logs\\'
        msg = string.format(messages.EVENT_TAKEN, eventName)
    end

    -- io.open with mode 'a' will not create parent folders on Windows; if the
    -- "HNM Logs\" / "Event Logs\" directory is missing (fresh addon install,
    -- or folders deleted manually) the open silently fails. Match the same
    -- best-effort mkdir pattern api.lua uses for the temp dir so the first
    -- enable of CSV Export works without manual setup. The 2>nul swallows
    -- the "already exists" stderr line on subsequent runs.
    os.execute('mkdir "' .. dir:gsub('\\$', '') .. '" 2>nul')

    local filePath = dir .. dateStr .. ' ' .. timeStr .. '.csv'

    local f, openErr = io.open(filePath, 'a')
    if not f then
        return nil, string.format('Could not open file: %s (%s)',
            filePath, tostring(openErr or 'unknown error'))
    end

    local count = 0
    for _, row in ipairs(attendance.data) do
        if not row.name:match('^X ') then
            f:write(string.format(
                '%s,%s,%s,%s,%s,%s\n',
                row.name,
                row.jobsMain,
                os.date('%m/%d/%Y'),
                os.date('%H:%M:%S'),
                row.zone,
                eventName
            ))
            count = count + 1
        end
    end
    f:close()

    return count, msg
end

-- End-of-event summary CSV: written when the user clicks End Event with CSV
-- Export enabled. Source of truth is the server's end-event response (already
-- committed to EventHistory + DkpLedgerEntry) so the rows match what the web /
-- Discord views show. Goes to the same HNM Logs / Event Logs folder split as
-- write_file but uses a "<event> Summary" filename so it doesn't collide with
-- the per-post roster snapshots.
function attendance.write_end_event_file(addon_path, mode, eventName, result)
    if type(result) ~= 'table' then
        return nil, 'No end-event payload to summarize.'
    end

    local function asStr(v)
        if type(v) == 'string' and v ~= '' then return v end
        return ''
    end

    local dir
    if mode == 'HNM' then
        dir = addon_path .. 'HNM Logs\\'
    else
        dir = addon_path .. 'Event Logs\\'
    end

    -- io.open won't create parent folders on Windows; mkdir ahead of time so a
    -- fresh install / deleted folder doesn't silently swallow the write.
    os.execute('mkdir "' .. dir:gsub('\\$', '') .. '" 2>nul')

    -- Sanitize event name for the filename: drop characters Windows rejects in
    -- file names (\ / : * ? " < > |) and trim trailing whitespace/dots.
    local safeName = (asStr(result.eventName) ~= '' and asStr(result.eventName))
        or eventName or 'Event'
    safeName = safeName:gsub('[\\/:*?"<>|]', '_'):gsub('[%s%.]+$', '')
    if safeName == '' then safeName = 'Event' end

    local dateStr = os.date('%A %d %B %Y')
    local timeStr = os.date('%H.%M.%S')
    local filePath = string.format('%s%s Summary %s %s.csv',
        dir, safeName, dateStr, timeStr)

    local f, openErr = io.open(filePath, 'w')
    if not f then
        return nil, string.format('Could not open file: %s (%s)',
            filePath, tostring(openErr or 'unknown error'))
    end

    -- Header row + event-level metadata block, then the per-participant table.
    -- Two sections in one file keeps the audit self-contained: who hosted what
    -- at which rate, then exactly who got credited and for how much.
    local windowCount = tonumber(result.windowCount) or 1
    local isWindowed  = windowCount > 1
    local rateUnit    = isWindowed and 'window' or 'hour'
    local rate        = isWindowed
        and (tonumber(result.dkpPerWindow) or 0)
        or  (tonumber(result.dkpPerHour) or 0)

    f:write('Event,Type,Location,Started,Ended,DKPRate,RateUnit,WindowCount\n')
    f:write(string.format('%s,%s,%s,%s,%s,%g,%s,%d\n',
        asStr(result.eventName),
        asStr(result.eventType),
        asStr(result.eventLocation),
        asStr(result.commencementStartTime),
        asStr(result.endTime),
        rate,
        rateUnit,
        windowCount))
    f:write('\n')
    f:write('CharacterName,MainJob,SubJob,DurationHours,WindowsAttended,DKPEarned\n')

    local count = 0
    local participants = (type(result.participants) == 'table') and result.participants or {}
    for _, p in ipairs(participants) do
        f:write(string.format('%s,%s,%s,%s,%s,%g\n',
            asStr(p.characterName),
            asStr(p.jobName),
            asStr(p.subJobName),
            tostring(tonumber(p.durationHours) or 0),
            tostring(tonumber(p.windowsAttended) or 0),
            tonumber(p.dkpEarned) or 0))
        count = count + 1
    end
    f:close()

    return count, 'Wrote end-event summary: ' .. filePath
end

function attendance.resolve_events_for_zone(zid)
    local zname = resources.attZoneList[zid] or 'UnknownZone'
    
    -- Explicit mappings
    local evs_by_id = {}
    for eventName, zoneIdSet in pairs(resources.attCreditZoneIds) do
        if zoneIdSet[zid] then
            table.insert(evs_by_id, eventName)
        end
    end
    if #evs_by_id > 0 then
        return evs_by_id, zname
    end

    -- Name mapping
    local nz   = helpers.norm(zname)
    local evs  = {}
    for eventName, zoneList in pairs(resources.attCreditNames) do
        for _, zone in ipairs(zoneList) do
            if helpers.norm(zone) == nz then
                table.insert(evs, eventName)
                break
            end
        end
    end
    
    return evs, zname
end

return attendance
