<!--
Docker Hub repository page for keepmark/keepmark-api.
Short description (Docker Hub limit 100 chars):
Drop-in xBrowserSync sync server (.NET 10 + MongoDB): same API, safer writes, easy to run.
Paste everything below this comment into Docker Hub > Repository > Overview.
-->

<p align="center">
  <img src="https://raw.githubusercontent.com/Keepmark/KeepmarkApi/master/docs/img/keepmark.png" width="128" height="128" alt="Keepmark" />
</p>

# Keepmark Server

**A drop-in xBrowserSync sync service: same contract, safer writes, easier to run.**

Keepmark Server stores encrypted bookmark syncs for xBrowserSync and Keepmark
clients. It speaks the exact xBrowserSync API v1.1.13 contract, so **official
xBrowserSync apps and Keepmark apps work unchanged**, and adds an opt-in v1.2
contract (revisions, conflict detection, cheap change checks, sync delete).

It never sees plaintext: clients encrypt before upload, the server stores
ciphertext only.

- **Source code, issues and full docs:** <https://github.com/Keepmark/KeepmarkApi>
- **Project home:** <https://github.com/Keepmark>
- **Keepmark for browsers:** [Firefox Add-ons](https://addons.mozilla.org/en-US/firefox/addon/keepmark/),
  [Chrome Web Store](https://chromewebstore.google.com/detail/jaaekkbgpeiikepahjelfbkfgpjphbhb)
- **License:** AGPL-3.0-only

![Keepmark in a browser and on Android](https://raw.githubusercontent.com/Keepmark/KeepmarkApi/master/docs/img/ecosystem.png)

## Tags

| Tag | Notes |
|---|---|
| `1.2.0`, `latest` | Current release, `linux/amd64` |

Pin a version tag (`1.2.0`) in production; `latest` moves with each release.

## Why run it

- **Nothing changes for your users.** Same endpoints, same error codes, same
  MongoDB layout, byte-identical v1.1 responses. No user migration.
- **No silent overwrites.** Every conditional write is an atomic
  compare-and-swap: two clients pushing on the same base get one `200` and one
  `409`, instead of losing one sync.
- **Built to be public.** Per-IP throttling with `Retry-After`, a daily new-sync
  quota, an origin allow-list, security headers, sync IDs truncated in every
  log line, optional deletion of unused syncs.
- **Small and auditable.** One runtime dependency (`MongoDB.Driver`), about 2k
  lines of logic, 193 unit tests plus live contract scripts.

## Requirements

The image contains the API only. It needs **MongoDB** (7.x tested). Indexes
are created automatically at startup.

> The API connects to MongoDB at startup. If MongoDB is unreachable it logs
> `Unable to connect to database` after `Db__ConnTimeout` (30 s) and exits
> with code 1; wrong credentials fail at once with `Authentication failed`.
> With `restart: unless-stopped` it retries until MongoDB is ready.

## Quick start with Docker Compose (API + MongoDB)

1. Create a folder and download the MongoDB init script. It creates the
   least-privilege database user the API connects with, on the first start only:

   ```sh
   mkdir keepmark && cd keepmark
   curl -fsSL --create-dirs -o mongo-init/01-create-user.sh \
     https://raw.githubusercontent.com/Keepmark/KeepmarkApi/master/mongo-init/01-create-user.sh
   chmod -R a+rX mongo-init   # the mongo container user must be able to read it
   ```

2. Create `.env` with real passwords, then `chmod 600 .env`:

   ```sh
   MONGO_ROOT_USER=root
   MONGO_ROOT_PASSWORD=change-me-root
   XBROWSERSYNC_DB_USER=xbrowsersyncdb
   XBROWSERSYNC_DB_PWD=change-me-app
   ```

3. Create `docker-compose.yml`:

   ```yaml
   services:
     mongo:
       image: mongo:7.0
       restart: unless-stopped
       environment:
         MONGO_INITDB_ROOT_USERNAME: ${MONGO_ROOT_USER}
         MONGO_INITDB_ROOT_PASSWORD: ${MONGO_ROOT_PASSWORD}
         XBROWSERSYNC_DB_USER: ${XBROWSERSYNC_DB_USER}
         XBROWSERSYNC_DB_PWD: ${XBROWSERSYNC_DB_PWD}
       volumes:
         - mongo-data:/data/db
         - ./mongo-init:/docker-entrypoint-initdb.d:ro
       healthcheck:
         test: ["CMD-SHELL", "mongosh --quiet -u $$MONGO_INITDB_ROOT_USERNAME -p $$MONGO_INITDB_ROOT_PASSWORD --authenticationDatabase admin --eval 'db.adminCommand(\"ping\")'"]
         interval: 10s
         timeout: 5s
         retries: 5
         start_period: 20s

     api:
       image: keepmark/keepmark-api:1.2.0
       restart: unless-stopped
       depends_on:
         mongo:
           condition: service_healthy
       environment:
         Db__Host: mongo
         Db__AuthSource: admin
         XBROWSERSYNC_DB_USER: ${XBROWSERSYNC_DB_USER}
         XBROWSERSYNC_DB_PWD: ${XBROWSERSYNC_DB_PWD}
         Status__Message: "Welcome to my Keepmark service!"
       volumes:
         - api-logs:/app/logs
       ports:
         - "8080:8080"

   volumes:
     mongo-data:
     api-logs:
   ```

4. Start and check:

   ```sh
   docker compose up -d
   curl http://localhost:8080/info
   ```

   Expected: `{"location":"","maxSyncSize":1048576,"message":"Welcome to my Keepmark service!","status":1,"version":"1.2.0"}`.

   Logs: `docker compose logs -f api` (console) and
   `docker compose exec api cat /app/logs/api.log` (rotating file).

5. In the xBrowserSync or Keepmark app, set the service URL to your server
   (for public use, put it behind HTTPS; see below).

The [GitHub repository](https://github.com/Keepmark/KeepmarkApi) has the full
Compose stack with rolling MongoDB backups and hot-reloadable log level.

## Run with an existing MongoDB

```sh
docker run -d --name keepmark-server --restart unless-stopped -p 8080:8080 \
  -e Db__Host=your-mongo-host \
  -e XBROWSERSYNC_DB_USER=xbrowsersyncdb \
  -e XBROWSERSYNC_DB_PWD='your-db-password' \
  keepmark/keepmark-api:1.2.0
```

If MongoDB runs on the same host, use `--network host` and
`-e Db__Host=127.0.0.1` instead of `-p 8080:8080`.

The database user needs `readWrite` on the `xbrowsersync` database:

```sh
mongosh --eval "db.getSiblingDB('admin').createUser({user:'xbrowsersyncdb',pwd:'<pwd>',roles:[{role:'readWrite',db:'xbrowsersync'}]})"
```

## Already running xBrowserSync?

Keepmark Server uses your xBrowserSync MongoDB database as it is: no data
conversion, users change nothing, and going back means starting Node again.
Stop the Node service, start this image against the same database (command
above). Read the
[migration guide](https://github.com/Keepmark/KeepmarkApi/blob/master/docs/migration.md)
first: backup, settings mapping, checks and rollback.

## Configuration

Every setting is an environment variable. Nested keys use `__`
(`Status__Message`, `Throttle__MaxRequests`). Names match the xBrowserSync
`settings.json`. Settings apply at startup: restart the container after a
change.

| Variable | Default | What it does |
|---|---|---|
| `Db__Host` / `Db__Port` / `Db__Name` | `127.0.0.1` / `27017` / `xbrowsersync` | MongoDB location |
| `XBROWSERSYNC_DB_USER` / `XBROWSERSYNC_DB_PWD` | empty | MongoDB credentials (used when `Db__Username` / `Db__Password` are empty) |
| `Db__AuthSource` | `admin` | Database that holds the user |
| `Db__UseSrv` / `Db__Ssl` | `false` | `mongodb+srv://` URL / TLS to MongoDB |
| `Db__ConnTimeout` | `30000` | Connection timeout, ms |
| `Server__Port` | `8080` | Listening port in the container |
| `Server__RelativePath` | `/` | Base path when served under a sub-path (e.g. `/api`) |
| `Server__BehindProxy` | `false` | Trust `X-Forwarded-For`; enable **only** behind your own proxy |
| `Server__Https__Enabled` / `__CertPath` / `__KeyPath` | `false` | Serve HTTPS directly with a mounted PEM cert and key |
| `Status__Online` | `true` | `false` = service offline, requests refused |
| `Status__AllowNewSyncs` | `true` | `false` = existing syncs work, new ones refused |
| `Status__Message` | empty | Message shown to users in `/info` (markdown) |
| `Location` | empty | Two-letter country code shown in `/info` |
| `AllowedOrigins__0`, `__1`, … | empty (any) | Allowed `Origin` values, exact strings |
| `DailyNewSyncsLimit` | `3` | New syncs per IP per UTC day, `0` = no limit |
| `MaxSyncs` | `5242` | Total syncs before new ones are refused, `0` = unlimited |
| `MaxSyncSize` | `1048576` | Max sync size in bytes (1 MB) |
| `SyncExpiryDays` | `0` | Delete syncs not read or written for N days, `0` = never |
| `Throttle__MaxRequests` / `Throttle__TimeWindow` | `1000` / `300000` | Requests per IP per window (ms), `0` = off |
| `Log__Stdout__Level` | `info` | `trace`, `debug`, `info`, `warn`, `error`, `fatal` |
| `Log__File__Enabled` / `Log__File__Level` | `true` / `info` | Rotating file log (`false` = console only) |
| `Log__File__Path` | `/app/logs/api.log` | Log file; mount a volume on `/app/logs` to keep it |
| `Log__File__RotationPeriod` / `Log__File__RotatedFilesToKeep` | `1d` / `5` | Log rotation |

Logs never contain bodies, keys or full sync IDs.

## HTTPS

- **Reverse proxy (recommended):** terminate TLS at Caddy, nginx or Traefik and
  set `Server__BehindProxy=true` so client IPs are real.
- **Direct:** mount cert and key, set `Server__Https__Enabled=true`,
  `Server__Https__CertPath` and `Server__Https__KeyPath`.

## Image details

- Base: `mcr.microsoft.com/dotnet/aspnet:10.0`, runs as the non-root `app` user.
- Port: `8080`.
- Healthcheck: `GET /info` every 30 s.
- Data lives in MongoDB only. Optional volume: `/app/logs` (file logs,
  writable by `app` out of the box).

## Documentation

- [README](https://github.com/Keepmark/KeepmarkApi#readme)
- [Migration guide](https://github.com/Keepmark/KeepmarkApi/blob/master/docs/migration.md): move from xBrowserSync API, step by step
- [Operator guide](https://github.com/Keepmark/KeepmarkApi/blob/master/docs/operators.md): why switch, FAQ
- [Developer and operations guide](https://github.com/Keepmark/KeepmarkApi/blob/master/docs/DEVELOPMENT.md): API contracts, Docker, logging, backups, security notes

Questions and bug reports: [GitHub issues](https://github.com/Keepmark/KeepmarkApi/issues).

## License

Copyright (C) 2026 Yari Melzani. Free software under the **GNU Affero General
Public License, version 3 only** (AGPL-3.0-only). If you run a modified version
as a network service, you must offer its source code to its users (AGPL §13).
Source: <https://github.com/Keepmark/KeepmarkApi>.

Independent implementation, compatible with the xBrowserSync protocol. Not
affiliated with or endorsed by xBrowserSync.
