# LinkshellManager — Discord Activity Guide

> Same data as the web app, packaged for Discord. Launch it from any voice channel — no browser tab.

This guide covers the **Discord Activity**. Its companions have their own guides:
- **[Web App Guide](LSManager-WebApp-Guide.md)** — the full management surface in a browser.
- **[In-Game Addon Guide](LSManager-Addon-Guide.md)** — the `lsm` Ashita addon.

Everything is shared across all three surfaces over one database.

---

## Launching it
Open any voice channel → click the **rocket icon → Activities → LinkshellManager**. It opens inside Discord.

### First launch
1. Discord prompts you to authorize the app (`identify`, `guilds`, `applications.commands`).
2. The app finds your linked account and drops you on the **Dashboard** tab.
3. Visiting `/discord-activity` directly in a browser (outside Discord) gives a **standalone preview** with the same UI.

The top strip shows the active linkshell name and member count, a clock, a refresh button, and your role/name. Which tabs appear depends on the linkshell's **type** and **feature toggles**.

## The tabs

- **Dashboard** — Linkshell overview stat strip (members · items · revenue · upcoming events · upcoming auctions · upcoming ToDs) · Rules · Announcements · searchable Roster · **News & Updates** (newsy feed: announcements, rules, new auctions, new members, big DKP changes) · **Upcoming Events** · **ToD Tracker** (countdowns + loot history) · **HNM Claims** donut (7d / 30d / All) · **Recent Activity** (operational feed — filter All / Kills / Claims / Events / Loot). Officers can post rules and announcements inline.

- **Event System** — Upcoming events you can sign up for, plus in-progress events with attendance and break tracking. **Current Field Activity** also lists open **attendance events** — the window/snapshot rosters captured in-game and the review rows an ended HNM camp hands off; review each snapshot's roster, set DKP, and post it (officers), and posted ones can still be edited. Unlinked snapshots and closed attendance events sit below the queue.

- **ToDs** — Add a ToD manually (mob, time, day, cooldown) from the full monster list. Grouped history per monster — expand for loot per kill. When a monster has an assigned **Party Setup**, an inline sign-up panel lets you claim a slot right there.

- **Party Setup** — Browse alliance → party → slot raid templates and claim an open slot (one per setup). Officers get the full tree editor: create / edit / delete a setup and assign it to a monster.

- **Auctions** *(DKP linkshells)* — Live bid view; place bids inline. The tab carries a live-auction count badge.

- **DKP** *(DKP linkshells)* — The DKP ledger and manual adjustments/audit (**Adjust**, **Add to a previous entry**, **Misc**), **plus** the read-only synced **Google Sheet** (Main + Tally tabs, searchable). Connect the sheet on the web.

- **Loot** — Loot history across events and ToDs (works for Loot Council linkshells too).

- **Management** — The linkshell's people. Full member roster: characters, ranks, status, DKP per linkshell, with an inline search. Officer tools for kicking, role changes, and invites.
- **Treasury** — Everything the linkshell owns, in two sections. **Gil** — a simple balance sheet (what we have, what we owe — expandable to who — and what we're worth), what came in and went out, and the list of transactions. **Items** — the stash, and marking things sold. Reading is open to every member; recording gil needs "Record treasury entries" and changing the stash needs "Manage inventory". Selling an item records the gil in the same action, which is why the two live together. See [How the Treasury works](LSManager-WebApp-Guide.md#how-the-treasury-works-and-what-it-does-not-do) for what Fix and Reverse do, and why nothing is ever deleted.

- **Profile** — character name, alts, time zone, profile image, primary linkshell.

- **Messages** — internal inbox.

- **Configurations** *(officer/leader)* — see below.

- **Endgame / Missions** *(optional, feature-toggled)* — navigation placeholders ("Feature Coming Soon").

## Configurations tab *(officer/leader)*
- **Switch Linkshells** — change which linkshell is active (when you belong to more than one).
- **Create a New Linkshell** — start one and invite members.
- **Customize Linkshell** — Loot structure, DKP rounding increment, Feature toggles, and **Hide ToD Mobs** (grouped, collapsible).
- **Permissions** — the role/permission matrix; create, edit, and delete roles.
- **Game Addon (lsm)** — generate one-time pairing codes for the in-game addon.

### Generating an addon pairing code (Discord)
1. Switch to the **Configurations** tab.
2. Under **Game Addon (lsm)**, click **+ Get Code**.
3. Pick the **linkpearl slot** (Slot 1 or Slot 2) the code should bind to.
4. Click **Generate** — the code displays with a countdown. Copy it before it expires.
5. In FFXI, type `/lsm link <code>` (see the [Addon Guide](LSManager-Addon-Guide.md)).

You can also generate codes from the web app at **Configurations → Customize Linkshell → Addon pairing**.

> **Note:** Auctions, DKP, and Loot are full tabs along the top (they used to be right-edge side panels). Roster lookups live in the search box on both the Dashboard and Management tabs; invites are handled on the Management tab.

---

## Quick reference
- **Launch:** voice channel → Activities → LinkshellManager → tabs along the top.
- **Sign up for an event:** Event System tab → pick an event → Sign up.
- **Claim a party slot:** ToDs tab (or Party Setup tab) → open a setup with an assigned monster → claim a slot.
- **Pair the addon:** Configurations → Game Addon → + Get Code → in FFXI `/lsm link <code>`.
- **Companion guides:** [Web App](LSManager-WebApp-Guide.md) · [In-Game Addon](LSManager-Addon-Guide.md)
