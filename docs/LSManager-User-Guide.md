# LinkshellManager — User Guide

> Your linkshell's command center. Plan events. Track DKP. Capture ToDs. Run live attendance — straight from the game.

LinkshellManager (LSManager) is a three-piece system over one shared database. Each piece now has its own focused guide:

| Piece | Where it lives | What it's for | Guide |
| --- | --- | --- | --- |
| **Web App** | `https://your-host/` (any browser) | The same app as the Discord Activity, plus the super-admin tools (Admin, Data Admin) | **[Web App Guide](LSManager-WebApp-Guide.md)** |
| **Discord Activity** | Inside Discord — voice "Activities" tray | Quick member-facing experience while everyone's in voice | **[Discord Activity Guide](LSManager-DiscordActivity-Guide.md)** |
| **In-Game Addon** (`lsm`) | Ashita v3 / HorizonXI client | HNM camp attendance, ToD, Claim Shield and loot capture | **[In-Game Addon Guide](LSManager-Addon-Guide.md)** |

All three share the **same database** — anything done on one surface shows up on the others in seconds.

---

## Putting it all together — a typical HNM camp

1. **Officer** — in the app, *Event System → Create Event* for "Fafnir" (or let the HNM board post itself from the last ToD).
2. **Members** — sign up from the Discord board, the Activity or the web.
3. **Camp leader** — in game, `/attend` opens the launcher; pick the queued camp.
4. **During the camp** — **Start & Post** the first roster, then post the rest as the camp goes (the monster's *Window Method* decides which). Every alliance needs its own paired poster.
5. **Fafnir dies** — post the last rosters (**Close**, **Kill**). The addon catches the ToD, the Claim Shield lottery and the loot from chat; press **Post** on each.
6. **End Event** — hands the camp to the app. An officer reviews it and posts the DKP; balances update on every member's profile.
7. **Next pop** — `/repop` in game, or the Dashboard's ToD Tracker, counts down to the next window.

---

## Quick reference card

**Web** — `https://your-host/` → Sign in with Discord → tabs along the top, the same as in Discord. See the [Web App Guide](LSManager-WebApp-Guide.md).
**Discord** — voice channel → Activities → LinkshellManager → tabs along the top. See the [Discord Activity Guide](LSManager-DiscordActivity-Guide.md).
**Addon** — `/addon load lsm` → in the app, **Game Addon → Pair Code → + Get Code** → `/lsm link <code>` → `/attend`. One code pairs every linkshell you are in, and there is no server to set. See the [Addon Guide](LSManager-Addon-Guide.md).

**Most-used in-game commands:**
- `/attend` — open the launcher
- `/repop` — open the Repop Timer
- `/lsm now [name]` — snap your alliance now; an officer files it from Unlinked Posts
- `/lsm status` — show pairings
- `/lsm link <code>` — pair the addon
- `/lsm unlink [name|all]` — unpair
- `/lsm help` — print the command list in chat
