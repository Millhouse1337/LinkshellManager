# LinkshellManager — Web App Guide

> The full management surface. Roster, DKP, items, revenue, auctions, events, ToDs, and party setups — all in the browser.

**LinkshellManager (LSManager)** is a three-piece system over one shared database. This guide covers the **Web App**. Its companions have their own guides:

| Piece | Where it lives | Guide |
| --- | --- | --- |
| **Web App** | `https://your-host/` (any browser) | *(this guide)* |
| **Discord Activity** | Inside Discord — voice channel "Activities" tray | [Discord Activity Guide](LSManager-DiscordActivity-Guide.md) |
| **In-Game Addon** (`lsm`) | Ashita v3 / HorizonXI client | [In-Game Addon Guide](LSManager-Addon-Guide.md) |

Everything is shared — anything done on the addon or in Discord shows up in the web in seconds.

---

## Getting in
- Open the site in a browser.
- Click **Login with Discord** — accounts are linked to your Discord identity automatically.
- First-time login creates your account. You'll be prompted to set your **character name** (and optionally up to two **alt character names**) on the Profile page.

## Sidebar map

The left sidebar is grouped into sections. Sub-items marked *(officer)* require a role permission and won't appear for regular members. Some groups appear only depending on your linkshell's **type** and **feature toggles** (see Customize Linkshell).

### Dashboard
A snapshot of your active linkshell — member count, upcoming events, upcoming auctions, upcoming ToDs, and revenue (if enabled). Below the stat strip are **two distinct feeds**:
- **News & Updates** — the "newsy" feed: new announcements, rules, auctions, members who just joined, and large DKP changes.
- **Recent Activity** — the "operational" feed (filter by Kills / Claims / Events / Loot): ToD kills and claims, loot awarded, and completed events.

The **Upcoming Events** card also surfaces ToD repops opening within the next 2 hours, tagged as repops, so you see what's about to pop alongside scheduled events.

### Announcements
- **View Announcements** — scrollable feed for everyone in the linkshell.
- **Create Announcement** *(officer)* — post news.

### Rules
- **View Rules** — your linkshell's published rule set.
- **Create Rule** *(officer)* — add or edit rules.

### Missions *(optional)*
Navigation placeholders for **Rise of the Zilart / Chains of Promathia / Treasures of Aht Urhgan**.

### End Game *(optional)*
Navigation placeholders for **Sky / Sea / Dynamis / Limbus**.

### Linkshell Auction
- **View Auctions** — current live and upcoming auctions.
- **Create Auction** *(officer)* — list an item with starting bid, min increment, and end time.
- **Auction History** — every settled auction with winner and final DKP.

### Event System *(timed-event linkshells; hidden when Linkshell type = HNM Only)*
- **Timed Events** — every active event (open for sign-ups, in progress, or scheduled).
- **Create Timed Event** *(officer)* — name, type (HNM / Event / Custom), location, start time, DKP per hour, requested slots.
- **Event History** — completed events with full attendance, breaks, and loot ledger.

### Attendance System
Snapshot/window-based attendance, fed by the in-game addon's `/sea` scans.
- **Attendance Events** — each captured attendance snapshot; review the roster, set DKP, and post it.
- **Attendance History** — past snapshots.
- **Pending Submissions** *(officer)* — addon submissions awaiting review.

### ToD Tracker
- **View ToDs** — every tracked NM/HNM with countdown to repop, claim status, and loot history.
- **Add ToD** *(officer)* — manually log a kill (the addon does this automatically — see the [Addon Guide](LSManager-Addon-Guide.md)).

### Party Setups
- **View Party Setups** — alliance → party → slot raid templates; claim an open slot. You hold at most one slot per setup.
- **Create Party Setup** *(officer)* — build the template (job/role requirements, party leaders) and assign it to a monster so it appears on that monster's ToD card.

### Management
- **Manage Team** *(officer)* — View Team, Add Members (search players), View Invites.
- **Manage Items** — View Items; Add Item *(officer)*.
- **Manage Revenue** *(officer)* — View Income, Add Income.
- **DKP** — **View DKP** (ledger), **DKP Adjustments** *(officer — Adjust / Add to a previous entry / Misc)*, and **App DKP Sheet** (read-only view of the synced Google Sheet, Main + Tally tabs).
- **Loot** — **Loot History**; Add Loot *(officer)*.
- **Configurations** — **View Linkshells**, **Create Linkshell**, **Customize Linkshell** *(officer)*, **Google Sheet Integration** *(officer)*, **Reconcile Members** *(officer)*.

### Pages
- **Profile** — character name, alts, time zone, profile image, primary linkshell.
- **Messages** — internal messaging.

## Customize Linkshell (the control panel)
`Configurations → Customize Linkshell` (officer only):

- **Loot structure**
  - **DKP** — time-based DKP per event, spent on items.
  - **Loot Council** — leader awards items; no DKP.
  - **Percentage Based** — DKP earned normally; loot deducts a % of the winner's balance.
- **Linkshell type** — tells the app and addon what your linkshell runs:
  - **Sky/Sea/Dynamis etc.** — timed-event experience only; no HNM presets.
  - **HNM Only** — HNM snapshot sessions only; hides the Event System.
  - **Both** — timed events and HNM (default).
- **DKP rounding** — Quarter (0.25) or Half (0.5) increments.
- **Feature toggles** — hide any section your linkshell doesn't use (Endgame, Missions, Auctions, ToDs, Events, DKP, Items tile, Revenue tile).
- **Hide ToD Mobs** — pick monsters (grouped HNM / Sky / Sea / HENM / Other) to hide from the Tracked Windows panel on the Dashboard and the ToD views.
- **Addon pairing codes** — generate one-time codes the in-game addon redeems to link itself to this linkshell.

### Google Sheet Integration *(web only)*
`Configurations → Google Sheet Integration` connects a Google account so DKP syncs out to a spreadsheet, with import/preview and member reconciliation. This lives **only** in the web app — the OAuth redirect flow doesn't run inside the Discord iframe. Once connected, members can read the synced sheet from the **App DKP Sheet** page (web) or the **DKP** tab (Discord Activity).

---

## Quick reference
- **URL:** `https://your-host/` → Login with Discord → use the left sidebar.
- **Set up loot:** Configurations → Customize Linkshell → Loot structure + Linkshell type → Save.
- **Connect a sheet:** Configurations → Google Sheet Integration → Connect Google.
- **Pair the addon:** Configurations → Customize Linkshell → Addon pairing → generate code → in FFXI `/lsm link <code>`.
- **Companion guides:** [Discord Activity](LSManager-DiscordActivity-Guide.md) · [In-Game Addon](LSManager-Addon-Guide.md)
