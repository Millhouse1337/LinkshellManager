-- text_parser.lua
-- HNM defeat-message detector + loot-drop detector. Body is a byte-for-byte
-- copy of the text_in handler from att.lua. att.lua's text_in registration
-- becomes a one-line delegate to M.handle(state, deps, e).
--
-- Constants are passed as M.handle args (capture window + dedup) so this
-- module has no module-level state of its own.

local M = {}

-- Tunables (session-only). Kept here because they belong to the parser; the
-- original att.lua initialised them as locals at file scope.
M.TOD_CAPTURE_MAX       = 3
M.TOD_CAPTURE_DEDUP_SEC = 5

function M.handle(state, deps, e)
    local helpers   = deps.helpers
    local constants = deps.constants
    local chat      = deps.chat
    local config    = deps.config

    if not state.todCaptureEnabled then return end
    if e.blocked then return end
    if e.injected then return end -- ignore other addons echoing lines

    -- Mode filter intentionally OFF. Different FFXI clients and private
    -- servers route battle-defeat text through different log types (121,
    -- 122, 36, ...). The patterns below are specific enough that random
    -- chat won't false-match, so we let any incoming line through and let
    -- the pattern engine be the gate.
    local raw = e.message_modified or e.message
    local clean = helpers.clean_str(raw or '')
    if clean == '' then return end

    -- Match against canonical singles (Tiamat, "King Behemoth", etc.) and
    -- the testing presets. Iterating constants tables (not state.linkedEventName)
    -- keeps capture independent of the linked event, and works for
    -- slash-joined preset labels like "Behemoth/King Behemoth" since the
    -- chat line always uses the canonical single name.
    -- Match a defeat line against any monster name. Accepts either a
    -- pairs-iterated table (keys = names, like HNM_WINDOW_COUNTS) or a
    -- list-iterated table (values = names, like config.customMonsters).
    local function defeat_pattern_hits(monsterName)
        local esc = monsterName:gsub('(%W)', '%%%1')
        return clean:find('defeats the ' .. esc .. '%.', 1)
            or clean:find('[Tt]he ' .. esc .. ' was defeated by', 1)
            or clean:find('[Tt]he ' .. esc .. ' falls to the ground', 1)
    end

    local function find_defeat_match(tbl)
        for monsterName, _ in pairs(tbl) do
            if defeat_pattern_hits(monsterName) then return monsterName end
        end
        return nil
    end

    local function find_defeat_match_list(list)
        for _, monsterName in ipairs(list or {}) do
            if monsterName ~= '' and defeat_pattern_hits(monsterName) then
                return monsterName
            end
        end
        return nil
    end

    local hitName = find_defeat_match(constants.HNM_WINDOW_COUNTS)
                 or find_defeat_match(constants.TESTING_MONSTERS)
                 or find_defeat_match(constants.SKY_FARM_NMS)
                 or find_defeat_match_list(config.customMonsters)

    -- Diagnostic: when /att tod debug is on, print every chat line that
    -- mentions "defeats" / "defeated" / "falls" so we can see what mode &
    -- wording the server actually uses if a kill was missed.
    if state.todCaptureDebug
       and (clean:find('defeats') or clean:find('defeated') or clean:find('falls')) then
        print(chat.header('att') .. string.format('[tod-debug] mode=%s match=%s :: %s',
            tostring(e.mode), tostring(hitName or '<none>'), clean))
    end

    -- Loot pool detection: "You find <a/an/the> <item> on the <monster>."
    -- Items are attributed to the most recent capture in the ring buffer
    -- whose monster matches. Drop is silently dropped if the defeat line
    -- hasn't fired yet (lag) so we don't accidentally attribute it to a
    -- stale capture. Runs alongside defeat matching so a single chat line
    -- can't be both -- defeat matches return early below; loot matches
    -- append and return here without falling through.
    if not hitName then
        local function loot_capture_for(monsterName)
            local esc = monsterName:gsub('(%W)', '%%%1')
            return clean:match('^You find ([^%.]+) on the ' .. esc .. '%.$')
        end

        local function find_loot_match(tbl)
            for monsterName, _ in pairs(tbl) do
                local item = loot_capture_for(monsterName)
                if item then return monsterName, item end
            end
            return nil, nil
        end

        local function find_loot_match_list(list)
            for _, monsterName in ipairs(list or {}) do
                if monsterName ~= '' then
                    local item = loot_capture_for(monsterName)
                    if item then return monsterName, item end
                end
            end
            return nil, nil
        end

        local lootMonster, lootItem = find_loot_match(constants.HNM_WINDOW_COUNTS)
        if not lootMonster then
            lootMonster, lootItem = find_loot_match(constants.TESTING_MONSTERS)
        end
        if not lootMonster then
            lootMonster, lootItem = find_loot_match(constants.SKY_FARM_NMS)
        end
        if not lootMonster then
            lootMonster, lootItem = find_loot_match_list(config.customMonsters)
        end

        if state.todCaptureDebug and clean:find('You find') then
            print(chat.header('att') .. string.format(
                '[loot-debug] mode=%s monster=%s item=%s :: %s',
                tostring(e.mode), tostring(lootMonster or '<none>'),
                tostring(lootItem or '<none>'), clean))
        end

        if lootMonster and lootItem then
            -- Strip leading article so "a Goblin mask" becomes "Goblin mask".
            lootItem = lootItem:gsub('^[Aa]n? ', '')
                                :gsub('^[Tt]he ', '')
                                :gsub('^[Ss]ome ', '')
                                :gsub('%s+$', '')
            -- Find the most recent matching capture (newest at index 1)
            -- and append. If none, the drop is dropped silently.
            for _, cap in ipairs(state.todCaptures or {}) do
                if cap.monster == lootMonster then
                    cap.lootDrops = cap.lootDrops or {}
                    table.insert(cap.lootDrops, {
                        itemName   = lootItem,
                        detectedAt = os.time(),
                        draftOpen  = false,
                        draft      = { winner = '', dkpSpent = '' },
                        posting    = false,
                        posted     = nil,
                        postError  = nil,
                    })
                    print(chat.header('att') .. 'Loot detected: '
                        .. lootItem .. ' (' .. lootMonster .. ')')
                    return
                end
            end
        end
        return
    end

    -- Dedup window: same monster within TOD_CAPTURE_DEDUP_SEC is treated as
    -- a duplicate. A different monster within the window still records.
    local now = os.clock()
    if state.todLastCaptureKey == hitName
       and (now - state.todLastCaptureClock) < M.TOD_CAPTURE_DEDUP_SEC then
        return
    end

    -- The verbatim chat line, trimmed only of trailing whitespace. We rely
    -- on Captured at (wall-clock at callback time, second precision) for
    -- the actual ToD value, since FFXI's optional [HH:MM:SS] chat-prefix
    -- requires a client setting most users don't have on and would just
    -- duplicate Captured at anyway.
    local stripped = clean:gsub('%s+$', '')

    table.insert(state.todCaptures, 1, {
        monster      = hitName,
        message      = stripped,
        callbackAt   = constants.format_posted_at(os.date('*t')),
        callbackSec  = os.time(),
    })
    while #state.todCaptures > M.TOD_CAPTURE_MAX do
        table.remove(state.todCaptures)
    end
    state.todLastCaptureKey   = hitName
    state.todLastCaptureClock = now

    -- Echo the capture to chat so the user has visual feedback even when
    -- the launcher window isn't open.
    print(chat.header('att') .. 'ToD Captured: ' .. hitName
        .. ' :: "' .. tostring(stripped) .. '"')
end

return M
