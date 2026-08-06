```
╔══════════════════════════════════════════════════════════════════════╗
║                                                                      ║
║                    LSM ADDON · QUICK REFERENCE                       ║
║                                                                      ║
║              Commands · Snapshot Tie-Ins · De-Duplication            ║
║                                                                      ║
╚══════════════════════════════════════════════════════════════════════╝
```

> **What this is.** A one-page reference for officers using the in-game
> `lsm` addon — every command it registers, how the `/lsm now <name>`
> snapshot ties into the Window Events page, and how the server avoids
> double-counting attendance when you snap the same fight twice.

---

## ▌ PANEL 1 · COMMAND REFERENCE

### Web sync / pairing

| Command | What it does |
|---|---|
| `/lsm server <url>` | Set the LSManager web server URL. |
| `/lsm link <code> [1\|2]` | Redeem a pairing code on slot 1 or 2 (default 1). |
| `/lsm unlink [1\|2\|all]` | Drop a slot pairing (or all). |
| `/lsm status` | Show server + pairings. Auto-drops revoked tokens. |

### Alliance / party / zone snapshot

| Command | What it does |
|---|---|
| `/lsm now [name] [p\|z\|a]` | Write a local CSV **and** push the same payload to LSManager. Optional **name** labels it in the web UI. Trailing scope: `p` = own party (slots 0-5), `z` = everyone in current zone, `a` (default) = full alliance. Quoted names with spaces work: `/lsm now "Fafnir Window 2" p`. |

### Windows / panels

| Command | What it does |
|---|---|
| `/attend` | Toggle the main LSM launcher window. |
| `/attend close` | Close the launcher. |
| `/tod` | Toggle the standalone ToD Tracker window. |
| `/tod close` | Close the ToD Tracker window. |
| `/lsm todtracker` | Alias for `/tod`. |
| `/lsm todtracker debug` | Toggle verbose pop-signal attribution logging. |

### Diagnostics

| Command | What it does |
|---|---|
| `/lsm claimshield` | Print current Claim Shield capture status. |
| `/lsm claimshield debug` | Toggle verbose Claim Shield capture logging. |

### Help

| Command | What it does |
|---|---|
| `/lsm help` | Print the full command list + quick-start in chat. Also runs as the fallback for bare `/lsm` or an unrecognized subcommand. |

> 💡 **Quick start:** `/lsm server <url>` → `/lsm link <code>` → `/attend`.

---

## ▌ PANEL 2 · `/lsm now <name>` — MONSTER-NAME TIE-IN

When you call `/lsm now Fafnir`, the addon ships the name verbatim. The
server then **auto-tags the Window Event's Entry Type** so officers don't
have to set it manually for every kill. The mapping lives in
`WindowEventEntryTypes.FromMonsterName()` and is case- and
whitespace-insensitive.

```
┌──────────────────────────────────────────────────────────────┐
│   YOU TYPE                       →   ENTRY TYPE              │
├──────────────────────────────────────────────────────────────┤
│   Tiamat · Jormungand · Vrtra    →   Wyrms Camp              │
│                                                              │
│   Adamantoise · Aspidochelone    →   Kings Camp              │
│   Behemoth · Fafnir                                          │
│   King Behemoth · Nidhogg                                    │
│                                                              │
│   anything else                  →   Misc Camp               │
│   (including no name)                                        │
└──────────────────────────────────────────────────────────────┘
```

> 📌 **Why this matters.** Entry Type is what the Google Sheet's AttInput
> formula pivots on. Typing `/lsm now Fafnir` lands as a **Kings Camp** row
> on the sheet automatically — no officer hand-edit needed.

> ⚠️ **Note.** Jormungand is intentionally only in the **Wyrms** set even
> though FFXI lore puts it on both lists. Wyrms wins per the linkshell
> convention.

> 🚫 **What it does NOT auto-detect.**
> - Does **not** auto-switch to HNM-style multi-window mode (`/lsm now` is
>   always a single-window snapshot).
> - Does **not** set DKP rate — that's applied per-window from the Window
>   Events page or inherited from a linked Event.
> - Does **not** consult `HnmConfig.LongWindowHnms` / `ShortWindowHnms` —
>   that list only feeds the ToD Tracker.

---

## ▌ PANEL 3 · TWO `/lsm now` CALLS A MINUTE APART

The server uses **two independent mechanisms** to keep repeat snapshots
sane: one *groups* them onto the same Window Event, the other *flags*
near-duplicates so nobody gets DKP twice.

### A) Grouping into the same Window Event

`FindOrCreateWindowEventAsync` reuses an existing Window Event when
**all four** conditions hold:

| Condition | Detail |
|---|---|
| **Same linkshell** | `LinkshellId` must match the caller's pairing |
| **Still open** | `Status == Open` — closed events never absorb new snaps |
| **Same normalized name** | trim → collapse whitespace → uppercase. `"Fafnir"`, `"fafnir"`, `" Fafnir "` all match |
| **Not stale** | `LastCapturedAtUtc ≥ capturedAt − 21 hours` |

So `/lsm now Fafnir` and again 1 minute later → **same Window Event**,
two snapshots attached as siblings. The stale clock slides forward
each time.

```
                  21h
   ┌────────────────────────────────────┐
   │           SAME WINDOW EVENT        │      (FRESH WINDOW EVENT)
   │                                    │
   ●──●─●────────●───●───────────────●─ │  ─────────●──
   ↑              ↑                  ↑                ↑
  first       same name           still <21h       22h gap →
 /lsm now     1m later             attaches        new event
```

### B) Folding close-together posts into one roster

Nothing is flagged as a duplicate. A post landing close in time to an
existing snapshot on the same Window Event is **folded into it** — its
members are unioned in and no second snapshot row is created:

| Condition | Detail |
|---|---|
| **Same Window Event** | Both posts attached to the same parent |
| **Target still Active** | A row an officer marked `Duplicate`/`Ignored` is never a target |
| **Within the merge window** | `\|target.CapturedAt − this.CapturedAt\| ≤ merge window` |

The merge window is scaled to the monster's own spawn window, so a fold
can never span two real windows (`HnmConfig.SnapshotMergeWindow`):

| Camp | Spawn window | Merge window |
|---|---|---|
| Tiamat / Jormungand / Vrtra | 60 min × 25 | **5 min** |
| Fafnir / Behemoth / Adamantoise (+HQ) | 10 min × 7 | **3 min** |
| Everything else (Sky, farm NMs, ad-hoc) | — | **3 min** |

There is no roster-similarity test. Two officers each scanning their own
alliance barely overlap, and that is exactly the case that most needs
combining. The target's `CapturedAt` does not move when a post folds in,
so a steady drip of posts eventually starts a fresh snapshot instead of
chaining into one that grows all camp long.

```
┌─────────────────────────────────────────────────────────────┐
│   1-MINUTE-LATER SCENARIO                                   │
├─────────────────────────────────────────────────────────────┤
│   Inside the merge window   →  folded in; one snapshot,     │
│                                union of both rosters        │
│                                                             │
│   Outside the merge window  →  fresh Active snapshot on the │
│                                same event (combined roster) │
└─────────────────────────────────────────────────────────────┘
```

> **Why this replaced duplicate detection.** DKP is credited per Window
> Event, not per snapshot, so double-posting never double-paid. But a
> flagged snapshot is *excluded* from the combined roster — so anyone who
> appeared only in the flagged post silently lost their credit. Folding
> keeps every name.

---

## ▌ PANEL 4 · WHEN TO OVERRIDE THE DEFAULTS

| You want… | How to do it |
|---|---|
| A **new event** for a same-name repop within 21h | Close the previous Window Event on the web UI first — closed events are no longer absorbed. |
| Two posts to stay **separate** rows | Leave more than the merge window between them (5 min on a wyrm, 3 min otherwise), or post the second under a different event name. |
| To mark a snapshot junk | Window Events page → set status to `Ignored`. |
| To track multi-day pops separately | Use a day suffix in the name: `/lsm now Fafnir D2`, `/lsm now Fafnir D3` — they normalize differently so they don't group. |

---

## ▌ WHY THE SPECIFIC NUMBERS

| Threshold | Reasoning |
|---|---|
| **21 hours** stale cutoff | Shorter than the shortest HNM repop (~22h). The same name posted a day later correctly becomes a new event instead of merging with yesterday's. |
| **±8 minutes** duplicate window | Wide enough to catch officers double-tapping the command or two officers each running a snapshot for the same kill. |
| **75% overlap** threshold | Sweet spot — common dedup cases (network blip re-post, simultaneous officer captures) all sit well above it; "different people zoning in mid-fight" sits below. |

---

```
══════════════════════════════════════════════════════════════════════
  Source pointers
  ──────────────────────────────────────────────────────────────────
  Commands ............ lsm/commands.lua
  Snapshot endpoint ... Controllers/AddonApiController.AttendanceSnapshots.cs
  Monster mapping ..... Models/WindowEvent.cs · WindowEventEntryTypes
  Sheet integration ... Services/AttInputAppendService.cs
══════════════════════════════════════════════════════════════════════
```
