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
                -> Postgres 127.0.0.1:5432 (local on droplet)
```

Nothing on the droplet is exposed except SSH (22), HTTP (80, redirect), and HTTPS (443). Postgres is loopback only.

## Prerequisites

- A droplet running Ubuntu 22.04 or 24.04, with a public IPv4 (you have this).
- Postgres installed on the droplet (you have this - Option A).
- A domain in Cloudflare DNS already pointing at your old tunnel.
- Discord application client ID + client secret.
- Local: .NET 8 SDK, Node 20+, OpenSSH (Windows 10+ has it).

## Step 1 - Bootstrap the droplet

SSH in as `root` (or with sudo) and run:

```bash
# from your dev machine
scp deploy/setup-droplet.sh root@<DROPLET_IP>:/tmp/
ssh root@<DROPLET_IP> 'bash /tmp/setup-droplet.sh'
```

The script:
- installs .NET 8 ASP.NET Core runtime, nginx, ufw
- creates a `lsmanager` system user
- enables ufw with 22/80/443 only
- creates the `lsmanager` Postgres database + user with a random password
- writes `/etc/lsmanager/env` with that connection string and placeholders for Discord secrets

At the end it prints the generated DB password - you shouldn't need to write it down (it's already in the env file), but note that it ran.

## Step 2 - Fill in the env file

```bash
ssh root@<DROPLET_IP>
sudo nano /etc/lsmanager/env
```

Set these two values:
- `Discord__ClientId=1320463503848636568`  (your existing client id)
- `Discord__ClientSecret=...`              (from Discord Developer Portal)

Save. Don't change the `ConnectionStrings__DefaultConnection` line.

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

This checks the git tree is clean, runs the tests, builds the Angular activity, publishes the .NET app, uploads it, dumps the database (see **Database backups**), swaps the new build in, starts the service and waits for `/readyz`. The first start will run EF migrations against the empty Postgres DB and create the schema.

On a brand-new droplet there is no database to back up yet, so pass `-SkipBackup` for the first deploy only.

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

**The application does NOT use the droplet's local Postgres.** It connects to a
DigitalOcean **Managed Database** over TLS:

```
Host      dbaas-db-3934998-do-user-36034864-0.e.db.ondigitalocean.com
Port      25060
Database  defaultdb
User      doadmin
```

The local `lsmanager` database still exists but has **zero tables** — it is an
unused leftover from an earlier setup. The command this table used to recommend
(`sudo -u postgres pg_dump lsmanager`) therefore dumped an EMPTY database and
produced a ~700-byte file that looks like a valid dump. A real dump is ~13 MB
across ~74 tables. Do not trust a backup you have not size-checked.

Two more traps:

- **Client version.** The managed server is Postgres 18.x; Ubuntu 24.04 ships
  client 16, and `pg_dump` refuses to dump a newer server. Install the matching
  client once:
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
  has ~70 tables; the bar is low so dropping unused tables later does not trip it.) The
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

## One-time migration: persistent Data Protection keys

The app encrypts Google Sheet refresh tokens (and signs auth cookies / antiforgery
tokens) with the ASP.NET Core Data Protection key ring. That key ring must live
**outside** `/var/www/lsmanager`, because `deploy.ps1` replaces that directory on
every release. Fresh bootstraps (`setup-droplet.sh`) now provision this
automatically; **existing droplets created before this change need a one-time
fixup** or they'll keep losing their Google Sheet connection (and logging
everyone out) on every deploy:

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

After this, each linkshell reconnects its Google Sheet **one final time**; the
key ring then persists across all future deploys, so the connection sticks.

> If Google still drops the connection after ~7 days, the OAuth consent screen in
> Google Cloud Console is in **Testing** mode (refresh tokens expire in 7 days).
> Publish it to **Production** to stop that — that's a Google-side limit, not app code.

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

## Things to set up later (not blocking go-live)

- Automated daily `pg_dump` to a DO Spaces bucket (or any object store). The pre-deploy dumps (**Database backups**) only live on the droplet, so they do not survive losing it.
- Cloudflare **Authenticated Origin Pulls** (commented out in `nginx-lsmanager.conf`) so the droplet only accepts traffic from your CF zone, not just any CF customer.
- A non-root deploy user (create `lsmanager-deploy` with sudo NOPASSWD limited to `systemctl restart lsmanager` and the file copy paths).
- Fail2ban for SSH.
- Unattended-upgrades (`apt-get install unattended-upgrades`).
