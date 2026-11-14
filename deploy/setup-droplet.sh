#!/usr/bin/env bash
# One-time droplet bootstrap for LSManager.
# Target: Ubuntu 22.04 / 24.04. Run as root or with sudo.
# Usage:   bash setup-droplet.sh
#
# The database is NOT on the droplet: the app uses a DigitalOcean Managed Database. This script
# only installs the Postgres CLIENT (pg_dump / psql, for deploy backups) and leaves the
# connection string as REPLACE_ME for you to fill in (deploy/RUNBOOK.md, Step 2).

set -euo pipefail

APP_USER="lsmanager"
APP_DIR="/var/www/lsmanager"
ENV_FILE="/etc/lsmanager/env"

if [[ $EUID -ne 0 ]]; then
  echo "Run as root (sudo bash $0)" >&2
  exit 1
fi

echo "==> Updating apt"
apt-get update -y
apt-get upgrade -y

echo "==> Installing .NET 10 runtime, nginx, ufw"
apt-get install -y wget gnupg ca-certificates openssl ufw nginx

# .NET 10 comes from Ubuntu's own packages, which is what Microsoft's install guide says to use:
# Microsoft's package feed no longer carries .NET for Ubuntu 24.04, and mixing the two feeds
# breaks installs. Ubuntu 24.04 has aspnetcore-runtime-10.0 in its normal archive, so it gets
# security fixes through ordinary apt upgrades. Ubuntu 22.04 only has it in Canonical's
# .NET backports PPA, so add that there. .NET 10 is the only runtime the app needs.
if ! dotnet --list-runtimes 2>/dev/null | grep -q '^Microsoft.AspNetCore.App 10\.'; then
  if [[ "$(. /etc/os-release && echo "$VERSION_ID")" == "22.04" ]]; then
    apt-get install -y software-properties-common
    add-apt-repository -y ppa:dotnet/backports
    apt-get update -y
  fi
  apt-get install -y aspnetcore-runtime-10.0
fi

# pg_dump refuses to dump a server newer than itself, and the managed database runs Postgres 18
# while Ubuntu ships an older client. Take the client from the PostgreSQL project's own apt repo.
echo "==> Installing the Postgres 18 client (for deploy backups)"
if ! command -v pg_dump >/dev/null 2>&1 || ! pg_dump --version | grep -q ' 18\.'; then
  apt-get install -y postgresql-common
  /usr/share/postgresql-common/pgdg/apt.postgresql.org.sh -y
  apt-get install -y postgresql-client-18
fi

echo "==> Creating app user and directories"
id -u "$APP_USER" >/dev/null 2>&1 || useradd --system --shell /usr/sbin/nologin --home "$APP_DIR" "$APP_USER"
# /var/lib/lsmanager/keys is the persistent Data Protection key ring. It MUST
# live outside $APP_DIR because the deploy script replaces $APP_DIR wholesale on
# every release; keeping the keys here is what keeps people signed in across redeploys.
# /var/lib/lsmanager/uploads holds ToD screenshots for the same reason (Uploads__Root).
mkdir -p "$APP_DIR" /etc/lsmanager /var/log/lsmanager /var/lib/lsmanager/keys /var/lib/lsmanager/uploads/tods
chown -R "$APP_USER:$APP_USER" "$APP_DIR" /var/log/lsmanager /var/lib/lsmanager
chmod 700 /var/lib/lsmanager/keys

echo "==> Configuring firewall (allow OpenSSH + HTTPS only)"
ufw allow OpenSSH
ufw allow 443/tcp
ufw allow 80/tcp   # for ACME challenges if you ever switch off Origin Cert
ufw --force enable

echo "==> Writing environment file ($ENV_FILE)"
if [[ ! -f "$ENV_FILE" ]]; then
  cat > "$ENV_FILE" <<EOF
# LSManager runtime environment.
# This file is read by the systemd unit (EnvironmentFile=). chmod 640, owner root:lsmanager.
# Every REPLACE_ME is a secret: keep the real values in your password manager, never in git.
# deploy/RUNBOOK.md, "Settings reference", lists every key.
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://127.0.0.1:5000
# The DigitalOcean Managed Database (DO console -> Databases -> Connection details).
# Example shape: Host=<host>;Port=25060;Database=defaultdb;Username=doadmin;Password=<pw>;SSL Mode=Require
ConnectionStrings__DefaultConnection=REPLACE_ME
# Persistent Data Protection key ring (outside the deploy artifact dir) so
# login sessions survive redeploys.
DataProtection__KeyRingPath=/var/lib/lsmanager/keys
# ToD screenshots, also outside the deploy artifact dir so they survive redeploys.
Uploads__Root=/var/lib/lsmanager/uploads
Discord__ClientId=REPLACE_ME
Discord__ClientSecret=REPLACE_ME
Discord__BotToken=REPLACE_ME
Discord__PublicKey=REPLACE_ME
# Your own Discord user id (Discord: Settings > Advanced > Developer Mode, then
# right-click your name > Copy User ID). The ONLY way an account becomes super admin.
SuperAdmin__DiscordUserId=REPLACE_ME
# Cloudflare IPv4 / IPv6 ranges (https://www.cloudflare.com/ips/).
# Update periodically; nginx also handles this via real_ip_from.
ForwardedHeaders__KnownNetworks__0=173.245.48.0/20
ForwardedHeaders__KnownNetworks__1=103.21.244.0/22
ForwardedHeaders__KnownNetworks__2=103.22.200.0/22
ForwardedHeaders__KnownNetworks__3=103.31.4.0/22
ForwardedHeaders__KnownNetworks__4=141.101.64.0/18
ForwardedHeaders__KnownNetworks__5=108.162.192.0/18
ForwardedHeaders__KnownNetworks__6=190.93.240.0/20
ForwardedHeaders__KnownNetworks__7=188.114.96.0/20
ForwardedHeaders__KnownNetworks__8=197.234.240.0/22
ForwardedHeaders__KnownNetworks__9=198.41.128.0/17
ForwardedHeaders__KnownNetworks__10=162.158.0.0/15
ForwardedHeaders__KnownNetworks__11=104.16.0.0/13
ForwardedHeaders__KnownNetworks__12=104.24.0.0/14
ForwardedHeaders__KnownNetworks__13=172.64.0.0/13
ForwardedHeaders__KnownNetworks__14=131.0.72.0/22
ForwardedHeaders__KnownNetworks__15=2400:cb00::/32
ForwardedHeaders__KnownNetworks__16=2606:4700::/32
ForwardedHeaders__KnownNetworks__17=2803:f800::/32
ForwardedHeaders__KnownNetworks__18=2405:b500::/32
ForwardedHeaders__KnownNetworks__19=2405:8100::/32
ForwardedHeaders__KnownNetworks__20=2a06:98c0::/29
ForwardedHeaders__KnownNetworks__21=2c0f:f248::/32
# Trust the loopback nginx as a proxy too
ForwardedHeaders__KnownProxies__0=127.0.0.1
EOF
  chown root:"$APP_USER" "$ENV_FILE"
  chmod 640 "$ENV_FILE"
  echo
  echo "Wrote $ENV_FILE. Edit it now and replace every REPLACE_ME (deploy/RUNBOOK.md, Step 2)."
else
  echo "$ENV_FILE already exists - leaving it alone."
fi

echo
echo "==> Bootstrap done."
echo "Next steps:"
echo "  1. Edit $ENV_FILE and replace every REPLACE_ME (connection string, Discord values, SuperAdmin id)."
echo "  2. Place the Cloudflare Origin Cert at /etc/ssl/cloudflare/origin.pem and key at /etc/ssl/cloudflare/origin.key (chmod 600)."
echo "  3. Copy nginx-lsmanager.conf into /etc/nginx/sites-available/, symlink, and reload nginx."
echo "  4. Copy lsmanager.service into /etc/systemd/system/, then 'systemctl daemon-reload && systemctl enable lsmanager'."
echo "  5. Run the deploy script from your dev machine to publish the app."
