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

The **Linkshell roster** card lists every member with their rank, DKP, and status. An **App** tag beside a name means that member has opened and synced the app at least once. Tick **Show Jobs** to swap Rank/DKP/Status for each member’s leveled jobs — main and alts, with merit stars and relic pills — and the search box then matches job names and levels too. This mirrors the Discord Activity’s dashboard roster.

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
- **DKP rounding** — Quarter (0.25) or Half (0.5) increments.
- **Feature toggles** — hide any section your linkshell doesn't use (Endgame, Missions, Auctions, ToDs, Events, DKP, Items tile, Finances tile).
- **Hide ToD Mobs** — pick monsters (grouped HNM / Sky / Other) to hide from the Tracked Windows panel on the Dashboard and the ToD views. Pop-only mobs (Sky Gods, Sea NMs, HENMs) aren't listed — they spawn from an item, not a repop timer, so they have no window to track.
- **Addon pairing codes** — generate one-time codes the in-game addon redeems to link itself to this linkshell.

### How the Treasury works, and what it does not do

`Management → Finances → Treasury` tracks the linkshell's gil. Behind the scenes every transaction is
recorded twice — once against gil on hand and once against a category — so the two always cancel out and
the balance can be worked out from the entries rather than stored and hoped over. You never see that
part: you say **what happened** in two clicks and the app does the rest. If you are curious,
**Show the bookkeeping details** on any entry opens up the two halves.

**Recording something** is a button, and then a sentence. There are no categories to pick between:

| Button | What it does | What it asks |
|---|---|---|
| **Gil In** | Gil on hand goes **up** | The amount, and why — in your own words |
| **Gil Out** | Gil on hand goes **down** | The amount, and why — in your own words |
| **Split Gil** | Sets up a payout run — see below | Who is getting a share |
| **Owed to us** | Someone owes us, or has now paid | Which of the two, and who |
| **We Owe a Member** | Gil promised to one person, not handed over yet | Who, and how much |

The **Reason** box is the whole of the question for Gil In and Gil Out. Whatever you type is what
the transactions list shows, so "sold the Osode to Skid" and "food and echoes for Sky" are the
record — not a category somebody has to translate afterwards. There is no separate Note field: one
box, and it is the one you are looking at.

The three buttons on the right are the ones that are **not** simply gil moving, which is why they
are not Gil In or Gil Out reasons. Split Gil and We Owe a Member record gil you have promised but
not handed over; Owed to us records gil promised *to* you. None of them touch gil on hand until
someone is ticked off a list.

One kind of entry the app writes for you and labels precisely: selling an item out of the
**stockpile** records **Gil In — Stockpile Item Sold** with the price, because the app already knows
what it was and does not have to ask.

The top card is a **simple balance sheet**: what we have (gil on hand, plus anything owed to us),
what we owe, and what that leaves — *what we're worth*. Every line is shown even when it is zero,
because a liability that vanishes at zero reads as "there is no such thing" rather than "there is
none right now". **Owed to members** expands to show exactly who is still waiting on gil and how
much each, and those rows always add up to the figure above them.

Things worth knowing:

- **Nothing is ever deleted.** Once an entry is confirmed it cannot be edited. **Fix** records what it
  should have said and cancels the original in one step; **Reverse** just cancels it. Either way the
  original stays in the list — marked *Fixed* or *Reversed* — so what was believed and when is still
  readable. The **Fixed** and **Reversed** chips split the same way, so a corrected typo never buries
  the entries you actually called off.
- **Drafts are for typos.** Save as draft while you check something; a draft is editable, does not count
  toward the balance, and can be thrown away. Confirming it is what puts it on the books.
- **Gil in and gil out are recorded once**, when the gil moves. Selling an item and closing a gil
  auction record themselves.
- **An item listed on the auction house records nothing.** There is no buyer and no agreed price yet, so
  there is nothing to record until it sells. It stays in Inventory until then.
- **A cost is counted when you promise it, not when you hand it over.** Setting up a payout list is
  what moves **Gil out** and **Net change**; ticking someone off afterwards moves **Gil on hand** and
  **We owe**. Each figure moves exactly once. If you are watching Gil out expecting it to jump when
  you press the pay button, it will not.

**Splitting gil between members** is one action with one flow. Pick **Split Gil**, enter the total,
and tick everyone getting a share: that sets up a **payout list**. Nobody has been paid yet — gil on
hand does not change, and everyone lands on the **Owed to members** list on the balance sheet.

The gil is divided evenly, and because gil is whole numbers it usually does not divide exactly —
1,000,000 across three is 333,334 / 333,333 / 333,333. The extra gil goes to whoever comes first
alphabetically, so the shares always add back up to the total. The form shows who gets what before
you confirm. The whole thing is **one entry**, so reversing or fixing it acts on the whole payout
rather than on one person's share.

**Owing one person** does not need the split at all: press **We Owe a Member**, name them, and enter
the amount. It records the same obligation a one-person split would — gil on hand does not move,
they land on **Owed to members**, and the tick-and-pay panel below is what finally hands the gil
over. Its own button rather than a Gil Out reason, because nothing has left the treasury yet.

**Paying people off that list** is the tick-and-pay panel under **Owed to members**: tick whoever
you have actually handed the gil to, and press **Record payment**. It fills in what each person is
owed, tells you the total before you commit, and settles each of them in full. That panel is the
only way to settle up — there is no option in the picker for it, because there only ever needed to
be one way to do it.

Two things it will not let you get wrong. If someone else records more gil owed to a person while
your page is open, that row is **skipped rather than paid** — you would otherwise hand over a
figure you never agreed to — and it tells you which one and why; everyone else you ticked is still
paid. And if you recorded a debt **by mistake**, do not pay it off: **Fix** or **Reverse** the
original entry instead. Paying it tells the books gil left the treasury when it did not.

Two limits, stated plainly rather than left to be discovered:

- **One officer can record, fix and reverse on their own.** There is no second signature, because most
  linkshells run on one or two officers. Every member can read the treasury, and that is the check: the
  history is public to the linkshell.
- **The app cannot see the mule.** A character's actual gil is not readable by anything outside the
  game, so the balance is only ever what has been *recorded*. If you count the mule and the books
  disagree, record the gap as an ordinary **Gil In** or **Gil Out** and say so in the reason box —
  "counted the mule, 150k short". There is no separate reconciliation ceremony, and no
  starting-balance ritual either: a linkshell adopting the app records what it already had as one
  **Gil In** saying exactly that.

### Google Sheet Integration *(web only)*
`Configurations → Google Sheet Integration` connects a Google account so DKP syncs out to a spreadsheet, with import/preview and member reconciliation. This lives **only** in the web app — the OAuth redirect flow doesn't run inside the Discord iframe. Once connected, members can read the synced sheet from the **App DKP Sheet** page (web) or the **DKP** tab (Discord Activity).

---

## Quick reference
- **URL:** `https://your-host/` → Login with Discord → use the left sidebar.
- **Set up loot:** Configurations → Customize Linkshell → Loot structure → Save.
- **Connect a sheet:** Configurations → Google Sheet Integration → Connect Google.
- **Pair the addon:** Configurations → Customize Linkshell → Addon pairing → generate code → in FFXI `/lsm link <code>`.
- **Companion guides:** [Discord Activity](LSManager-DiscordActivity-Guide.md) · [In-Game Addon](LSManager-Addon-Guide.md)
