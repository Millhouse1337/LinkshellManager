#!/bin/bash
# Dump the REAL production database (the DigitalOcean managed Postgres named in
# /etc/lsmanager/env, NOT the empty local "lsmanager" database) before a deploy.
#
# deploy.ps1 copies this file to /usr/local/bin/lsm-backup.sh on every deploy and
# runs it before stopping the service, because startup migrations can delete data
# and this dump is the quick way back. Run by hand: sudo /usr/local/bin/lsm-backup.sh
#
# Keeps the newest 7 dumps in /var/backups/lsmanager. Exits non-zero (which stops
# the deploy) if the dump looks empty, because an empty dump looks like a real file.
# How to restore: deploy/RUNBOOK.md, "Database backups".

set -euo pipefail

ENV_FILE=/etc/lsmanager/env
BACKUP_DIR=/var/backups/lsmanager
KEEP=7
MIN_BYTES=1000000   # a real dump is ~13 MB; an empty database dumps to under 1 KB
MIN_TABLES=60       # the real database has ~74 tables

# Read the password etc. from the env file instead of typing it, so it never
# lands in shell history or on the ssh command line.
conn=$(grep -m1 '^ConnectionStrings__DefaultConnection=' "$ENV_FILE" | cut -d= -f2- || true)
if [ -z "$conn" ]; then
    echo "lsm-backup: no ConnectionStrings__DefaultConnection in $ENV_FILE" >&2
    exit 1
fi
get() { echo "$conn" | tr ';' '\n' | grep -i "^ *$1=" | head -1 | cut -d= -f2- | xargs; }

mkdir -p "$BACKUP_DIR"
chmod 700 "$BACKUP_DIR"

OUT="$BACKUP_DIR/lsmanager-prod-$(date -u +%Y%m%d-%H%M%S).sql"

export PGPASSWORD="$(get Password)"
export PGSSLMODE=require
if ! pg_dump -h "$(get Host)" -p "$(get Port)" -U "$(get Username)" -d "$(get Database)" \
        --no-owner --no-acl > "$OUT"; then
    rm -f "$OUT"
    echo "lsm-backup: pg_dump failed" >&2
    exit 1
fi
unset PGPASSWORD
chmod 600 "$OUT"

size=$(stat -c %s "$OUT")
tables=$(grep -c '^CREATE TABLE' "$OUT" || true)
if [ "$size" -lt "$MIN_BYTES" ] || [ "$tables" -lt "$MIN_TABLES" ]; then
    # Keep the bad file under another name for a look, but out of the rotation.
    mv "$OUT" "$OUT.suspect"
    echo "lsm-backup: dump looks wrong ($size bytes, $tables tables). Saved as $OUT.suspect" >&2
    exit 1
fi

# Only prune after a good dump, so a run of failures never deletes the last good ones.
ls -1t "$BACKUP_DIR"/lsmanager-prod-*.sql | tail -n +$((KEEP + 1)) | xargs -r rm -f

echo "lsm-backup: $OUT ($size bytes, $tables tables)"
