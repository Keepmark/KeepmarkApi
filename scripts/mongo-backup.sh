#!/bin/sh
# Offline backup of the xbrowsersync databases (bookmarks + newsynclogs)
# into the mongo-dumps named volume.
# Usage: ./scripts/mongo-backup.sh  (from KeepmarkApi/)
# Cron example: 0 3 * * * cd /path/to/KeepmarkApi && ./scripts/mongo-backup.sh
# List: docker compose exec mongo ls /dump
# Fetch: docker cp keepmarkapi-mongo-1:/dump/<stamp> ./restores/
# Keeps the last 7 dumps.
set -eu
cd "$(dirname "$0")/.."
set -a
# shellcheck disable=SC1091
. ./.env
set +a
STAMP=$(date +%Y%m%d-%H%M%S)
docker compose exec -T mongo sh -c "
  set -eu
  mongodump --quiet -u '$MONGO_ROOT_USER' -p '$MONGO_ROOT_PASSWORD' --authenticationDatabase admin \
    --db xbrowsersync --out /dump/$STAMP >/dev/null
  mongodump --quiet -u '$MONGO_ROOT_USER' -p '$MONGO_ROOT_PASSWORD' --authenticationDatabase admin \
    --db xbrowsersynctest --out /dump/$STAMP >/dev/null
  ls -dt /dump/*/ | tail -n +8 | xargs -r rm -rf
  echo backup: /dump/$STAMP"
