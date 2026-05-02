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
        -- of the launcher. Content scrolls inside this region when it
        -- exceeds 100px (e.g. all 3 ring slots populated).
        imgui.BeginChild('todCaptures', { 0, 100 }, false)
        local captures = state.todCaptures or {}
        -- Count visible (non-hidden) captures up front so the empty hint
        -- only fires when the user-visible list is genuinely empty.
        local visibleCount = 0
        for _, cap in ipairs(captures) do
            if not cap.todHidden then visibleCount = visibleCount + 1 end
        end
        if visibleCount == 0 then
            imgui.TextDisabled('  No captures yet. ToDs for selected enemies in your range will appear here.')
        else
            local rendered = 0
            for i, cap in ipairs(captures) do
                if not cap.todHidden then
                    rendered = rendered + 1
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

                    -- Post ToD button + state. Three states, mutually exclusive:
                    --   posted   -> green "Posted ✓" + repop hint
                    --   posting  -> disabled "Posting..."
                    --   default  -> "Post ToD" button, plus optional error line
                    if cap.posted then
                        imgui.TextColored({ 0.4, 1.0, 0.4, 1.0 }, '    Posted to web!')
                        if cap.posted.repopFormatted then
                            imgui.Text('    Repop: ' .. tostring(cap.posted.repopFormatted))
                        end
                    elseif cap.posting then
                        imgui.TextDisabled('    Posting...')
                    else
                        imgui.Indent(20)
                        if imgui.Button('Post ToD##postTod' .. tostring(i), { 90, 0 }) then
                            if callbacks.on_post_tod then
                                callbacks.on_post_tod(i)
                            end
                        end
                        imgui.Unindent(20)
                        if cap.postError then
                            imgui.TextColored({ 1.0, 0.5, 0.5, 1.0 },
                                '    Post failed: ' .. tostring(cap.postError))
                        end
                    end

                    if rendered < visibleCount then imgui.Separator() end
                end
            end
        end
        imgui.EndChild()
    end
end

return M
