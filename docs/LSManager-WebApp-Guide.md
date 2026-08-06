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

### Event System
- **Timed Events** — every active event (open for sign-ups, in progress, or scheduled). Snapshot/window-based attendance — fed by the in-game addon's `/lsm now` scans and by ending an HNM camp — also lands here: open attendance events and unlinked snapshots render at the top of this page, where you review each roster, set DKP, and post it.
- **Create Timed Event** *(officer)* — name, type (HNM / Event / Custom), location, start time, DKP per hour, requested slots.
- **Event History** — completed events with full attendance, breaks, and loot ledger.
- **Attendance History** — closed attendance events, searchable.
- **Pending Submissions** *(officer)* — addon submissions awaiting review.

*The attendance items are hidden when Linkshell type = Sky/Sea/Dynamis, which runs timed events only.*

### ToD Tracker
- **View ToDs** — every tracked NM/HNM with countdown to repop, claim status, and loot history.
- **Add ToD** *(officer)* — manually log a kill (the addon does this automatically — see the [Addon Guide](LSManager-Addon-Guide.md)).

### Party Setups
- **View Party Setups** — alliance → party → slot raid templates; claim an open slot. You hold at most one slot per setup.
- **Create Party Setup** *(officer)* — build the template (job/role requirements, party leaders) and assign it to a monster so it appears on that monster's ToD card.

### Management
*The linkshell's people.*
- **Manage Team** *(officer)* — View Team, Add Members (search players), View Invites.
- **DKP** — **View DKP** (ledger), **DKP Adjustments** *(officer — Adjust / Add to a previous entry / Misc)*, and **App DKP Sheet** (read-only view of the synced Google Sheet, Main + Tally tabs).
- **Loot** — **Loot History**; Add Loot *(officer)*.
- **Configurations** — **View Linkshells**, **Create Linkshell**, **Customize Linkshell** *(officer)*, **Google Sheet Integration** *(officer)*, **Reconcile Members** *(officer)*.

### Treasury
*Everything the linkshell owns. Both halves are readable by every member; only changing them needs a
permission.*
- **Gil** — **Gil on hand** (a simple balance sheet plus every transaction), **Record Transaction**
  *(needs "Record treasury entries")*.
- **Items** — **View Items**, **Add Item** *(needs "Manage inventory")*.

Gil and Items sit together because one turns into the other: marking an item sold records the gil
against the treasury in the same transaction.

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
- **Feature toggles** — hide any section your linkshell doesn't use (Endgame, Missions, Auctions, ToDs, Events, DKP, Items tile, Finances tile).
- **Hide ToD Mobs** — pick monsters (grouped HNM / Sky / Other) to hide from the Tracked Windows panel on the Dashboard and the ToD views. Pop-only mobs (Sky Gods, Sea NMs, HENMs) aren't listed — they spawn from an item, not a repop timer, so they have no window to track.
- **Addon pairing codes** — generate one-time codes the in-game addon redeems to link itself to this linkshell.

### How the Treasury works, and what it does not do

`Management → Finances → Treasury` tracks the linkshell's gil. Behind the scenes every transaction is
recorded twice — once against gil on hand and once against a category — so the two always cancel out and
the balance can be worked out from the entries rather than stored and hoped over. You never see that
part: you pick one plain-English option from **What happened?** and the app does the rest. If you are
curious, **Show the bookkeeping details** on any entry opens up the two halves.

The top card is a **simple balance sheet**: what we have (gil on hand, plus anything owed to us),
what we owe, and what that leaves — *what we're worth*. Every line is shown even when it is zero,
because a liability that vanishes at zero reads as "there is no such thing" rather than "there is
none right now". **Owed to members** expands to show exactly who is still waiting on gil and how
much each, and those rows always add up to the figure above them.

Things worth knowing:

- **Nothing is ever deleted.** Once an entry is confirmed it cannot be edited. **Fix** records what it
  should have said and cancels the original in one step; **Reverse** just cancels it. Either way the
  original stays in the list, marked *Reversed*, so what was believed and when is still readable.
- **Drafts are for typos.** Save as draft while you check something; a draft is editable, does not count
  toward the balance, and can be thrown away. Confirming it is what puts it on the books.
- **Gil in and gil out are recorded once**, when the gil moves — or, if you use the "gil promised but not
  moved yet" options, when the promise is made. Selling an item and closing a gil auction record
  themselves.
- **An item listed on the auction house records nothing.** There is no buyer and no agreed price yet, so
  there is nothing to record until it sells. It stays in Inventory until then.
- **One lump sum can be shared between several members.** Pick **Split gil among several members**
  (the gil leaves now) or **We owe several members a split** (it does not), enter the total, and tick
  everyone getting a share. The gil is divided evenly, and because gil is whole numbers it usually
  does not divide exactly — 1,000,000 across three is 333,334 / 333,333 / 333,333. The extra gil goes
  to whoever comes first alphabetically, so the shares always add back up to the total. The form shows
  who gets what before you confirm. The whole thing is **one entry**, so reversing or fixing it acts
  on the whole payout rather than on one person's share. If you used the "we owe" version, settle each
  person with **We paid a member what we owed** as you catch them online.

Two limits, stated plainly rather than left to be discovered:

- **One officer can record, fix and reverse on their own.** There is no second signature, because most
  linkshells run on one or two officers. Every member can read the treasury, and that is the check: the
  history is public to the linkshell.
- **The app cannot see the mule.** A character's actual gil is not readable by anything outside the game,
  so **Check gil on hand** is you counting it and telling the app. The difference is recorded as its own
  entry rather than quietly adjusting the balance.

### Google Sheet Integration *(web only)*
`Configurations → Google Sheet Integration` connects a Google account so DKP syncs out to a spreadsheet, with import/preview and member reconciliation. This lives **only** in the web app — the OAuth redirect flow doesn't run inside the Discord iframe. Once connected, members can read the synced sheet from the **App DKP Sheet** page (web) or the **DKP** tab (Discord Activity).

---

## Quick reference
- **URL:** `https://your-host/` → Login with Discord → use the left sidebar.
- **Set up loot:** Configurations → Customize Linkshell → Loot structure + Linkshell type → Save.
- **Connect a sheet:** Configurations → Google Sheet Integration → Connect Google.
- **Pair the addon:** Configurations → Customize Linkshell → Addon pairing → generate code → in FFXI `/lsm link <code>`.
- **Companion guides:** [Discord Activity](LSManager-DiscordActivity-Guide.md) · [In-Game Addon](LSManager-Addon-Guide.md)
