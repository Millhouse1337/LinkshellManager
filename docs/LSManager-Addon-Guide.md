# LinkshellManager — In-Game Addon Guide (`lsm`)

> A Lua addon for **Ashita v3**. Captures attendance from `/sea` results, watches chat for ToDs, and (when paired) syncs everything to LSManager in real time.

This guide covers the **in-game addon**. Its companions have their own guides:
- **[Web App Guide](LSManager-WebApp-Guide.md)** — the full management surface in a browser.
- **[Discord Activity Guide](LSManager-DiscordActivity-Guide.md)** — the member-facing experience in Discord.

You generate **pairing codes** from either the web app or the Discord Activity; everything the addon captures shows up in both within seconds.

---

## Installation
1. Copy the `lsm` folder into your Ashita installation:  `Ashita/addons/lsm/`.
2. In game, load it:
   ```
   /addon load att
   ```
3. (Optional) auto-load on launch — add to your boot script:
   ```
   /addon load att
   ```

## Pair the addon to your linkshell — one time
The pairing flow is two short commands. You only run the *server* command on first install (or when the host URL changes).

**Step 1 — point the addon at your LSManager server**
```
/lsm server https://your-lsmanager-host
```
The addon probes the URL right away. You'll see one of:
- `Server OK (HTTP 401). Use /lsm link <code> [1|2] to pair.` — good, ready to pair.
- `Probe FAILED: ...` — URL is wrong or unreachable; check spelling and that the site is up.

**Step 2 — generate a pairing code on the website or Discord Activity**
- **Web:** Configurations → Customize Linkshell → Addon pairing → *Generate pairing code*.
- **Discord:** Configurations tab → Game Addon (lsm) → *+ Get Code*.
- Codes expire in a few minutes. Copy the 8-character code shown.

**Step 3 — link the addon to a pearl slot**
```
/lsm link <code>           (defaults to LS1)
/lsm link <code> 1         (LS pearl slot 1 — main linkshell)
/lsm link <code> 2         (LS pearl slot 2 — second linkshell)
```
On success: `Linked to <Linkshell Name> on LS1 [optional label]`. The addon now syncs to that linkshell whenever you use the LS1 (or LS2) pearl in game.

You can pair **two different linkshells** — one to LS1, one to LS2 — and the addon picks the right one based on which pearl you're wearing.

## Check your status / unlink
```
/lsm status                List server URL and current pairings
/lsm list                   Same as status
/lsm unlink                 Unlink everything
/lsm unlink 1               Unlink LS1 only
/lsm unlink 2               Unlink LS2 only
/lsm unlink all             Same as bare /lsm unlink
```

## Day-to-day commands

**Open the launcher (the main UI window)**
```
/attend
```
This opens the launcher with: action bar, attendance roster, queued events from the web, break room, create-event, loot pool, and ToD capture panel. The local roster is seeded with you immediately. If paired, the latest queued events from the web also load automatically.

**Take attendance for a known event**
```
/lsm <event-alias>            e.g.  /lsm kirin    /lsm fafnir    /lsm "King Behemoth"
/lsm <alias> ls               Restrict to LS pearl 1
/lsm <alias> ls2              Restrict to LS pearl 2
/lsm <alias> h                Write to HNM log
/lsm <alias> e                Write to Event log
```
The addon issues `/sea <area> linkshell`, waits for the result list, builds attendance, then opens the attendance window. If paired, the result also posts to the web event.

**Take attendance for the current zone**
```
/lsm                          Use current zone, no /sea scan
/lsm here                     Resolve event by current zone and run it
```

**Take attendance everywhere**
```
/lsm all                      /sea all linkshell — global scan
```

**Show composition vs. roster**
```
/comp <event-alias>           Show needed composition for the event
/comp list                    List every event with a composition defined
```

**Help window**
```
/lsm help
```

## ToD capture (automatic)

Once paired, the addon listens to chat for "X was defeated by ..." lines from known HNM/NM. When it sees one:

1. Captures the monster name and timestamp.
2. Posts a ToD to the linked linkshell (LS1 or LS2 depending on which pearl you're wearing).
3. The ToD shows up immediately on the **Dashboard ToD Tracker** and the **ToDs** view in both the web app and the Discord Activity — with countdown to repop window.

**Diagnose missed captures:**
```
/lsm tod debug                Toggle verbose capture logging
/lsm tod debug on             Force on
/lsm tod debug off            Force off
/lsm tod clear                Clear local capture cache
```

## Live event flow (when paired)

1. **Officer creates an event** — on web, Discord, or in-game (the launcher's *Create Event* panel).
2. **Members sign up** — from any of the three surfaces.
3. **Event starts** — once it's in commencement, attendance windows open.
4. **In-game**, anyone with the addon can run `/lsm <alias>` or `/lsm here` during each window. The addon scans, builds the roster, and posts attendance to the web.
5. **Breaks** — members can flag themselves as on-break from the launcher; officers can verify or moderate.
6. **Loot** — winners and DKP spent are recorded against the event and against the ToD if the kill was tied to one.
7. **Event ends** — it moves to **Event History** with the full attendance ledger and loot list.

## Debug & developer commands
```
/lsm debug          Toggle the addon debug window
/lsm debugmode      Toggle verbose console logging
/lsm memscan        Find the entity-list pointer (if memory broke after a patch)
/lsm memdump <addr> [count]
/lsm api            Dump entity manager metatable
/findoffset         Deep-scan for entity list
/apidump            Dump memory API methods
```

---

## Quick reference
- **Pair:** `/addon load att` → `/lsm server <url>` → generate a code on web/Discord → `/lsm link <code> [1|2]`.
- **Open the launcher:** `/attend`.
- **Take attendance:** `/lsm <event>` or `/lsm here`.
- **Check pairings:** `/lsm status`.
- **Companion guides:** [Web App](LSManager-WebApp-Guide.md) · [Discord Activity](LSManager-DiscordActivity-Guide.md)
