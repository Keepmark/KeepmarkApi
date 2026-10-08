#!/bin/sh
# Offline backup of the xbrowsersync production database into the mongo-dumps
# named volume. Set INCLUDE_TEST_DB=1 to also dump xbrowsersynctest.
# Usage: ./scripts/mongo-backup.sh  (from KeepmarkApi/)
# Cron example: 0 3 * * * cd /path/to/KeepmarkApi && ./scripts/mongo-backup.sh
# List: docker compose exec mongo ls /dump
# Fetch: docker compose cp mongo:/dump/<stamp> ./restores/  (or docker cp)
# Restore: docker compose exec -T mongo sh -c 'mongorestore -u "$MONGO_INITDB_ROOT_USERNAME" ...'
#   --authenticationDatabase admin --drop --db xbrowsersync /dump/<stamp>/xbrowsersync
#   (indexes are recreated by the API at startup). Keeps the last 7 dumps.
set -eu
cd "$(dirname "$0")/.."
set -a
# shellcheck disable=SC1091
. ./.env
set +a
STAMP=$(date +%Y%m%d-%H%M%S)
# Credentials travel via container env (invisible to host ps); the lock file
# serializes overlapping cron runs. Stale locks (kill -9, >2h old) are
# reclaimed so one dead run doesn't silence all future backups.
LOCK=/tmp/keepmarkapi-backup.lock
if ! mkdir "$LOCK" 2>/dev/null; then
  if find "$LOCK" -mmin +120 >/dev/null 2>&1 && [ -n "$(find "$LOCK" -mmin +120 2>/dev/null)" ]; then
    echo "removing stale backup lock (>2h)" >&2
    rmdir "$LOCK" 2>/dev/null || { echo "backup already running, skipping" >&2; exit 0; }
    mkdir "$LOCK" 2>/dev/null || { echo "backup already running, skipping" >&2; exit 0; }
  else
    echo "backup already running, skipping" >&2
    exit 0
  fi
fi
trap 'rmdir "$LOCK"' EXIT INT TERM
docker compose exec -T \
  -e BACKUP_USER="$MONGO_ROOT_USER" -e BACKUP_PWD="$MONGO_ROOT_PASSWORD" -e BACKUP_STAMP="$STAMP" \
  -e BACKUP_TESTDB="${INCLUDE_TEST_DB:-0}" \
  mongo sh -c '
  set -eu
  mongodump --quiet -u "$BACKUP_USER" -p "$BACKUP_PWD" --authenticationDatabase admin \
    --db xbrowsersync --out "/dump/$BACKUP_STAMP" >/dev/null
  if [ "$BACKUP_TESTDB" = "1" ]; then
    mongodump --quiet -u "$BACKUP_USER" -p "$BACKUP_PWD" --authenticationDatabase admin \
      --db xbrowsersynctest --out "/dump/$BACKUP_STAMP" >/dev/null
  fi
  ls -dt /dump/*/ | tail -n +8 | xargs -r rm -rf
  echo "backup: /dump/$BACKUP_STAMP"'
