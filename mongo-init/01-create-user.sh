#!/bin/sh
# Runs once at first mongo init (empty data volume). Creates the least-privilege
# app user with readWrite on both databases, mirroring api/README.md manual setup.
# The API creates the TTL (expiresAt) + ipAddress indexes itself at startup.
set -eu
mongosh -u "$MONGO_INITDB_ROOT_USERNAME" -p "$MONGO_INITDB_ROOT_PASSWORD" --quiet --eval "
db.getSiblingDB('admin').createUser({
  user: '$XBROWSERSYNC_DB_USER',
  pwd: '$XBROWSERSYNC_DB_PWD',
  roles: [
    { role: 'readWrite', db: 'xbrowsersync' },
    { role: 'readWrite', db: 'xbrowsersynctest' }
  ]
});"
