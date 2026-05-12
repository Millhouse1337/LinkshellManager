-- ui/launcher_tod_capture.lua
-- ToD Capture panel: master toggle + Clear + ring-buffer of captures.
-- Extracted from ui.lua byte-for-byte.
local imgui = require('imgui')

local M = {}

function M.draw(state, callbacks)
    -- ToD Capture panel: shows recent HNM defeat lines parsed by the
    -- text_in callback in att.lua. Session-only (no disk persist), with
    -- a master toggle and a manual Clear so the user can dismiss the
    -- list once they've recorded the ToD elsewhere.
    do
        imgui.Text('ToD Capturing')
        imgui.SameLine()
        local todPtr = { state.todCaptureEnabled and true or false }
        if imgui.Checkbox('##todToggle', todPtr) then
            state.todCaptureEnabled = todPtr[1]
        end
        imgui.SameLine()
        imgui.TextDisabled(state.todCaptureEnabled and '(listening)' or '(disabled)')

        -- Right-aligned Clear button mirrors the Close-button placement
        -- below so the panel feels visually consistent with the footer.
        do
            local CLEAR_W = 70
            local todWindowWidth = 600
            pcall(function()
                local ww = imgui.GetWindowWidth()
                if type(ww) == 'number' then todWindowWidth = ww end
            end)
            imgui.SameLine(todWindowWidth - CLEAR_W - 16)
            if imgui.Button('Clear##todClear', { CLEAR_W, 0 }) then
                -- Hide the visible ToD rows but keep the underlying captures
                -- (and any nested lootDrops) intact, so clearing the ToD list
                -- doesn't also wipe the Loot Pool entries that depend on the
                -- same capture rows. The render loop below skips todHidden.
                for _, cap in ipairs(state.todCaptures or {}) do
                    cap.todHidden = true
                end
                state.todLastCaptureKey   = nil
                state.todLastCaptureClock = 0
            end
        end

        -- Captures area is wrapped in a fixed-height BeginChild so the
        -- ring-buffer rows can't push CSV Export / Close off the bottom
        -- of the launcher.
        imgui.BeginChild('todCaptures', { 0, 160 }, false)
        local captures = state.todCaptures or {}

        -- Build a parallel `visible` list mapping tab index (1-based) to
        -- the underlying captures[] index so the post buttons keep using
        -- the original capture index (imgui IDs and on_post_tod expect
        -- it). Doing the walk once up-front means the tab strip + body
        -- render in O(visibleCount) instead of O(captures × tab).
        local visible = {}
        for i, cap in ipairs(captures) do
            if not cap.todHidden then
                visible[#visible + 1] = { capIdx = i, cap = cap }
            end
        end
        local visibleCount = #visible

        if visibleCount == 0 then
            imgui.TextDisabled('  No captures yet. ToDs for selected enemies in your range will appear here.')
        else
            -- Per-capture body renderer. Pulled out so single-capture and
            -- tabbed paths share the same Post/Claim logic byte-for-byte.
            local function render_capture_body(i, cap)
                imgui.TextColored({ 1.0, 0.85, 0.2, 1.0 }, '  ToD Captured!')

                local monster    = tostring(cap.monster    or '?')
                local message    = tostring(cap.message    or '')
                local capturedAt = tostring(cap.callbackAt or '?')
                -- Scrub stray 0x00..0x1F bytes that survived clean_str
                -- so they can't break imgui rendering downstream.
                message = message:gsub('[%z\1-\31]', ' '):gsub('%s+', ' ')
                if message == '' then message = '<no message text>' end

                imgui.Text('  ' .. monster .. ':')
                imgui.Text('    ' .. message)
                imgui.Text('    Captured at: ' .. capturedAt)

                -- Post / claim-status UI. The buttons are gated on the
                -- combined posted + posting + claim state:
                --   posting           -> disabled "Posting..."
                --   not posted        -> show both Post buttons
                --   posted, claim nil -> show "Posted (Not Specified)"
                --                        AND the buttons (so the user can
                --                        settle the status; pressing one
                --                        triggers an update call rather
                --                        than re-posting)
                --   posted, claim set -> show "Posted (Claimed/Unclaimed)"
                --                        no buttons
                local function draw_claim_buttons()
                    imgui.Indent(20)
                    if imgui.Button('Post (Claimed)##postTodClaim' .. tostring(i), { 150, 0 }) then
                        if callbacks.on_post_tod then
                            callbacks.on_post_tod(i, true)
                        end
                    end
                    imgui.SameLine()
                    if imgui.Button('Post (Unclaimed)##postTodNoClaim' .. tostring(i), { 170, 0 }) then
                        if callbacks.on_post_tod then
                            callbacks.on_post_tod(i, false)
                        end
                    end
                    imgui.Unindent(20)
                end

                if cap.posting then
                    imgui.TextDisabled('    Posting...')
                elseif cap.posted then
                    local claimLabel
                    if cap.posted.claim == true then
                        claimLabel = ' (Claimed)'
                    elseif cap.posted.claim == false then
                        claimLabel = ' (Unclaimed)'
                    else
                        claimLabel = ' (Not Specified)'
                    end
                    imgui.TextColored({ 0.4, 1.0, 0.4, 1.0 }, '    Posted to web!' .. claimLabel)
                    if cap.posted.repopFormatted then
                        imgui.Text('    Repop: ' .. tostring(cap.posted.repopFormatted))
                    end
                    if cap.posted.claim == nil then
                        draw_claim_buttons()
                    end
                    if cap.postError then
                        imgui.TextColored({ 1.0, 0.5, 0.5, 1.0 },
                            '    Update failed: ' .. tostring(cap.postError))
                    end
                else
                    draw_claim_buttons()
                    if cap.postError then
                        imgui.TextColored({ 1.0, 0.5, 0.5, 1.0 },
                            '    Post failed: ' .. tostring(cap.postError))
                    end
                end
            end

            if visibleCount == 1 then
                -- Single capture: skip the tab bar entirely so the panel
                -- doesn't render an empty-looking single-tab strip.
                render_capture_body(visible[1].capIdx, visible[1].cap)
            else
                -- Pre-count monster name occurrences so duplicate names
                -- get disambiguating "#N" suffixes in the tab labels
                -- (rare ring-buffer reuse during the same session).
                local nameCounts, seenSoFar = {}, {}
                for _, v in ipairs(visible) do
                    local key = tostring(v.cap.monster or '?')
                    nameCounts[key] = (nameCounts[key] or 0) + 1
                end
                -- Use imgui native tabs to match the Loot Pool tab
                -- styling (raised/highlighted active tab + proper
                -- hover states) instead of the flat Selectable row
                -- the previous revision used. imgui tracks the active
                -- tab internally so no external state.todActiveTabIndex
                -- is needed any more.
                if imgui.BeginTabBar('todCaptureTabs') then
                    for _, v in ipairs(visible) do
                        local monsterName = tostring(v.cap.monster or '?')
                        seenSoFar[monsterName] = (seenSoFar[monsterName] or 0) + 1
                        local label = monsterName
                        if (nameCounts[monsterName] or 0) > 1 then
                            label = string.format('%s #%d', monsterName, seenSoFar[monsterName])
                        end
                        -- imgui ID suffix off the underlying captures[]
                        -- index so a re-pop of the same monster name
                        -- into a different ring slot keeps distinct tabs.
                        local tabId = string.format('%s##todTab_%d', label, v.capIdx)
                        if imgui.BeginTabItem(tabId) then
                            render_capture_body(v.capIdx, v.cap)
                            imgui.EndTabItem()
                        end
                    end
                    imgui.EndTabBar()
                end
            end
        end
        imgui.EndChild()
    end
end

return M
