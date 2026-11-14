# LinkshellManager — Web App Guide

> The same app as the Discord Activity, in your browser — plus the tools that need a full browser.

**LinkshellManager (LSManager)** is a three-piece system over one shared database. This guide covers the **Web App**. Its companions have their own guides:

| Piece | Where it lives | Guide |
| --- | --- | --- |
| **Web App** | `https://your-host/` (any browser) | *(this guide)* |
| **Discord Activity** | Inside Discord — voice channel "Activities" tray | [Discord Activity Guide](LSManager-DiscordActivity-Guide.md) |
| **In-Game Addon** (`lsm`) | Ashita v3 / HorizonXI client | [In-Game Addon Guide](LSManager-Addon-Guide.md) |

Everything is shared — anything done on the addon or in Discord shows up on the website in seconds.

---

## Getting in
- Open the site in a browser and click **Sign in**. You sign in with Discord, so it's the same account as the Discord Activity.
- First time? Set your **character name** (and optionally up to two **alt character names**) on the **Profile** tab.
- **Sign out** is at the top right. Signed in, the site's home address takes you straight into the app.

## The tabs

The website is the same app as the Discord Activity, with the same tabs along the top: Dashboard,
Charts, Auctions, Event System, ToDs, Party Setup, DKP, Loot, Management, Treasury, Profile, Messages,
Configurations, Permissions and Game Addon. The [Discord Activity Guide](LSManager-DiscordActivity-Guide.md#the-tabs)
describes each one. Which tabs appear depends on your linkshell's **feature toggles**, and the buttons
inside them on your role's **permissions** (Configurations → Permissions).

A few things are nicer in a browser:

- **Every tab has its own address**, so you can bookmark it, share it, reload, and use Back. For example
  `/discord-activity/events/past` is the Event System's Past view and `/discord-activity/dkp/history` the DKP History (the DKP tab itself opens on the Sheet).
- **Old website links still work.** Bookmarks and old Discord posts pointing at the previous website's
  pages (such as `/Event` or `/LootHistory`) open the matching tab.
- **"Lock to this server" doesn't apply on the website.** That Configurations setting only limits which
  Discord server the Activity opens a linkshell from; members always see their linkshells on the website.

## Importing and exporting DKP

Both work on the website and inside Discord. Discord can't save a file itself, so there a download
(Export to Excel, or the import template) opens in your web browser through a link that works once and
expires after a few minutes; no sign-in needed in the browser.

### Import DKP *(DKP tab → Import DKP)*
Bringing a linkshell over from another tracker? Upload a spreadsheet (.xlsx or .csv) of your existing DKP.
**Download template** shows the exact columns (Member Name, Alt 1, Alt 2, Current DKP, Total DKP, Total DKP
Spent). **Preview import** lists every row as an update, a new member, or skipped, and flags negative
balances (they import as 0). Nothing changes until you press **Import**. Members already in the app are
updated; names it doesn't recognise become unsynced members who keep their DKP until that person first
opens the app and claims it. Needs the "Customize linkshell" permission.

### Export to Excel *(DKP → DKP Sheet)*
Downloads the linkshell's current DKP as a styled spreadsheet.

## Only on the website

These are tools for the one super-admin account, so they don't appear inside Discord. They show as extra
tabs after a divider at the end of the tab strip.

### Admin *(super admin only)*
The server-wide switches: turn the in-game addon or its Claim Shield capture off for everyone, turn the
**admin override** on or off, and show or hide the **launcher download** link on the home page (its address
is fixed in the server config, `Launcher__DownloadUrl`). It also links
to **Data Admin**.

### Data Admin *(super admin only)*
A browser over the database for the one account marked super admin, on its own tab at the end of the tab
strip (below the diagram there is also a plain list of every table). It opens on the **schema diagram**
(below): select a table there (click its card, or pick it in the panel) and **View records** (the bar
button, the panel, or V) opens its list page, with search, sort, filters and paging; Back from any
records page returns to the diagram with that table still selected. From the list, a details page shows
every column, its foreign keys link to the row they point at, and it lists every table with rows pointing
back; where the table allows it there are add/edit/delete pages too. The delete page shows what else goes
with the row (deleted, unlinked, or blocking the delete) before you confirm. Ledgers and history tables
are read-only, credentials and images are never shown, and `Linkshell` and `App User` can never be
deleted from here.

**Schema diagram** (the Data Admin landing page) draws the whole database as one
picture: a card per table listing its columns and keys, and a line for every foreign key, whose ends
show one-to-many or one-to-one and whose style shows what a delete does (solid cascades, dashed
unlinks, dotted blocks). Drag the background to pan, scroll to zoom, drag a table by its header to
move it (the browser remembers where you left it), and hover or click a table or a line to light up
what it connects to. The side panel searches tables and columns, lists every relationship and
many-to-many pairing, and holds the view options (keys only, square lines, hide the login tables);
**Focus**, or a double-click on a table, shows just that table and its neighbours until you press
Esc. The diagram itself is structure only, never rows: View records opens an app table's list page, and
the login (Identity) tables are drawn but have no records page.

## Configurations (the control panel)
The **Configurations** tab (needs the "Customize linkshell" permission):

- **Loot structure**
  - **DKP** — time-based DKP per event, spent on items.
  - **Loot Council** — leader awards items; no DKP.
  - **Percentage Based** — DKP earned normally; loot deducts a % of the winner's balance.
- **DKP rounding** — Quarter (0.25) or Half (0.5) increments.
- **Feature toggles** — hide any section your linkshell doesn't use (Charts, Auctions, ToDs, Events, DKP, Treasury).
- **Hide ToD Mobs** — pick monsters (grouped HNM / Sky / Other) to hide from the Tracked Windows panel on the Dashboard and the ToD views. Pop-only mobs (Sky Gods, Sea NMs, HENMs) aren't listed — they spawn from an item, not a repop timer, so they have no window to track.
- **Addon pairing codes** — on the **Game Addon** tab: generate one-time codes the in-game addon redeems to link itself to this linkshell.

### How the Treasury works, and what it does not do

The **Treasury** tab tracks the linkshell's gil. Behind the scenes every transaction is
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

---

## Quick reference
- **URL:** `https://your-host/` → Sign in with Discord → tabs along the top.
- **Set up loot:** Configurations → Loot structure → Save.
- **Import existing DKP:** DKP → Import DKP → Download template → fill it in → Preview import → Import.
- **Review members' /lsm now captures:** Event System → Current (Unlinked Snapshots). **Members' ToD submissions:** ToDs tab.
- **Pair the addon:** Game Addon → + Get Code → in FFXI `/lsm link <code>`.
- **Companion guides:** [Discord Activity](LSManager-DiscordActivity-Guide.md) · [In-Game Addon](LSManager-Addon-Guide.md)
