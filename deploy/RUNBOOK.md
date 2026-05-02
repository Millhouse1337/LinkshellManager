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
| Postgres shell | `ssh root@<IP> 'sudo -u postgres psql lsmanager'` |
| Backup DB | `ssh root@<IP> 'sudo -u postgres pg_dump lsmanager' > backup-$(date +%F).sql` |

## Things to set up later (not blocking go-live)

- Automated daily `pg_dump` to a DO Spaces bucket (or any object store).
- Cloudflare **Authenticated Origin Pulls** (commented out in `nginx-lsmanager.conf`) so the droplet only accepts traffic from your CF zone, not just any CF customer.
- A non-root deploy user (create `lsmanager-deploy` with sudo NOPASSWD limited to `systemctl restart lsmanager` and the file copy paths).
- Fail2ban for SSH.
- Unattended-upgrades (`apt-get install unattended-upgrades`).
