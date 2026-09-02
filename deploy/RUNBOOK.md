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
- installs the headless-Chromium OS libraries used to render event-board images
- creates a `lsmanager` system user
- enables ufw with 22/80/443 only
- creates the `lsmanager` Postgres database + user with a random password
- writes `/etc/lsmanager/env` with that connection string, placeholders for Discord secrets, and a persistent Playwright browser-cache path

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

This builds the Angular activity, publishes the .NET app, syncs to the droplet, and starts the service. The first start will run EF migrations against the empty Postgres DB and create the schema.

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
| Tail logs | `ssh root@<IP> 'sudo journalctl -u lsmanager -f'` |
| Service status | `ssh root@<IP> 'sudo systemctl status lsmanager'` |
| Restart service | `ssh root@<IP> 'sudo systemctl restart lsmanager'` |
| Postgres shell | `ssh root@<IP> '/tmp/dbshell.sh'` — see **Database access** below |
| Backup DB | `ssh root@<IP> '/tmp/dbbackup.sh'` — see **Database access** below |

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
# /tmp/dbbackup.sh — dump the REAL production database.
conn=$(grep -m1 'ConnectionStrings__DefaultConnection' /etc/lsmanager/env | cut -d= -f2-)
get() { echo "$conn" | tr ';' '
' | grep -i "^ *$1=" | head -1 | cut -d= -f2- | xargs; }
export PGPASSWORD="$(get Password)"; export PGSSLMODE=require
OUT=/root/lsmanager-prod-$(date +%F-%H%M).sql
pg_dump -h "$(get Host)" -p "$(get Port)" -U "$(get Username)" -d "$(get Database)"   --no-owner --no-acl > "$OUT"
unset PGPASSWORD
ls -lh "$OUT"; grep -c '^CREATE TABLE' "$OUT"   # sanity: expect ~74, not 0
```

Swap `pg_dump` for `psql` in the last block for an interactive shell.

Note DigitalOcean managed databases also take their own automated daily backups
with point-in-time recovery, so the console is the faster path for a real
restore. The dump above is for pre-deploy safety and for moving data around.

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

## One-time migration: Chromium for event-board images

Party-setup events post their sign-up board to Discord as a **rendered PNG** (the
"Esports HUD" — colour, 3 parties per row). Rendering uses headless Chromium via
Playwright. Fresh bootstraps (`setup-droplet.sh`) now install the Chromium OS
libraries, provision a persistent browser cache, and point the app at it
automatically. **Existing droplets created before this change need a one-time
fixup.** Until then nothing breaks — boards just post as the plain **text-embed
fallback** instead of the image.

```bash
ssh root@<IP>

# 1. Persistent browser cache (survives deploys, like the key ring; it's already a
#    ReadWritePath in the shipped unit file). Without this, Chromium re-downloads
#    (~110 MB) on every deploy.
sudo mkdir -p /var/lib/lsmanager/ms-playwright
sudo chown -R lsmanager:lsmanager /var/lib/lsmanager

# 2. Point the app at it (setup-droplet.sh leaves an existing env file untouched, so
#    add this line yourself).
echo 'PLAYWRIGHT_BROWSERS_PATH=/var/lib/lsmanager/ms-playwright' | sudo tee -a /etc/lsmanager/env

# 3. Install the Chromium OS libraries (root/apt). Ubuntu 24.04 renamed several
#    packages with a "t64" suffix, and there libasound2 is a virtual package with
#    no install candidate — so install each name, falling back to its t64 variant
#    (a single `apt-get install` of the whole list would abort on the first such
#    package and install nothing). This is exactly what setup-droplet.sh does.
sudo apt-get update -y
for lib in libnss3 libnspr4 libdbus-1-3 libatk1.0-0 libatk-bridge2.0-0 libcups2 \
           libdrm2 libatspi2.0-0 libx11-6 libxcomposite1 libxdamage1 libxext6 \
           libxfixes3 libxrandr2 libgbm1 libxcb1 libxkbcommon0 libpango-1.0-0 \
           libcairo2 libasound2 libwayland-client0 fonts-liberation fonts-noto-color-emoji; do
  sudo apt-get install -y "$lib" 2>/dev/null \
    || sudo apt-get install -y "${lib}t64" 2>/dev/null \
    || echo "   (skipped $lib — not packaged on this release)"
done

# 4. Restart; the app downloads Chromium into the cache on first boot.
sudo systemctl daemon-reload
sudo systemctl restart lsmanager
```

Verify after restart:

```bash
ssh root@<IP> 'sudo journalctl -u lsmanager | grep -i playwright'
# "Playwright Chromium is installed — event boards will render as images." = good.
# A render failure logs "falling back to the text board" with the cause.
```

> Opt out of the auto-download (e.g. to bake Chromium into a custom image) by
> adding `LSM_PLAYWRIGHT_AUTOINSTALL=0` to `/etc/lsmanager/env`.

## Things to set up later (not blocking go-live)

- Automated daily `pg_dump` to a DO Spaces bucket (or any object store).
- Cloudflare **Authenticated Origin Pulls** (commented out in `nginx-lsmanager.conf`) so the droplet only accepts traffic from your CF zone, not just any CF customer.
- A non-root deploy user (create `lsmanager-deploy` with sudo NOPASSWD limited to `systemctl restart lsmanager` and the file copy paths).
- Fail2ban for SSH.
- Unattended-upgrades (`apt-get install unattended-upgrades`).
