# LinkshellManager — Discord Activity Guide

> Same data as the web app, packaged for Discord. Launch it from any voice channel — no browser tab.

This guide covers the **Discord Activity**. Its companions have their own guides:
- **[Web App Guide](LSManager-WebApp-Guide.md)** — the same app in a browser, plus the website-only tools.
- **[In-Game Addon Guide](LSManager-Addon-Guide.md)** — the `lsm` Ashita addon.

Everything is shared across all three surfaces over one database.

---

## Launching it
Open any voice channel → click the **rocket icon → Activities → LinkshellManager**. It opens inside Discord.

### First launch
1. Discord prompts you to authorize the app (`identify`, `guilds`, `applications.commands`).
2. The app finds your linked account and drops you on the **Dashboard** tab.
3. Opening the site in a normal browser instead gives you this same app as the website (sign in with Discord there). See the [Web App Guide](LSManager-WebApp-Guide.md).

The top strip shows the active linkshell name and member count, a clock, a refresh button, and your role/name. Which tabs appear depends on the linkshell's **type** and **feature toggles**.

## The tabs

- **Dashboard** — Linkshell overview stat strip (members · items · revenue · upcoming events · upcoming auctions · upcoming ToDs) · Rules · Announcements · searchable Roster · **News & Updates** (newsy feed: announcements, rules, new auctions, new members, big DKP changes) · **Upcoming Events** · **ToD Tracker** (countdowns + loot history) · **HNM Claims** donut (7d / 30d / All) · **Recent Activity** (operational feed — filter All / Kills / Claims / Events / Loot). Officers can post rules and announcements inline.

- **Event System** — three views, switched at the top right:
  - **Current** — what is running now: in-progress events with attendance and break tracking, and open **attendance events** (the window/snapshot rosters captured in-game and the review rows an ended HNM camp hands off; review each roster, set DKP and post it, officers). Then **Unlinked Snapshots**, in two parts: *Awaiting confirmation* (captures members without permission sent with `/lsm now`, e.g. on a run no officer was at, showing who captured and sent each one; officers Confirm and file them, or Reject) and *Unlinked* (officers' captures not filed against an event yet). The queue of upcoming events you can sign up for sits below, and officers create events with **Create Event** under the view switch.
  - **Pending** — **Events Pending DKP Post**: ended events whose DKP hasn't been posted yet. The button shows how many are waiting.
  - **Past** — settled events, with comments and (officers) edits.

- **ToDs** — Add a ToD manually (mob, time, day, cooldown) from the full monster list. Grouped history per monster — expand for loot per kill. When a monster has an assigned **Party Setup**, an inline sign-up panel lets you claim a slot right there. For those who can approve them, **ToD Submissions Needing Approval** sits at the top: ToDs posted by members who can't add them directly, to fix up and Approve or Reject.

- **Party Setup** — Browse alliance → party → slot raid templates and claim an open slot (one per setup). Officers get the full tree editor: create / edit / delete a setup and assign it to a monster.

- **Auctions** *(DKP linkshells)* — Live bid view; place bids inline. The tab carries a live-auction count badge.

- **DKP** *(DKP linkshells)* — The DKP ledger and manual adjustments/audit (**Adjust**, **Add to a previous entry**, **Misc**), **plus** the **DKP Sheet**: every member's current, total and spent DKP, searchable.

- **Loot** — Loot history across events and ToDs (works for Loot Council linkshells too).

- **Management** — The linkshell's people. Full member roster: characters, ranks, status, DKP per linkshell, with an inline search. Officer tools for kicking, role changes, and invites.
- **Treasury** — Everything the linkshell owns, in three views picked at the top right. **Balance Sheet** — what came in and went out, and a simple balance sheet (what we have, what we owe — expandable to who — and what we're worth). **Items** — the stash, and marking things sold. **Transactions** — the searchable list of every entry, where Fix and Reverse live. Record Transaction is on both gil views. Reading is open to every member; recording gil needs "Record treasury entries" and changing the stash needs "Manage inventory". Selling an item records the gil in the same action, which is why the two live together. See [How the Treasury works](LSManager-WebApp-Guide.md#how-the-treasury-works-and-what-it-does-not-do) for what Fix and Reverse do, and why nothing is ever deleted.

- **Profile** — three views, switched at the top right:
  - **Jobs** — character name, alts, time zone, and each character's job levels, merits, gear/skill stars and relics.
  - **Crafts** — each character's craft levels.
  - **Player Ratings** — *What your linkshell thinks* (teammates' average gear/skill per job and their comments, for whichever of your characters you pick) beside **Rate teammates**.

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

The website has the same **Game Addon** tab.

> **Note:** Auctions, DKP, and Loot are full tabs along the top (they used to be right-edge side panels). Roster lookups live in the search box on both the Dashboard and Management tabs; invites are handled on the Management tab.

---

## Quick reference
- **Launch:** voice channel → Activities → LinkshellManager → tabs along the top.
- **Sign up for an event:** Event System tab → pick an event → Sign up.
- **Claim a party slot:** ToDs tab (or Party Setup tab) → open a setup with an assigned monster → claim a slot.
- **Pair the addon:** Configurations → Game Addon → + Get Code → in FFXI `/lsm link <code>`.
- **Companion guides:** [Web App](LSManager-WebApp-Guide.md) · [In-Game Addon](LSManager-Addon-Guide.md)
