# LinkshellManager — User Guide

> Your linkshell's command center. Plan events. Track DKP. Capture ToDs. Run live attendance — straight from the game.

LinkshellManager (LSManager) is a three-piece system over one shared database. Each piece now has its own focused guide:

| Piece | Where it lives | What it's for | Guide |
| --- | --- | --- | --- |
| **Web App** | `https://your-host/` (any browser) | Full management — roster, DKP, items, revenue, auctions, events | **[Web App Guide](LSManager-WebApp-Guide.md)** |
| **Discord Activity** | Inside Discord — voice "Activities" tray | Quick member-facing experience while everyone's in voice | **[Discord Activity Guide](LSManager-DiscordActivity-Guide.md)** |
| **In-Game Addon** (`lsm`) | Ashita v3 / HorizonXI client | Live attendance, ToD capture, loot logging, event creation | **[In-Game Addon Guide](LSManager-Addon-Guide.md)** |

All three share the **same database** — anything done on one surface shows up on the others in seconds.

---

## Putting it all together — a typical Sky run

1. **Officer** — on the web or Discord Activity, *Events → Create Event* "Kirin", DKP 5/hr, 18 slots.
2. **Members** — sign up from Discord Activity or web.
3. **Raid leader** — in game, `/attend` to open the launcher and confirm the queued event is loaded.
4. **At pop** — leader runs `/lsm kirin ls h` — addon does `/sea limbus_zone linkshell`, builds the attendance, posts it to the live event, writes the local HNM log file.
5. **Kirin dies** — addon auto-captures the ToD and posts it to the linked linkshell.
6. **Loot rolls** — winners are recorded with DKP spent.
7. **Event ends** — leader closes it on web or Discord; the event moves to *Event History*; DKP balances update on every member's profile.
8. **Next pop window** — anyone glancing at the Dashboard sees the Kirin countdown ticking down to repop.

---

## Quick reference card

**Web** — `https://your-host/` → Login with Discord → use the sidebar. See the [Web App Guide](LSManager-WebApp-Guide.md).
**Discord** — voice channel → Activities → LinkshellManager → tabs along the top. See the [Discord Activity Guide](LSManager-DiscordActivity-Guide.md).
**Addon** — `/addon load att` → `/lsm server <url>` → generate code on web/Discord → `/lsm link <code> [1|2]` → `/attend`. See the [Addon Guide](LSManager-Addon-Guide.md).

**Most-used in-game commands:**
- `/attend` — open the launcher
- `/lsm <event>` — take attendance
- `/lsm here` — attendance for current zone
- `/lsm status` — show pairings
- `/lsm link <code> [1|2]` — pair to a linkshell
- `/lsm unlink [1|2|all]` — unpair
- `/lsm help` — open the in-game help window
