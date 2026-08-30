# LSM Addon Tutorial Brochure

> A practical officer walkthrough for the in-game `lsm` addon. Use this with
> the launcher screenshot: the main LSM Addon window is on the left, Settings
> is on the right, the standalone ToD Tracker is near the bottom, and chat
> confirmations appear in the game chat log.

---

## At A Glance

| Area in the screenshot | What it is for | Most common action |
|---|---|---|
| **Header bar** | Shows web-sync state, active linkshell, compact toggle, and refresh. | Confirm `[Web Sync Activated]`, pick the correct LS, click **Refresh** before an event. |
| **Create New Event** | Builds a new timed event from inside the addon. | Use when the event was not already created on the website or Discord Activity. |
| **Event Presets** | Fast-start lists for common event types such as Events, NMs, and HNMs. | Expand a category, choose a monster/event, then post windows as they happen. |
| **Queued Events** | Events already created but not started. | Select the event you are about to run. |
| **Attendance panel** | The live roster and posting controls. | Choose Party / Alliance and your alliance number, review attendees, then **Start & Post** or **Post Window**. |
| **Loot Pool** | Captured drops for the current kill. | Assign winners and post loot after drops appear in chat. |
| **Settings** | DKP defaults, panel visibility, opacity, ToD capture lists, Claim Shield messages. | Set defaults once, then save. |
| **ToD Tracker** | Countdown view for tracked monsters. | Watch repop windows and post a ToD when a tracked mob pops/dies. |
| **Chat log** | Source of truth for command results. | Read the `[lsm]` confirmation after every server, link, snapshot, ToD, or post action. |

---

## Panel 1 - First-Time Setup

Run this once per Ashita install, or again when the LSManager host changes.

```text
/addon load lsm
/lsm server https://your-lsmanager-host
```

The addon saves the server URL and probes it immediately. Any real HTTP
response means the route is reachable; a probe failure usually means a typo or
the site is down.

Next, generate a pairing code from the web app or Discord Activity:

```text
Configurations -> Customize Linkshell -> Addon Tokens -> Generate pairing code
```

Redeem the code in game:

```text
/lsm link ABC123        # defaults to LS1
/lsm link ABC123 2      # pair LS2 instead
/lsm status             # verify server and pairings
```

When pairing succeeds, chat prints `Linked to <Linkshell Name> on LS1`. The
launcher header should then show `[Web Sync Activated]`, and the LS dropdown
should contain the paired linkshell.

---

## Panel 2 - Open The Launcher

```text
/attend
```

The screenshot shows the full launcher layout. Treat it as your in-game control
desk:

| Step | What to check |
|---|---|
| **1. Header** | Confirm web sync is active and the selected linkshell is correct. |
| **2. Timezone** | Verify the displayed timezone is the one you expect officers to use. |
| **3. Scope** | Pick `Party` or `Alliance` for the roster you are about to post, and set your alliance number. |
| **4. Events** | Use Event Presets, create a new event, or select an existing queued event. |
| **5. Attendance** | Check the visible roster before posting. |
| **6. Loot / ToD tools** | Leave these open if you want automatic kill, ToD, and loot follow-through. |

Use **Compact** when you only need the live attendance, loot, and capture tools.
Use **Refresh** when another officer created or changed an event from the web
or Discord.

---

## Panel 3 - Start And Post A Timed Event

For standard scheduled events, the flow is:

```text
1. /attend
2. Select a queued event, or check Create New Event and create one.
3. Choose the attendance scope.
4. Review the roster.
5. Click Start & Post.
6. Keep the launcher open while attendance, breaks, and loot change.
7. End the event when finished.
```

The `Timed - DKP / Hour` value in Settings becomes the default rate for timed
events created from the addon. Officers can still adjust the event on the web
side if a special rate is needed.

For attendance scope:

| Scope | Use it when |
|---|---|
| **Party** | Only your six-person party should count. |
| **Alliance** | The whole alliance should count. This is the default, and what you want almost every time. |

There is no Zone scope. The FFXI client cannot show an addon who is in the zone outside your own
alliance, so a second alliance at the same camp is invisible to you — **it needs its own poster.**
Set which one you are with `/lsm alliance <1-6>` (or the dropdown beside the scope radios) so the
web keeps the two rosters apart.

---

## Panel 4 - Run HNM / NM Windows

The Event Presets list is built for repeatable window posting. In the screenshot,
`Shikigami Weapon` is selected and the attendance panel has a `Window 1` block.

```text
1. Expand Event Presets.
2. Pick the NM/HNM.
3. Confirm the window number and DKP per window.
4. Choose Party or Alliance, and set your alliance number.
5. Click Start & Post: Window 1.
6. For later windows, click Post Window.
7. Close or end the event when the camp is done.
```

HNM-style events use the `HNM - DKP / Window` default from Settings. Each posted
window records its own attendance batch, and the web app rolls those batches
into the event history and sheet sync.

---

## Panel 5 - Fast Snapshot Command

Use `/lsm now` when you need an immediate roster snapshot without running the
full launcher workflow.

```text
/lsm now
/lsm now Fafnir
/lsm now Fafnir z
/lsm now "Fafnir D2" p
```

Every snapshot writes a local CSV and also pushes the same payload to LSManager
when paired. The final token may be a scope:

| Token | Captures |
|---|---|
| `a` or omitted | Your full alliance (up to 18) |
| `p` | Your party (up to 6) |

Named snapshots create or update a Window Event on the web. The server also
auto-tags common camps:

| Monster name | Entry Type |
|---|---|
| `Tiamat`, `Jormungand`, `Vrtra` | Wyrms Camp |
| `Cerberus`, `Hydra`, `Khimaira` | Misc Camp (ToAU — neither wyrm nor king) |
| `Adamantoise`, `Aspidochelone`, `Behemoth`, `Fafnir`, `King Behemoth`, `Nidhogg` | Kings Camp |
| Anything else | Misc Camp |

Repeat snapshots with the same normalized name attach to the same open Window
Event while it is fresh. Posts landing close together are folded into a single
snapshot holding the union of their rosters — within 5 minutes on the 60-minute
band (Tiamat/Jormungand/Vrtra plus Cerberus/Hydra/Khimaira) and 3 minutes on
everything else. Nothing is flagged
as a duplicate: DKP is credited per Window Event rather than per snapshot, so
several officers scanning the same camp produce one roster, not double credit.

---

## Panel 6 - ToD Tracker And ToD Capture

The standalone ToD Tracker in the screenshot is opened with:

```text
/tod
/tod close
```

It shows tracked monsters, day suffixes such as `D1`, and countdown states such
as `Window expired`, `Window 1/3`, `POPPED`, or `Posted`.

The Settings window controls the capture list:

| Setting area | What it changes |
|---|---|
| **Display: ToD Tracker** | Enables or disables the standalone tracker window. |
| **ToD Capture monsters** | Lists built-in HNMs, Sky NMs, Sea NMs, other NMs, HENMs, and custom names. |
| **Add monster** | Adds a custom defeat-line matcher that persists across reloads. |

When a matching defeat line appears in chat, the addon can post the ToD to the
paired linkshell. The ToD then appears in the web and Discord tracker with the
next repop window.

For diagnostics:

```text
/lsm todtracker debug
```

---

## Panel 7 - Loot Pool

The Loot Pool panel fills from in-game drop messages. After a kill:

```text
1. Leave the launcher open.
2. Wait for drops to appear in chat.
3. Review the detected loot rows.
4. Choose winners.
5. Post each loot result.
```

Loot posts are tied to the current event or ToD context where possible. This
keeps event history, member ledgers, and ToD loot history aligned without
manual re-entry on the website.

---

## Panel 8 - Claim Shield

Claim Shield watches for configured spawn-announcement lines, then tracks LS
members through the lottery result. The Settings window shows built-in and
custom spawn messages.

Use these commands when testing capture behavior:

```text
/lsm claimshield
/lsm claimshield debug
```

The debug toggle prints more attribution detail in chat, which is useful when a
spawn message or claim result was missed.

---

## Panel 9 - Settings Checklist

Open Settings from the launcher and set these before the first real event:

| Setting | Recommended first pass |
|---|---|
| **Timed - DKP / Hour** | Your normal hourly event rate. |
| **HNM - DKP / Window** | Your normal per-window camp rate. |
| **Display panels** | Leave Loot Pool, ToD Capturing, and Claim Shield enabled on Main; disable anything noisy in Compact. |
| **Window Opacity** | Keep `1.00` unless the addon blocks important game UI. |
| **ToD Capture monsters** | Add custom NMs your linkshell tracks. |
| **Claim Shield Spawn Messages** | Add custom lottery spawn lines if your server uses non-standard text. |

Click **Save** after changing persistent settings. Opacity sliders apply live,
but panel visibility, DKP defaults, and custom capture lists should be saved.

---

## Officer Walkthrough - First Real Camp

```text
Before pull:
  /lsm status
  /attend
  Refresh
  Select the event or preset
  Confirm scope = Alliance
  Confirm your alliance number (/lsm alliance <n>)

At the first attendance point:
  Review visible roster
  Click Start & Post
  Confirm the [lsm] chat success line

During camp:
  Post later windows from the same launcher event
  Use Loot Pool as drops appear
  Watch /tod or the ToD panel for tracked mobs

After camp:
  End or close the event
  Review Window Events / Event History on the web
  Check the combined roster, and Ignore any junk snapshot
```

---

## Command Card

| Command | Purpose |
|---|---|
| `/addon load lsm` | Load the addon. |
| `/lsm server <url>` | Set the LSManager server URL. |
| `/lsm link <code> [1\|2]` | Pair the addon to LS1 or LS2. |
| `/lsm unlink [1\|2\|all]` | Remove a pairing. |
| `/lsm status` | Show server and pairings; auto-drops revoked tokens. |
| `/attend` | Toggle the main launcher. |
| `/attend close` | Close the launcher. |
| `/tod` | Toggle the standalone ToD Tracker. |
| `/tod close` | Close the ToD Tracker. |
| `/lsm todtracker` | Alias for `/tod`. |
| `/lsm todtracker debug` | Toggle verbose ToD attribution logging. |
| `/lsm now [name] [p\|z\|a]` | Capture an immediate CSV plus web snapshot. |
| `/lsm claimshield` | Print current Claim Shield capture status. |
| `/lsm claimshield debug` | Toggle verbose Claim Shield logging. |
| `/lsm help` | Print the in-game command help. |

---

## Troubleshooting

| Symptom | What to do |
|---|---|
| Launcher says web sync is not active | Run `/lsm status`; if unpaired, generate a new pairing code and run `/lsm link <code>`. |
| Events are missing from Queued Events | Click **Refresh**; confirm the correct LS is selected in the header. |
| Snapshot synced but local CSV failed | The web copy is still saved. Check folder permissions under `Ashita\addons\lsm\Snapshots`. |
| Local CSV saved but web sync failed | Check `/lsm status`, server URL, and network connectivity, then capture again if needed. |
| ToD Tracker will not open | Enable **Display: ToD Tracker** in Settings, save, then run `/tod`. |
| Two officers posted the same kill | Nothing to do — posts within the merge window fold into one snapshot holding both rosters, and DKP is credited per Window Event, so nobody is paid twice. |
| Same monster should be a new event | Close the previous Window Event first, or add a suffix such as `Fafnir D2`. |

---

## Source Pointers

| Topic | File |
|---|---|
| Commands | `lsm/commands.lua` |
| Launcher UI | `lsm/ui/launcher.lua` |
| Settings UI | `lsm/ui/settings.lua` |
| ToD Tracker window | `lsm/ui/tod_tracker_window.lua` |
| Snapshot endpoint | `Controllers/AddonApiController.AttendanceSnapshots.cs` |
| Monster entry-type mapping | `Models/WindowEvent.cs` |
| Sheet integration | `Services/AttInputAppendService.cs` |

For a shorter daily-use card, see `docs/lsm-addon-quick-reference.md`.
