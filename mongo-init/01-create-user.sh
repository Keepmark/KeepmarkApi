#!/bin/sh
# Runs once at first mongo init (empty data volume). Creates the least-privilege
# app user with readWrite on both databases, mirroring api/README.md manual setup.
# The API creates the TTL (expiresAt) + ipAddress indexes itself at startup.
# Passwords travel via environment into mongosh (process.env), never interpolated
# into JS source — quotes and $ in passwords are safe. Re-running against an
# existing volume is a no-op when the user already exists.
set -eu
mongosh -u "$MONGO_INITDB_ROOT_USERNAME" -p "$MONGO_INITDB_ROOT_PASSWORD" --quiet --eval "
if (db.getSiblingDB('admin').getUser(process.env.XBROWSERSYNC_DB_USER) === null) {
  db.getSiblingDB('admin').createUser({
    user: process.env.XBROWSERSYNC_DB_USER,
    pwd: process.env.XBROWSERSYNC_DB_PWD,
    roles: [
      { role: 'readWrite', db: 'xbrowsersync' },
      { role: 'readWrite', db: 'xbrowsersynctest' }
    ]
  });
}"
