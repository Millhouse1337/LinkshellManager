# LinkshellManager — User Guide

> Your linkshell's command center. Plan events. Track DKP. Capture ToDs. Run live attendance — straight from the game.

LinkshellManager (LSManager) is a three-piece system that keeps every part of your endgame linkshell on the same page:

| Piece | Where it lives | What it's for |
| --- | --- | --- |
| **Web App** | `https://your-host/` (any browser) | Full management — roster, DKP, items, revenue, auctions |
| **Discord Activity** | Inside Discord — voice channel "Activities" tray | Quick member-facing experience while everyone's already in voice |
| **In-Game Addon** (`att`) | Ashita v3 / HorizonXI client | Live attendance, ToD capture, loot logging, event creation — without alt-tabbing |

All three share the **same database**. Anything done on the addon shows up in the web and Discord views in seconds.

---

## Part 1 — The Web App

The full ASP.NET site. Everything in the system lives here, organized into a left sidebar.

### Getting in
- Open the site in a browser.
- Click **Login with Discord** — accounts are linked to your Discord identity automatically.
- First-time login creates your `AppUser` record. You'll be prompted to set your **character name** (and optionally up to two **alt character names**) on the Profile page.

### Sidebar Map

**Top — Dashboard**
A snapshot of your active linkshell: member count, upcoming events, upcoming auctions, upcoming ToDs, revenue (if enabled), and a recent-activity feed split by Kills / Claims / Events / Loot.

**Announcements**
- *View Announcements* — scrollable feed for everyone in the linkshell.
- *Create Announcement* — leaders/officers post news.

**Rules**
- *View Rules* — your linkshell's published rule set.
- *Create Rule* — leaders/officers add or edit rules.

**Missions** *(navigation placeholders for Zilart / CoP / ToAU — content per linkshell)*

**End Game** *(navigation placeholders for Sky / Sea / HNM / Dynamis / Limbus)*

**Linkshell Auction**
- *View Auctions* — current live and upcoming auctions.
- *Create Auction* — leaders/officers list an item with starting bid, min increment, and end time.
- *Auction History* — every settled auction with winner and final DKP.

**Event System**
- *View Events* — every active event (open for sign-ups, in progress, or scheduled).
- *Create Event* — name, type (HNM / Event / Custom), location, start time, DKP per hour, requested slots.
- *Event History* — completed events with full attendance, breaks, and loot ledger.

**ToDs (Time of Death)**
- *View ToDs* — every tracked NM/HNM with countdown to repop, claim status, and loot history.
- *Add ToD* — manually log a kill (the addon does this automatically — see Part 3).

**Management** *(officer/leader area)*
- **Manage Team** — view roster, search/add players, send and manage invites.
- **Manage Items** — items inventory the linkshell can auction or distribute.
- **Manage Revenue** — track linkshell income.
- **DKP** — view DKP ledger; apply manual DKP adjustments.
- **Configurations** — view all linkshells you belong to, create a new one, or **Customize Linkshell** (loot structure, feature toggles, addon pairing — see below).

**Pages**
- **Profile** — character name, alts, time zone, profile image, primary linkshell.
- **Messages** — internal messaging.

### Customize Linkshell (the control panel)
On `Configurations → Customize Linkshell` (leader/officer only) you can:

- **Loot structure**
  - **DKP** — time-based DKP per event, spent on items.
  - **Loot Council** — leader awards items; no DKP.
  - **Percentage Based** — DKP earned normally; loot deducts a % of the winner's balance.
- **DKP rounding** — Quarter (0.25) or Half (0.5) increments.
- **Feature toggles** — turn off any tab your linkshell doesn't use (Endgame, HNM, Missions, Auctions, ToDs, Events, DKP, Items tile, Revenue tile).
- **Addon pairing codes** — generate one-time codes that the in-game addon redeems to link itself to this linkshell.

---

## Part 2 — The Discord Activity

Same data, packaged for Discord. Launch it from any voice channel: click the **rocket icon → Activities → LinkshellManager**. It opens inside Discord — no browser tab.

### First launch
1. Discord prompts you to authorize the app (`identify`, `guilds`, `applications.commands`).
2. The app finds your linked `AppUser` and drops you on the **Dashboard** tab.
3. If you're outside Discord and visit `/discord-activity` directly, you get a **standalone preview** with the same UI.

### The tabs (top of the Activity)

- **Dashboard** — Linkshell overview stat strip · Rules · Announcements · Roster (with search) · News & Updates · Upcoming Events · ToD Tracker (with countdowns and loot history) · HNM Claims donut (7d / 30d / All) · Recent Activity feed (filter: All · Kills · Claims · Events · Loot).

- **Linkshell** — Full member roster: characters, ranks, status, DKP per linkshell. Officer tools for kicking, role changes, etc.

- **Events** — Upcoming events you can sign up for, plus in-progress events with attendance and break tracking.

- **ToDs** — Add a ToD manually (mob, time, day, cooldown). View grouped history per monster, expand to see loot per kill.

- **Configurations** *(officer/leader)* — Manage your linkshells, create new ones, customize the active linkshell, and **generate addon pairing codes**.

### Side panels (right edge)

- **Roster panel** — quick member lookup without leaving the tab.
- **Auctions panel** — live bid view; place bids inline.
- **Invites panel** — pending invites to/from the linkshell.

### Generating an addon pairing code (Discord)
1. Switch to **Configurations** tab.
2. Scroll to **Addon Tokens** → click **+ Generate pairing code**.
3. (Optional) add a label like "Nils — desktop".
4. The code displays with a countdown — copy it. It expires in a few minutes.
5. In FFXI, type `/att link <code>` (see Part 3).

You can also generate codes from the web app at **Configurations → Customize Linkshell → Addon Tokens**.

---

## Part 3 — The In-Game Addon (`att`)

A Lua addon for **Ashita v3**. Captures attendance from `/sea` results, watches chat for ToDs, and (when paired) syncs everything to LSManager in real time.

### Installation
1. Copy the `att` folder into your Ashita installation:  `Ashita/addons/att/`.
2. In game, load it:
   ```
   /addon load att
   ```
3. (Optional) auto-load on launch — add to your boot script:
   ```
   /addon load att
   ```

### Pair the addon to your linkshell — one time
The pairing flow is two short commands. You only run the *server* command on first install (or when the host URL changes).

**Step 1 — point the addon at your LSManager server**
```
/att server https://your-lsmanager-host
```
The addon probes the URL right away. You'll see one of:
- `Server OK (HTTP 401). Use /att link <code> [1|2] to pair.` — good, ready to pair.
- `Probe FAILED: ...` — URL is wrong or unreachable; check spelling and that the site is up.

**Step 2 — generate a pairing code on the website or Discord Activity**
- **Web:** Configurations → Customize Linkshell → Addon Tokens → *Generate pairing code*.
- **Discord:** Configurations tab → Addon Tokens → *+ Generate pairing code*.
- Codes expire in a few minutes. Copy the 8-character code shown.

**Step 3 — link the addon to a pearl slot**
```
/att link <code>           (defaults to LS1)
/att link <code> 1         (LS pearl slot 1 — main linkshell)
/att link <code> 2         (LS pearl slot 2 — second linkshell)
```
On success: `Linked to <Linkshell Name> on LS1 [optional label]`. The addon now syncs to that linkshell whenever you use the LS1 (or LS2) pearl in game.

You can pair **two different linkshells** — one to LS1, one to LS2 — and the addon picks the right one based on which pearl you're wearing.

### Check your status / unlink
```
/att status                List server URL and current pairings
/att list                   Same as status
/att unlink                 Unlink everything
/att unlink 1               Unlink LS1 only
/att unlink 2               Unlink LS2 only
/att unlink all             Same as bare /att unlink
```

### Day-to-day commands

**Open the launcher (the main UI window)**
```
/attend
```
This opens the launcher with: action bar, attendance roster, queued events from the web, break room, create-event, loot pool, and ToD capture panel. The local roster is seeded with you immediately. If paired, the latest queued events from the web also load automatically.

**Take attendance for a known event**
```
/att <event-alias>            e.g.  /att kirin    /att fafnir    /att "King Behemoth"
/att <alias> ls               Restrict to LS pearl 1
/att <alias> ls2              Restrict to LS pearl 2
/att <alias> h                Write to HNM log
/att <alias> e                Write to Event log
```
The addon issues `/sea <area> linkshell`, waits for the result list, builds attendance, then opens the attendance window. If paired, the result also posts to the web event.

**Take attendance for the current zone**
```
/att                          Use current zone, no /sea scan
/att here                     Resolve event by current zone and run it
```

**Take attendance everywhere**
```
/att all                      /sea all linkshell — global scan
```

**Show composition vs. roster**
```
/comp <event-alias>           Show needed composition for the event
/comp list                    List every event with a composition defined
```

**Help window**
```
/att help
```

### ToD capture (automatic)

Once paired, the addon listens to chat for "X was defeated by ..." lines from known HNM/NM. When it sees one:

1. Captures the monster name and timestamp.
2. Posts a ToD to the linked linkshell (LS1 or LS2 depending on which pearl you're wearing).
3. The ToD shows up immediately on the **Dashboard ToD Tracker** and the **ToDs tab** in both web and Discord — with countdown to repop window.

**Diagnose missed captures:**
```
/att tod debug                Toggle verbose capture logging
/att tod debug on             Force on
/att tod debug off            Force off
/att tod clear                Clear local capture cache
```

### Live event flow (when paired)

1. **Officer creates an event** — on web, Discord, or in-game (the launcher's *Create Event* panel).
2. **Members sign up** — from any of the three surfaces.
3. **Event starts** — once it's in commencement, attendance windows open.
4. **In-game**, anyone with the addon can run `/att <alias>` or `/att here` during each window. The addon scans, builds the roster, and posts attendance to the web.
5. **Breaks** — members can flag themselves as on-break from the launcher; officers can verify or moderate.
6. **Loot** — winners and DKP spent are recorded against the event and against the ToD if the kill was tied to one.
7. **Event ends** — it moves to **Event History** with the full attendance ledger and loot list.

### Debug & developer commands
```
/att debug          Toggle the addon debug window
/att debugmode      Toggle verbose console logging
/att memscan        Find the entity-list pointer (if memory broke after a patch)
/att memdump <addr> [count]
/att api            Dump entity manager metatable
/findoffset         Deep-scan for entity list
/apidump            Dump memory API methods
```

---

## Putting it all together — a typical Sky run

1. **Officer** — on the web or Discord Activity, *Events → Create Event* "Kirin", DKP 5/hr, 18 slots.
2. **Members** — sign up from Discord Activity or web.
3. **Raid leader** — in game, `/attend` to open the launcher and confirm the queued event is loaded.
4. **At pop** — leader runs `/att kirin ls h` — addon does `/sea limbus_zone linkshell`, builds the attendance, posts it to the live event, writes the local HNM log file.
5. **Kirin dies** — addon auto-captures the ToD and posts it to the linked linkshell.
6. **Loot rolls** — winners are recorded with DKP spent.
7. **Event ends** — leader closes it on web or Discord; the event moves to *Event History*; DKP balances update on every member's profile.
8. **Next pop window** — anyone glancing at the Dashboard sees the Kirin countdown ticking down to repop.

---

## Quick reference card

**Web** — `https://your-host/` → Dashboard → use the sidebar.
**Discord** — voice channel → Activities → LinkshellManager → tabs along the top.
**Addon** — `/addon load att` → `/att server <url>` → generate code on web/Discord → `/att link <code> [1|2]` → `/attend` to open the launcher.

**Most-used in-game commands:**
- `/attend` — open the launcher
- `/att <event>` — take attendance
- `/att here` — attendance for current zone
- `/att status` — show pairings
- `/att link <code> [1|2]` — pair to a linkshell
- `/att unlink [1|2|all]` — unpair
- `/att help` — open the in-game help window
