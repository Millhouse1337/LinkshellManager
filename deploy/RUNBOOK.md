# LSManager Droplet Deployment Runbook

End-to-end steps to move from "running locally behind a Cloudflare Tunnel" to "running 24/7 on a DigitalOcean droplet behind Cloudflare proxy."

## Architecture

```
Discord client
   v
Cloudflare (DNS + proxy + SSL termination, public)
   v
Droplet :443  -> nginx (re-terminates SSL with Cloudflare Origin Cert)
                -> Kestrel 127.0.0.1:5000 (LSManager .NET app)
                     v
                   DigitalOcean Managed Database (Postgres 18, TLS, port 25060)
```

Nothing on the droplet is exposed except SSH (22), HTTP (80, redirect), and HTTPS (443). The
database is not on the droplet, so losing the droplet does not lose any data (see
**Rebuild the droplet from scratch**).

## Prerequisites

- A droplet running Ubuntu 22.04 or 24.04, with a public IPv4.
- The DigitalOcean Managed Database (its connection details are in the DO console).
- A domain in Cloudflare DNS.
- The values in **Settings reference** below (Discord app, bot token, your Discord user id).
- Local: .NET 10 SDK, Node 20+, OpenSSH (Windows 10+ has it).

## Settings reference

Everything the app reads from `/etc/lsmanager/env`. Keep every secret in your password manager,
never in git: this file is the only copy on the droplet, and rebuilding the droplet means typing
them in again.

| Key | Needed? | What it is | Where the value comes from |
|---|---|---|---|
| `ConnectionStrings__DefaultConnection` | Yes, secret | The managed database (`Host=…;Port=25060;Database=…;Username=…;Password=…;SSL Mode=Require`) | DO console → Databases → Connection details |
| `Discord__ClientId` | Yes | The Discord application id (also in `appsettings.json`) | Discord Developer Portal → General Information |
| `Discord__ClientSecret` | Yes, secret | Discord sign-in | Developer Portal → OAuth2 |
| `Discord__BotToken` | Yes, secret | Posts boards and reads guild members. Without it nothing is posted to Discord | Developer Portal → Bot |
| `Discord__PublicKey` | Yes | Checks Discord's button clicks on boards. Without it board buttons fail | Developer Portal → General Information |
| `SuperAdmin__DiscordUserId` | Yes | Your own Discord user id; the only way to become super admin | Discord → Developer Mode → right-click your name → Copy User ID |
| `ASPNETCORE_ENVIRONMENT`, `ASPNETCORE_URLS` | Yes | `Production` and `http://127.0.0.1:5000` | `setup-droplet.sh` writes them |
| `DataProtection__KeyRingPath` | Yes | `/var/lib/lsmanager/keys`, keeps sign-ins across deploys | `setup-droplet.sh` writes it |
| `Uploads__Root` | Yes | `/var/lib/lsmanager/uploads`, ToD screenshots | `setup-droplet.sh` writes it |
| `ForwardedHeaders__KnownNetworks__N`, `ForwardedHeaders__KnownProxies__0` | Yes | Cloudflare's IP ranges and nginx, so the app sees real visitor IPs | `setup-droplet.sh` writes them; refresh from cloudflare.com/ips now and then |
| `App__PublicBaseUrl` | Recommended | The site's public address. Auction and loot posts in Discord only carry an app link when it is set | Your domain, e.g. `https://linkshellmanager.com` |
| `Ai__ApiKey` (or `ANTHROPIC_API_KEY`), `Ai__Model` | Optional, secret | Short summaries of job-rating comments. Off when unset | Anthropic console |
| `RateLimiting__ApiPermitPerMinute` | Optional | Raise only if real users see "too many requests" | — |
| `Launcher__DownloadUrl` | Optional | Overrides where **Download launcher** points (default in `appsettings.json`: the latest lsb-launcher GitHub release) | Your GitHub release |

## Rebuild the droplet from scratch

Use this if the droplet is lost or you move to a new one. The database is a separate managed
service, so all data survives; only the droplet's own files need rebuilding.

What is lost with the droplet, and what that means:
- `/etc/lsmanager/env` — type it in again from your password manager (**Settings reference**).
- `/var/lib/lsmanager/keys` — everyone has to sign in again once. Nothing else.
- `/var/lib/lsmanager/uploads` — ToD screenshots. Copy the folder off the old droplet first if you can.
- `/var/backups/lsmanager` — the pre-deploy dumps. The managed database keeps its own daily backups.
- The Cloudflare origin certificate files — create a new one (Step 3).

Steps:
1. Create an Ubuntu 24.04 droplet. If the managed database has **Trusted sources** turned on, add
   the new droplet there in the DO console, or the app cannot connect.
2. Run **Step 1** to **Step 5** below. Step 1 installs the .NET 10 runtime (the only one needed),
   nginx, ufw and the Postgres 18 client. It does not create a database.
3. If you saved the old screenshots, copy them into `/var/lib/lsmanager/uploads/tods` and
   `sudo chown -R lsmanager:lsmanager /var/lib/lsmanager/uploads`.
4. Deploy (**Step 6**). The database already has its schema, so the backup step works and
   `-SkipBackup` is not needed; the app only applies migrations the database does not have yet.
5. Point the Cloudflare `A` record at the new droplet's IP (**Step 7**), then check `/healthz`
   and sign in (**Step 8**).
6. Destroy the old droplet once the new one is working.

## Step 1 - Bootstrap the droplet

SSH in as `root` (or with sudo) and run:

```bash
# from your dev machine
scp deploy/setup-droplet.sh root@<DROPLET_IP>:/tmp/
ssh root@<DROPLET_IP> 'bash /tmp/setup-droplet.sh'
```

The script:
- installs the .NET 10 ASP.NET Core runtime (from Ubuntu's own package archive), nginx, ufw
- installs the Postgres 18 client (`pg_dump`, `psql`) for the pre-deploy backups
- creates a `lsmanager` system user and its folders under `/var/lib/lsmanager`
- enables ufw with 22/80/443 only
- writes `/etc/lsmanager/env` with `REPLACE_ME` placeholders for the secrets

It does not create a database: the app uses the DigitalOcean Managed Database.

## Step 2 - Fill in the env file

```bash
ssh root@<DROPLET_IP>
sudo nano /etc/lsmanager/env
```

Replace every `REPLACE_ME` (see **Settings reference** for where each value comes from):
- `ConnectionStrings__DefaultConnection` (the managed database)
- `Discord__ClientId`, `Discord__ClientSecret`, `Discord__BotToken`, `Discord__PublicKey`
- `SuperAdmin__DiscordUserId` (your own Discord user id: Discord Settings > Advanced >
  Developer Mode on, then right-click your name > Copy User ID)

Add `App__PublicBaseUrl=https://<your domain>` too. Save.

`SuperAdmin__DiscordUserId` is the only way an account becomes super admin. On every start
the app finds the account signed in with that Discord id and sets `IsSuperAdmin` on it. If it
is missing, the log says `SuperAdmin seeding: SuperAdmin:DiscordUserId is not set` and nobody
is promoted. See **Super admin by Discord id (November 2026)** for droplets set up earlier.

## Step 3 - Cloudflare Origin Certificate

In the Cloudflare dashboard for your domain:
1. **SSL/TLS** -> **Origin Server** -> **Create Certificate**.
2. Use defaults (RSA 2048, 15 years, hostnames `*.yourdomain.com, yourdomain.com`).
3. Copy the **certificate** and **private key** that appear.

On the droplet:
```bash
sudo mkdir -p /etc/ssl/cloudflare
sudo nano /etc/ssl/cloudflare/origin.pem   # paste certificate
sudo nano /etc/ssl/cloudflare/origin.key   # paste private key
sudo chmod 600 /etc/ssl/cloudflare/origin.key
sudo chmod 644 /etc/ssl/cloudflare/origin.pem
```

In Cloudflare **SSL/TLS** -> **Overview**, set encryption mode to **Full (strict)**.

## Step 4 - nginx site

```bash
# from your dev machine
scp deploy/nginx-lsmanager.conf root@<DROPLET_IP>:/etc/nginx/sites-available/lsmanager.conf

# on the droplet
ssh root@<DROPLET_IP>
sudo ln -sf /etc/nginx/sites-available/lsmanager.conf /etc/nginx/sites-enabled/lsmanager.conf
sudo rm -f /etc/nginx/sites-enabled/default
sudo nginx -t
sudo systemctl reload nginx
```

`nginx -t` should report `syntax is ok` and `test is successful`. If it complains about cert files, redo step 3.

## Step 5 - systemd service

```bash
# from your dev machine
scp deploy/lsmanager.service root@<DROPLET_IP>:/etc/systemd/system/lsmanager.service

# on the droplet
ssh root@<DROPLET_IP>
sudo systemctl daemon-reload
sudo systemctl enable lsmanager
# Don't start yet - there's nothing in /var/www/lsmanager. The deploy script handles that.
```

## Step 6 - First deploy

From your dev machine, in the repo root:

```powershell
.\deploy\deploy.ps1 -DropletHost root@<DROPLET_IP>
```

This checks the git tree is clean, runs the tests, builds the Angular activity, publishes the .NET app, uploads it, dumps the database (see **Database backups**), swaps the new build in, starts the service and waits for `/readyz`. On start the app applies any EF migrations the database does not have yet (on an empty database, that creates the whole schema).

Only with a brand-new, **empty** database is there nothing to back up (the backup check would
fail on the tiny dump), so pass `-SkipBackup` for that first deploy only. Rebuilding the droplet
against the existing database needs no `-SkipBackup`.

When the script finishes, tail the logs to verify:

```bash
ssh root@<DROPLET_IP> 'sudo journalctl -u lsmanager -f'
```

You should see Kestrel say it's listening on `http://127.0.0.1:5000`. Hit it with curl from the droplet to confirm:

```bash
curl -i http://127.0.0.1:5000/
```

A 200 or a redirect means the app is up. Then hit it through nginx:

```bash
curl -ik https://127.0.0.1/ -H "Host: yourdomain.com"
```

## Step 7 - Cloudflare DNS cutover

In Cloudflare DNS:
- Find the existing `A` record that currently points at your tunnel.
- Edit it to point at the **droplet's public IPv4**.
- Make sure the cloud is **orange** (proxied).
- Save.

Propagation is near-instant for proxied records.

Test from a browser: open the URL Discord uses for your activity and confirm the app loads.

## Step 8 - Verify Discord activity

Inside the Discord client, launch the activity. Walk the golden path:
- Login flow completes.
- The Angular activity loads in the iframe.
- API calls from the activity succeed (open devtools network tab - look for any CSP / CORS errors).
- WebSocket / SignalR (if used) connects.

Nothing on the Discord side needs to change because the public hostname is unchanged. URL Mappings, OAuth redirects, and CSP allowlists all still match.

## Step 9 - Tear down the old setup

After 24-48h of stable running:
- Stop and disable `cloudflared` on your local machine (or uninstall it).
- In the Cloudflare Zero Trust dashboard, delete the tunnel.
- If you had any other DO services for this app (the old managed DB, etc.), destroy them.

## Day 2 operations

| Task | Command |
|---|---|
| Redeploy after code change | `.\deploy\deploy.ps1 -DropletHost root@<IP>` |
| Which commit is live | `ssh root@<IP> 'curl -s http://127.0.0.1:5000/healthz'` → `{"status":"Healthy","version":"3f2a9c1"}` |
| Tail logs | `ssh root@<IP> 'sudo journalctl -u lsmanager -f'` |
| Service status | `ssh root@<IP> 'sudo systemctl status lsmanager'` |
| Restart service | `ssh root@<IP> 'sudo systemctl restart lsmanager'` |
| Postgres shell | `ssh root@<IP> '/tmp/dbshell.sh'` — see **Database access** below |
| Backup DB | `ssh root@<IP> 'sudo /usr/local/bin/lsm-backup.sh'` — see **Database backups** below |
| Roll back a bad deploy | see **Rollback** below |

### deploy.ps1 safety checks

Each one stops the deploy before the running app is touched (except the last). If the
backup fails, the new build is left unpacked in `/var/www/lsmanager.new`; that is harmless
and the next deploy clears it.

| Check | Skip with | Why it exists |
|---|---|---|
| Git tree has no uncommitted changes | `-AllowDirty` (version then ends in `-dirty`) | So what is live is always a real commit |
| `dotnet test` passes | `-SkipTests` | A broken test or a forgotten migration (`MigrationsUpToDateTests`) stops here, not in production |
| Database dump succeeds and looks real | `-SkipBackup` | Startup migrations can delete data; the dump is the quick way back |
| `/readyz` answers within ~90 s after start | — | `systemctl is-active` says "active" even while the app crash-loops. On failure the script prints the last 50 log lines and exits 1; the new build stays in place, so roll back by hand |

The script writes the commit (`git rev-parse --short HEAD`) into `version.txt` next to the app, and
`/healthz` shows it, so the end of each deploy prints which commit is running.

## Database access — READ THIS BEFORE BACKING UP

**The application does NOT use a Postgres on the droplet.** It connects to a
DigitalOcean **Managed Database** over TLS (port 25060). The host, database, user and
password are in `ConnectionStrings__DefaultConnection` in `/etc/lsmanager/env` and in
the DO console.

Droplets set up before November 2026 also have a local `lsmanager` database with
**zero tables** — an unused leftover (`setup-droplet.sh` no longer creates it). The
command this table used to recommend (`sudo -u postgres pg_dump lsmanager`) therefore
dumped an EMPTY database and produced a ~700-byte file that looks like a valid dump. A
real dump is ~13 MB across ~60 tables. Do not trust a backup you have not size-checked.

Two more traps:

- **Client version.** The managed server is Postgres 18.x; Ubuntu 24.04 ships
  client 16, and `pg_dump` refuses to dump a newer server. `setup-droplet.sh` installs
  client 18; on an older droplet install it once:
  `apt-get install -y postgresql-common && /usr/share/postgresql-common/pgdg/apt.postgresql.org.sh -y && apt-get install -y postgresql-client-18`
- **TLS.** The managed database requires it — export `PGSSLMODE=require`.

Credentials live in `/etc/lsmanager/env` as `ConnectionStrings__DefaultConnection`.
Read them from there rather than pasting the password into a shell command, so it
does not land in your shell history:

```bash
#!/bin/bash
# /tmp/dbshell.sh — interactive psql on the REAL production database.
conn=$(grep -m1 '^ConnectionStrings__DefaultConnection=' /etc/lsmanager/env | cut -d= -f2-)
get() { echo "$conn" | tr ';' '\n' | grep -i "^ *$1=" | head -1 | cut -d= -f2- | xargs; }
export PGPASSWORD="$(get Password)"; export PGSSLMODE=require
psql -h "$(get Host)" -p "$(get Port)" -U "$(get Username)" -d "$(get Database)"
unset PGPASSWORD
```

For a dump, use `/usr/local/bin/lsm-backup.sh` (next section) instead of a hand-made
`pg_dump`: it checks the result for you.

Note DigitalOcean managed databases also take their own automated daily backups
with point-in-time recovery, so the console is the faster path for a real
restore. The dump below is for pre-deploy safety and for moving data around.

## Database backups

`deploy.ps1` dumps the production database **before every deploy**, because the new
build migrates the database on startup and some migrations delete rows. It uses
`/usr/local/bin/lsm-backup.sh` on the droplet. The source is `deploy/lsm-backup.sh` in
this repo; the deploy copies it to the droplet each time, so edit the repo copy, never
the droplet one.

What it does:

- Reads the connection string from `/etc/lsmanager/env` (the password never goes on a
  command line) and runs `pg_dump --no-owner --no-acl` against the managed database.
- Writes `/var/backups/lsmanager/lsmanager-prod-YYYYMMDD-HHMMSS.sql` (UTC time, root only).
- **Fails the deploy** if the dump is under 1 MB or has fewer than 40 `CREATE TABLE`
  lines — the "empty database looks like a valid dump" trap above. (The real database
  has ~60 tables since Phase 4B dropped seven and Phase 7 three; the bar is low so dropping unused tables does not
  trip it.) The
  bad file is kept as `...sql.suspect` for a look.
- Keeps the newest 7 good dumps and deletes older ones (only after a good dump).

Run it by hand any time: `ssh root@<IP> 'sudo /usr/local/bin/lsm-backup.sh'`.
Dumps made with the older `/tmp/dbbackup.sh` recipe sit in `/root/lsmanager-prod-*.sql`
and can be deleted once you no longer need them.

### Restoring a dump

Restore into a **new** database and point the app at it. That leaves the damaged
database untouched in case you need to look at it, and needs no `DROP`. (This recipe
has not been rehearsed yet — try it once on a quiet day before you need it.)

```bash
ssh root@<IP>
sudo systemctl stop lsmanager

# Same connection helper as the psql block above.
conn=$(grep -m1 '^ConnectionStrings__DefaultConnection=' /etc/lsmanager/env | cut -d= -f2-)
get() { echo "$conn" | tr ';' '\n' | grep -i "^ *$1=" | head -1 | cut -d= -f2- | xargs; }
export PGPASSWORD="$(get Password)" PGSSLMODE=require PGHOST="$(get Host)" PGPORT="$(get Port)" PGUSER="$(get Username)"

ls -lt /var/backups/lsmanager/                      # pick the dump to restore
DUMP=/var/backups/lsmanager/lsmanager-prod-YYYYMMDD-HHMMSS.sql
NEWDB=lsm_restore_$(date -u +%Y%m%d_%H%M)

psql -d "$(get Database)" -c "CREATE DATABASE $NEWDB"
psql -d "$NEWDB" -v ON_ERROR_STOP=1 -f "$DUMP"      # stops at the first error
psql -d "$NEWDB" -c 'SELECT count(*) FROM "AspNetUsers"'   # sanity check
unset PGPASSWORD
echo "$NEWDB"                                        # note the name
```

Then:

1. Point the app at the new database: edit `/etc/lsmanager/env` and change
   `Database=<old name>` to `Database=<NEWDB>` in `ConnectionStrings__DefaultConnection`
   (`sudo nano /etc/lsmanager/env`).
2. The dump was taken **before** the bad build's migrations ran. If you start the bad
   build again it will migrate the restored copy the same way, so first put the old
   build back (**Rollback** below, steps 2–3).
3. `sudo systemctl start lsmanager` and check `curl -s http://127.0.0.1:5000/readyz`.

Anything written between the dump and the restore (DKP posts, ToDs, sign-ups) is lost.
Drop the old database later, once you are sure you do not need it.

## Rollback

Each deploy keeps the previous build in `/var/www/lsmanager.old` (only one level back).

**When it is safe to just swap the folders back:** when the new build added **no
migrations**, or only migrations that add things (new tables, new nullable columns). The
old build simply ignores those. Check with `git log --stat <old commit>..<new commit> -- Migrations/`
(the commits are what `/healthz` printed before and after the deploy).

**When it is not:** if a new migration dropped or renamed a column/table, or changed data,
the old build may fail on startup or misread data. Then either undo that migration by hand
(like the SQL under **Board images removed** below) or restore the pre-deploy dump
(**Restoring a dump** above) together with the swap.

```bash
ssh root@<IP>
sudo systemctl stop lsmanager                                         # 1
sudo mv /var/www/lsmanager /var/www/lsmanager.bad                     # 2
sudo mv /var/www/lsmanager.old /var/www/lsmanager                     # 3
sudo systemctl start lsmanager                                        # 4
curl -s http://127.0.0.1:5000/readyz; echo; curl -s http://127.0.0.1:5000/healthz; echo
sudo rm -rf /var/www/lsmanager.bad                                    # once you are happy
```

The next normal deploy overwrites `lsmanager.old` again, so roll back before deploying anything else.

A rollback starts the old build with the .NET runtime it was built for. Since November 2026 both
the live build and `lsmanager.old` are .NET 10, the only runtime on the droplet (see
**.NET 10 runtime (November 2026)**).

## .NET 10 runtime (November 2026)

The app needs the **ASP.NET Core 10 runtime** on the droplet. `deploy.ps1` checks for it first
and stops, changing nothing, if it is missing. `setup-droplet.sh` installs it on a new droplet;
by hand it is:

```bash
ssh root@<IP>
sudo apt-get update
sudo apt-get install -y aspnetcore-runtime-10.0
dotnet --list-runtimes     # expect Microsoft.AspNetCore.App 10.0.x
```

Where it comes from: Ubuntu 24.04's own package archive (`/usr/lib/dotnet`). Microsoft's install
guide says this is the way on 24.04: Microsoft's own package feed no longer carries .NET for
Ubuntu 24.04, and the `ppa:dotnet/backports` PPA is only needed on 22.04. Fixes for the runtime
arrive with ordinary `apt upgrade` runs. If `apt-cache policy aspnetcore-runtime-10.0` ever shows
`packages.microsoft.com`, the two feeds are mixed; search "package mix ups on Linux" in
Microsoft's docs before going further.

**The .NET 8 runtime was removed from the droplet on 2026-11-13**, after the second .NET 10
deploy made `lsmanager.old` a .NET 10 build too. Nothing needs it now. Only a rollback to a build
from before the .NET 10 move (commit "Move every project to .NET 10 and EF Core 10") would need
it back: `sudo apt-get install -y aspnetcore-runtime-8.0`.

The .NET 10 move added two migrations. `EfCore10ModelSnapshot` changes nothing in the database
(it only refreshes EF's own record of the model). `DropIdentityRoleTables` dropped the unused
`AspNetRoles`, `AspNetUserRoles` and `AspNetRoleClaims` (permissions are `LinkshellRoles`); older
builds never read those tables, so they still start without them.

## One-time migration: persistent Data Protection keys

The app signs auth cookies and antiforgery tokens with the ASP.NET Core Data
Protection key ring. That key ring must live **outside** `/var/www/lsmanager`,
because `deploy.ps1` replaces that directory on every release. Fresh bootstraps
(`setup-droplet.sh`) now provision this automatically; **existing droplets created
before this change need a one-time fixup** or every deploy logs everyone out:

```bash
ssh root@<IP>
# 1. Create the persistent key ring dir owned by the service user.
sudo mkdir -p /var/lib/lsmanager/keys
sudo chown -R lsmanager:lsmanager /var/lib/lsmanager
sudo chmod 700 /var/lib/lsmanager/keys

# 2. Point the app at it (the deploy already ships an updated unit file, but the
#    env file is left untouched by setup-droplet.sh, so add this line yourself).
echo 'DataProtection__KeyRingPath=/var/lib/lsmanager/keys' | sudo tee -a /etc/lsmanager/env

# 3. Reload the unit (new ReadWritePaths) and restart.
sudo systemctl daemon-reload
sudo systemctl restart lsmanager
```

After this, the key ring persists across all future deploys, so people stay signed
in. (Google Sheets was removed, so there is no Google connection to keep any more.)

## Board images removed (October 2026)

Discord boards (event sign-up, auction, DKP sheet) used to be rendered as PNGs with
headless Chromium (Playwright). They are text embeds now, so the app no longer needs
Chromium, its OS libraries or the browser cache.

**Optional cleanup on droplets set up before this change** (nothing breaks if you skip it):

```bash
ssh root@<IP>
sudo rm -rf /var/lib/lsmanager/ms-playwright                     # ~110 MB browser cache
sudo sed -i '/^PLAYWRIGHT_BROWSERS_PATH=/d' /etc/lsmanager/env
sudo systemctl restart lsmanager
```

**Rolling back past this release** needs the database changed first. Its migration
(`RemoveEventBoardImageSettings`) drops `Linkshells.EventBoardTheme` and
`Linkshells.UseComponentsV2Boards`, and the older app reads both, so it fails on
startup without them. Before swapping the old app back in, open `psql` (the
`/tmp/dbshell.sh` block under **Database access** above) and run:

```sql
ALTER TABLE "Linkshells" ADD COLUMN "EventBoardTheme" character varying(16) NOT NULL DEFAULT 'Crystal';
ALTER TABLE "Linkshells" ADD COLUMN "UseComponentsV2Boards" boolean NOT NULL DEFAULT false;
DELETE FROM "__EFMigrationsHistory" WHERE "MigrationId" = '20261015084328_RemoveEventBoardImageSettings';
```

## Super admin by Discord id (November 2026)

The super admin used to be matched by email or main character name from `appsettings.json`.
Anyone could rename their main character to match, so that is gone: the app now reads
`SuperAdmin__DiscordUserId` from `/etc/lsmanager/env` only. **Add it before deploying this
release**, or you lose the super-admin tabs until you do:

```bash
ssh root@<IP>
echo 'SuperAdmin__DiscordUserId=<your Discord user id>' | sudo tee -a /etc/lsmanager/env
```

The new code never removes the flag from anyone. Check who already has it (read-only, via
the `/tmp/dbshell.sh` block under **Database access**):

```sql
SELECT "Id", "CharacterName", "UserName" FROM "AspNetUsers" WHERE "IsSuperAdmin";
```

If any row is not you, clear it:

```sql
UPDATE "AspNetUsers" SET "IsSuperAdmin" = false WHERE "Id" = '<their Id>';
```

## ToD screenshots outside the deploy folder (November 2026)

ToD screenshots used to be saved in `/var/www/lsmanager/wwwroot/uploads/tods`. Every deploy
replaces that folder, so each release lost the earlier screenshots. They now live in
`Uploads__Root` (`/var/lib/lsmanager/uploads`, already writable in `lsmanager.service`).
**Do this before deploying this release**, so the files still in the current build move over:

```bash
ssh root@<IP>
# 1. The new folder, owned by the service user.
sudo mkdir -p /var/lib/lsmanager/uploads/tods

# 2. Copy every screenshot that still exists. -n never overwrites; the .old folder may hold
#    a few from the release before (it is fine if one of these paths does not exist).
sudo cp -n /var/www/lsmanager/wwwroot/uploads/tods/* /var/lib/lsmanager/uploads/tods/ 2>/dev/null
sudo cp -n /var/www/lsmanager.old/wwwroot/uploads/tods/* /var/lib/lsmanager/uploads/tods/ 2>/dev/null
sudo chown -R lsmanager:lsmanager /var/lib/lsmanager/uploads
ls /var/lib/lsmanager/uploads/tods | wc -l      # how many came across

# 3. Point the app at it.
echo 'Uploads__Root=/var/lib/lsmanager/uploads' | sudo tee -a /etc/lsmanager/env
```

Then deploy as usual. If you forget step 3, the app keeps using `wwwroot/uploads` and the next
deploy throws new screenshots away again, exactly as before.

Uploading a screenshot now also needs a linkshell role that can log a ToD, submit one for
approval, or manage events (the Leader always can), and each account may upload 30 a day.

## Discord work queues live in memory (accepted risk)

Discord posts are not sent inside the save that causes them. The save drops an item on an
in-memory queue (`Services/*Queue.cs`: event boards, event-ended summaries, board deletions,
auction cards, loot posts, the ToD board and the DKP sheet), and a background worker sends it a
moment later. Nothing writes these items to the database, and a failed send is logged, not
retried.

So a restart or deploy while items are queued, or a Discord outage, can lose them. The database
is always right; only the Discord copy can be stale. What that looks like, and the fix:

| Lost item | What you see | Fix |
|---|---|---|
| Event board, auction card, ToD board, DKP sheet | Old or missing message | The next change to it re-posts it (edit the event, log a ToD, change some DKP) |
| Event-ended summary or loot post | No summary/loot message for that event | Post it by hand if anyone needs it |
| Board deletion | An ended event's board stays in the channel | Delete the message in Discord |

This is accepted on purpose: deploys are short and rare, and every lost item is cosmetic. If it
starts to matter, the first step is a small `DiscordPendingDeletes` table for board deletions (the
one case nothing heals on its own), written in the same save and removed only after Discord
confirms. If a board looks wrong after a deploy, look in the log for the worker's error line (for
example `Unhandled error in DiscordEventChannelBackgroundService loop`).

## Things to set up later (not blocking go-live)

- Automated daily `pg_dump` to a DO Spaces bucket (or any object store). The pre-deploy dumps (**Database backups**) only live on the droplet, so they do not survive losing it.
- Cloudflare **Authenticated Origin Pulls** (commented out in `nginx-lsmanager.conf`) so the droplet only accepts traffic from your CF zone, not just any CF customer.
- A non-root deploy user (create `lsmanager-deploy` with sudo NOPASSWD limited to `systemctl restart lsmanager` and the file copy paths).
- Fail2ban for SSH.
- Unattended-upgrades (`apt-get install unattended-upgrades`).
