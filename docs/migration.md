# Migrating from xBrowserSync API to Keepmark Server

This guide moves a running xBrowserSync API service (the Node.js
`xbrowsersync/api`) to Keepmark Server. Your users change nothing: same URL,
same sync IDs, same apps.

There is **no data conversion**. Keepmark Server reads and writes the
xBrowserSync MongoDB database as it is: same database name (`xbrowsersync`),
same collections (`bookmarks`, `newsynclogs`), same ID format, same fields.

## The short version

You have Node xBrowserSync and MongoDB on the same server, Node listening on
port 8080. Back up, stop Node, start Keepmark Server on the same port and the
same database:

```sh
mongodump --db xbrowsersync --out ~/xbs-backup-$(date +%F)

docker pull keepmark/keepmark-api:1.2.0

sudo systemctl stop xbrowsersync      # or however you run the Node service

docker run -d --name keepmark-server --restart unless-stopped --network host \
  -e Db__Host=127.0.0.1 \
  -e XBROWSERSYNC_DB_USER=xbrowsersyncdb \
  -e XBROWSERSYNC_DB_PWD='your-db-password' \
  -e Log__File__Enabled=false \
  keepmark/keepmark-api:1.2.0

curl localhost:8080/info
```

The image is published on Docker Hub as
[`keepmark/keepmark-api`](https://hub.docker.com/r/keepmark/keepmark-api).
Prefer building it yourself? `git clone` this repository and run
`docker build -t keepmark/keepmark-api:1.2.0 .` instead of the pull.

Done. Your reverse proxy, TLS and DNS stay as they are, because Keepmark
Server answers on the same port. `--network host` lets the container reach a
MongoDB that listens only on `127.0.0.1`. Logs go to `docker logs
keepmark-server`.

The DB user and password are the ones Node already uses (`XBROWSERSYNC_DB_USER`
/ `XBROWSERSYNC_DB_PWD`, or `db.username` / `db.password` in Node's
`settings.json`).

**To go back:** `docker stop keepmark-server`, then start Node again. Same
database, nothing to undo.

## Before you start

- **Back up.** `mongodump --db xbrowsersync` (add `-u`, `-p`,
  `--authenticationDatabase admin` if your Mongo requires login).
- **Note your Node settings.** Anything you changed in Node's
  `config/settings.json` must be passed to Keepmark Server (see
  [Settings](#translate-your-settings)). Unchanged defaults need nothing.
- **Check one thing about expiry.** Did you create the optional TTL index that
  deletes syncs unused for 3 weeks? See [Inactive sync expiry](#inactive-sync-expiry):
  by default Keepmark Server **removes** that index.

## Option A: keep your MongoDB (recommended)

This is the short version above. Keepmark Server connects to the MongoDB that
Node uses. Nothing is copied.

- MongoDB on another host: drop `--network host`, publish the port with
  `-p 8080:8080`, and set `-e Db__Host=<mongo host>`.
- Want to try first without touching production? Restore the backup into a
  second database (`mongorestore --nsFrom 'xbrowsersync.*' --nsTo
  'xbrowsersynctest.*' ...`), start Keepmark Server with
  `-e Db__Name=xbrowsersynctest -e Server__Port=8081` next to Node, and point a
  test client at port 8081.

## Option B: move to the bundled MongoDB

Use this to retire the old MongoDB and run everything with Docker Compose
(API + MongoDB 7 + backup script).

```sh
git clone https://github.com/Keepmark/KeepmarkApi.git keepmark-server && cd keepmark-server
cp .env.example .env && chmod 600 .env   # set the four passwords

sudo systemctl stop xbrowsersync         # no writes from now on
mongodump --db xbrowsersync --archive=xbs.archive
sudo systemctl stop mongod               # frees port 27017 for the new Mongo

docker compose up -d mongo               # first start creates the DB user
docker compose exec -T mongo sh -c 'mongorestore -u "$MONGO_INITDB_ROOT_USERNAME" -p "$MONGO_INITDB_ROOT_PASSWORD" --authenticationDatabase admin --archive' < xbs.archive
docker compose up -d                     # pulls keepmark/keepmark-api
curl localhost:8080/info
```

Downtime is the length of the dump and restore, usually under a minute. Set
`API_PORT` in `.env` if 8080 is taken. Keep `xbs.archive`: restoring it into
the old Mongo is your way back.

## Translate your settings

Keys keep their xBrowserSync names. Pass them as environment variables, with
`__` between levels (`-e Status__Message=...` in `docker run`, or the
`environment:` block of `docker-compose.yml`).

| Node `settings.json` | Keepmark Server | Note |
|---|---|---|
| `server.port` | `Server__Port` | default `8080` on both |
| `server.behindProxy` | `Server__BehindProxy` | only behind your own proxy |
| `server.relativePath` | `Server__RelativePath` | |
| `server.https.*` | `Server__Https__Enabled`, `__CertPath`, `__KeyPath` | PEM files, as in Node |
| `db.host`, `db.port`, `db.name`, `db.authSource` | `Db__Host`, `Db__Port`, `Db__Name`, `Db__AuthSource` | |
| `db.username`, `db.password` | `Db__Username`, `Db__Password` | or `XBROWSERSYNC_DB_USER` / `_PWD`, as in Node |
| `db.useSRV`, `db.ssl`, `db.connTimeout` | `Db__UseSrv`, `Db__Ssl`, `Db__ConnTimeout` | |
| `status.online`, `status.allowNewSyncs`, `status.message` | `Status__Online`, `Status__AllowNewSyncs`, `Status__Message` | |
| `location` | `Location` | |
| `maxSyncs` | `MaxSyncs` | default `5242` on both |
| `maxSyncSize` | `MaxSyncSize` | **Node default `512000`, Keepmark `1048576`**: set `512000` to keep the old limit |
| `dailyNewSyncsLimit` | `DailyNewSyncsLimit` | default `3` on both |
| `throttle.maxRequests`, `throttle.timeWindow` | `Throttle__MaxRequests`, `Throttle__TimeWindow` | |
| `allowedOrigins` | `AllowedOrigins__0`, `AllowedOrigins__1`, … | exact strings only, no regular expressions |
| `log.stdout.*`, `log.file.*` | `Log__Stdout__*`, `Log__File__*` | |
| manual TTL index on `bookmarks.lastAccessed` | `SyncExpiryDays` | see below |

The full list of settings is in the [README](../README.md#configuration).

## Inactive sync expiry

xBrowserSync's README suggested an optional MongoDB index that deletes syncs
unused for 3 weeks. Keepmark Server manages that index from one setting,
`SyncExpiryDays`, at every startup:

| Your database | `SyncExpiryDays=0` (default) | `SyncExpiryDays=21` |
|---|---|---|
| has the 3-week index | index **removed**: syncs are kept forever | index kept as is, nothing changes |
| has no index | nothing changes | index created: syncs unused for 21+ days are **deleted** within a minute |

So: if you created the index, set `-e SyncExpiryDays=21` to keep your current
behavior. If you never did and want expiry now, first count what would go:

```sh
mongosh xbrowsersync --eval 'db.bookmarks.countDocuments({lastAccessed: {$lt: new Date(Date.now() - 21*86400000)}})'
```

Details: [developer guide](DEVELOPMENT.md#inactive-sync-expiry).

## Check that it works

1. `curl localhost:8080/info` answers with `"status":1` and `"version":"1.2.0"`.
2. Sync counts match before and after:
   `mongosh xbrowsersync --eval 'db.bookmarks.countDocuments()'`.
3. Open an existing xBrowserSync or Keepmark app and sync. It must work with no
   change on the device.
4. Optional, on a test database only (they create syncs):
   `BASE=http://localhost:8080 ./scripts/e2e-v1.sh` (26 checks).

If the service exits with `Unable to connect to database`, check `Db__Host`
and the credentials (`docker logs keepmark-server`).

## What happens to the existing data

- Syncs are served unchanged. Old apps see byte-identical responses.
- Keepmark Server adds a few fields (`revision`, `hash`, `size`, `createdAt`,
  `metaStamp`) to a sync the first time it **writes** it. Nothing is
  rewritten at startup. Node ignores these fields.
- Until that first write, apps using the newer v1.2 protocol see the sync as
  `revision: 0`. That is expected and costs nothing.

## Rolling back, or running both

The database stays fully readable by Node at any time, so rolling back is
stopping Keepmark Server and starting Node.

If Node writes a sync after Keepmark Server did (during a rollback, or with
both services on the same database), Keepmark Server notices on the next read:
that sync is served as `revision: 0` until Keepmark Server writes it again. This
keeps v1.2 apps from getting stale "no changes" answers or overwriting what
Node wrote. You do not have to do anything.

Running both on one database works, but keep it short: Node can silently lose
one of two simultaneous writes to the same sync; Keepmark Server cannot.

## Tested

This procedure was tested against a copy of a database written by the Node
xBrowserSync API v1.1.13: dump/restore copy, reads and writes from v1.1 and
v1.2 clients, conflict detection, Node writing the same syncs afterwards
(rollback), and back again.
