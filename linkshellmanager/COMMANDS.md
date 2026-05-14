# LSManager Addon — Command Reference

All chat commands the `linkshellmanager` (`lsm`) addon registers.

## Web sync / pairing

| Command | What it does |
|---|---|
| `/lsm server <url>` | Set the LSManager web server URL (e.g. `https://linkshellmanager.com`). |
| `/lsm link <code> [1\|2]` | Redeem a pairing code from the web app on slot 1 or 2 (defaults to 1). |
| `/lsm unlink [1\|2\|all]` | Drop a slot's pairing locally. `all` clears every pairing. |
| `/lsm status` | Show server + pairings. Probes each token against the server first and auto-drops any whose token was revoked from the web UI. |

## Launcher UI

| Command | What it does |
|---|---|
| `/attend` | Toggle the main launcher window (Web Sync, Event Presets, Queued / Active Events, Attendance, Break Room, Loot Pool, ToDs). |
| `/attend close` | Close the launcher. |

## Alliance snapshot

| Command | What it does |
|---|---|
| `/lsm now` | Walks alliance slots 0–17, writes a CSV row per active member to `Ashita\addons\linkshellmanager\Snapshots\{Char}_{date}_{time}.csv` (`name,MAIN##/SUB##,date,time,UTC+offset,zone,`), **and** pushes the same payload to LSManager. The snapshot appears on the **Event System → Attendance Snapshots** web page within seconds. CSV + web sync are independent — if one fails the other still runs and the chat status tells you which path succeeded. |

## Help

| Command | What it does |
|---|---|
| `/lsm help` | Print the full command list + quick-start guide directly in chat. |

## Typical workflow

1. **First-time setup:** run `/lsm server https://linkshellmanager.com`. Then generate a pairing code on the web app under the addon settings, then `/lsm link <code>` in-game.
2. **During an event night:** `/attend` to open the launcher; pick or create an active event; click **Start & Post / Post Window** as the event runs.
3. **Ad-hoc roster capture (e.g. mid-event headcount):** `/lsm now`. The snapshot shows up on the **Attendance Snapshots** page on the web app within a few seconds.
4. **Check link status / drop revoked tokens:** `/lsm status`.
