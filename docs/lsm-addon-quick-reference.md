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

### B) Duplicate detection on the new snapshot

After saving the new snapshot to the parent event,
`FindLikelyDuplicateSnapshotAsync` flags it as a **PossibleDuplicate**
when all of:

| Condition | Detail |
|---|---|
| **Same Window Event** | Both snapshots attached to the same parent |
| **Within ±8 minutes** | `\|other.CapturedAt − this.CapturedAt\| ≤ 8 min` |
| **Other not already Ignored / Duplicate** | Skips already-filtered rows |
| **Roster overlap ≥ 75%** | `overlap / min(this.size, other.size) ≥ 0.75` |

Both snapshots stay in the DB; downstream sheet sync, DKP credit, and
the Window Events UI **exclude** `PossibleDuplicate` and `Duplicate`
rows from the credited roster. Officers can flip the status back to
`Active` (or to `Ignored`) on the Window Events page.

```
┌─────────────────────────────────────────────────────────────┐
│   1-MINUTE-LATER SCENARIO                                   │
├─────────────────────────────────────────────────────────────┤
│   Roster mostly the same  →  PossibleDuplicate (no double-  │
│                              credit; officer reviews)       │
│                                                             │
│   Roster very different   →  fresh Active snapshot on the   │
│                              same event (combined roster)   │
└─────────────────────────────────────────────────────────────┘
```

---

## ▌ PANEL 4 · WHEN TO OVERRIDE THE DEFAULTS

| You want… | How to do it |
|---|---|
| A **new event** for a same-name repop within 21h | Close the previous Window Event on the web UI first — closed events are no longer absorbed. |
| To reinstate a flagged duplicate | Window Events page → flip status from `PossibleDuplicate` back to `Active`. |
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
