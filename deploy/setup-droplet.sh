#!/usr/bin/env bash
# One-time droplet bootstrap for LSManager.
# Target: Ubuntu 22.04 / 24.04. Run as root or with sudo.
# Usage:   bash setup-droplet.sh

set -euo pipefail

APP_USER="lsmanager"
APP_DIR="/var/www/lsmanager"
ENV_FILE="/etc/lsmanager/env"
DB_NAME="lsmanager"
DB_USER="lsmanager"

if [[ $EUID -ne 0 ]]; then
  echo "Run as root (sudo bash $0)" >&2
  exit 1
fi

echo "==> Updating apt"
apt-get update -y
apt-get upgrade -y

echo "==> Installing .NET 8 runtime, Postgres, nginx, ufw"
apt-get install -y wget gnupg ca-certificates openssl ufw nginx postgresql postgresql-contrib

# Microsoft package feed for .NET 8
if ! dotnet --list-runtimes 2>/dev/null | grep -q '^Microsoft.AspNetCore.App 8\.'; then
  wget -q https://packages.microsoft.com/config/ubuntu/$(. /etc/os-release && echo "$VERSION_ID")/packages-microsoft-prod.deb -O /tmp/packages-microsoft-prod.deb
  dpkg -i /tmp/packages-microsoft-prod.deb
  rm /tmp/packages-microsoft-prod.deb
  apt-get update -y
  apt-get install -y aspnetcore-runtime-8.0
fi

echo "==> Creating app user and directories"
id -u "$APP_USER" >/dev/null 2>&1 || useradd --system --shell /usr/sbin/nologin --home "$APP_DIR" "$APP_USER"
mkdir -p "$APP_DIR" /etc/lsmanager /var/log/lsmanager
chown -R "$APP_USER:$APP_USER" "$APP_DIR" /var/log/lsmanager

echo "==> Configuring firewall (allow OpenSSH + HTTPS only)"
ufw allow OpenSSH
ufw allow 443/tcp
ufw allow 80/tcp   # for ACME challenges if you ever switch off Origin Cert
ufw --force enable

echo "==> Creating Postgres database and user"
if [[ -f "$ENV_FILE" ]]; then
  CONNECTION_STRING=$(grep -E '^ConnectionStrings__DefaultConnection=' "$ENV_FILE" | tail -n 1 || true)
  DB_PASSWORD="${CONNECTION_STRING##*Password=}"
  DB_PASSWORD="${DB_PASSWORD%%;*}"

  if [[ -z "$CONNECTION_STRING" || "$DB_PASSWORD" == "$CONNECTION_STRING" || -z "$DB_PASSWORD" ]]; then
    echo "Could not read DB password from existing $ENV_FILE. Fix the env file or move it aside, then rerun." >&2
    exit 1
  fi
else
  DB_PASSWORD=$(openssl rand -base64 32 | tr -d '/+=' | head -c 32)
fi

sudo -u postgres psql <<SQL
DO \$\$
BEGIN
  IF NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = '${DB_USER}') THEN
    CREATE ROLE ${DB_USER} LOGIN PASSWORD '${DB_PASSWORD}';
  ELSE
    ALTER ROLE ${DB_USER} WITH PASSWORD '${DB_PASSWORD}';
  END IF;
END
\$\$;
SQL

if ! sudo -u postgres psql -lqt | cut -d '|' -f 1 | tr -d ' ' | grep -qx "${DB_NAME}"; then
  sudo -u postgres createdb -O "${DB_USER}" "${DB_NAME}"
fi

echo "==> Writing environment file ($ENV_FILE)"
if [[ ! -f "$ENV_FILE" ]]; then
  cat > "$ENV_FILE" <<EOF
# LSManager runtime environment.
# This file is read by the systemd unit (EnvironmentFile=). chmod 600.
ASPNETCORE_ENVIRONMENT=Production
ASPNETCORE_URLS=http://127.0.0.1:5000
ConnectionStrings__DefaultConnection=Host=127.0.0.1;Port=5432;Database=${DB_NAME};Username=${DB_USER};Password=${DB_PASSWORD}
Discord__ClientId=REPLACE_ME
Discord__ClientSecret=REPLACE_ME
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
  echo "Generated DB password: $DB_PASSWORD"
  echo "Saved into $ENV_FILE. Edit it now to set Discord__ClientId and Discord__ClientSecret."
else
  echo "$ENV_FILE already exists - leaving it alone."
fi

echo
echo "==> Bootstrap done."
echo "Next steps:"
echo "  1. Edit $ENV_FILE and fill in Discord__ClientId / Discord__ClientSecret."
echo "  2. Place the Cloudflare Origin Cert at /etc/ssl/cloudflare/origin.pem and key at /etc/ssl/cloudflare/origin.key (chmod 600)."
echo "  3. Copy nginx-lsmanager.conf into /etc/nginx/sites-available/, symlink, and reload nginx."
echo "  4. Copy lsmanager.service into /etc/systemd/system/, then 'systemctl daemon-reload && systemctl enable --now lsmanager'."
echo "  5. Run the deploy script from your dev machine to publish the app."
