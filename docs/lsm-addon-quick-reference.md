# LSM Addon — Quick Reference

A one-page card for the in-game `lsm` addon. For the full walkthrough with pictures, see the
[In-Game Addon Guide](LSManager-Addon-Guide.md). `/lsm help` in game prints the same list.

The addon is for **HNM and NM camps**. Events are made in Discord or the app; the addon picks
them up.

## First time

1. `/addon load lsm`
2. In the app: **Game Addon** tab → **Pair Code** → **+ Get Code**.
3. In game: `/lsm link <code>`. One code pairs every linkshell you are in.
4. `/attend` to open the launcher.

## Commands

| Command | What it does |
|---|---|
| `/attend` · `/attend close` | Open or close the launcher. |
| `/repop` · `/repop close` | Open or close the Repop Timer. |
| `/lsm link <code>` | Pair the addon. |
| `/lsm unlink [name\|all]` | Drop one pairing, or all of them. |
| `/lsm status` | Show the server, your pairings and which pearl slot each shell is in. |
| `/lsm now [name] [p\|a]` | Snap your alliance (`a`, the default) or party (`p`). It lands in **Unlinked Posts** for an officer to file. |
| `/lsm roster` | Check the linkshell roster against your alliance. |
| `/lsm jobs` | List the jobs the addon has learned this session. |
| `/lsm slots` · `/lsm slots swap` | Check what each pearl slot reads, or swap them if they are backwards. |
| `/lsm http [sync\|async]` | Show or set how background refreshes reach the server. `async` (the default) never pauses the game. |
| `/lsm server <url>` | Only if you self-host. The addon ships pointed at `https://linkshellmanager.com`. |
| `/lsm help` | Print the command list in chat. |

**Troubleshooting:** `/lsm claimshield` (status) · `/lsm claimshield debug` · `/lsm repop debug` ·
`/lsm todcapture` (log every defeat line) · `/lsm seaprobe` (Lobby **Scan** problems).

## Things to remember

- **Every alliance needs its own poster.** The game only shows the addon your own alliance.
  Nobody sets an alliance number; the addon reads the alliance leader.
- **Only a Post pays.** The Lobby is for looking.
- **End Event does not pay.** It hands the camp to the app, where an officer posts the DKP.

## Gone

`/lsm alliance` (automatic now), `/tod` (now `/repop`), `/lsm checkin` / `/lsm checkout`
(use the Discord camp board), `/lsm link <code> 1|2` (no slot to pick), Auto-Post, and making
events in game.
