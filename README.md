<p align="center">
  <img src="docs/img/keepmark.png" width="128" height="128" alt="Keepmark" />
</p>

<h1 align="center">Keepmark Server</h1>

<p align="center">
  <b>A drop-in xBrowserSync sync service: same contract, safer writes, easier to run.</b>
</p>

<p align="center">
  <img src="https://img.shields.io/badge/.NET-10-512bd4" alt=".NET 10" />
  <img src="https://img.shields.io/badge/MongoDB-7-47a248" alt="MongoDB" />
  <img src="https://img.shields.io/badge/xBrowserSync%20API-1.1.13%20%2B%201.2-243878" alt="xBrowserSync API 1.1.13 + 1.2" />
  <img src="https://img.shields.io/badge/license-AGPL--3.0--only-4a5170" alt="AGPL-3.0-only" />
</p>

---

Keepmark Server stores encrypted bookmark syncs for xBrowserSync and
Keepmark clients. It speaks the exact xBrowserSync API v1.1.13 contract, so
**official xBrowserSync apps and Keepmark apps work unchanged**, and adds an
opt-in v1.2 contract for clients that want more.

It never sees plaintext: clients encrypt before upload, the server stores
ciphertext only.

## Why run it

- **Nothing changes for your users.** Same endpoints, same error codes, same
  MongoDB layout, byte-identical v1.1 responses. No user migration, ever.
- **No silent overwrites.** Every conditional write is an atomic
  compare-and-swap. Two clients pushing on the same base get one `200` and
  one `409`, where the original service returns `200` twice and silently
  loses one sync.
- **Easy to operate.** Docker Compose with API, MongoDB and rolling backups.
  Healthchecks, auto-created indexes, rotating file logs and log verbosity
  you can change on the fly, without a restart.
- **Built to be public.** Per-IP throttling with `Retry-After`, a daily
  new-sync quota, an origin allow-list, security headers, sync IDs
  truncated in every log line, and optional deletion of syncs left unused
  for N days (`SyncExpiryDays`).
- **Small and auditable.** One runtime dependency (`MongoDB.Driver`), about
  2k lines of logic, 193 unit tests plus live contract scripts.

## What v1.2 adds

Clients opt in with `Accept-Version: ^1.2.0`; everyone else gets v1.1.

- **Revisions** on every sync (`revision`, `hash`, `size`, `createdAt`) and
  revision-based conflict detection.
- **Cheap "anything new?" checks:** `HEAD` and conditional `GET` with `304`.
- **Delete a sync** from the server (`DELETE`).
- **Capabilities** advertised in `/info`, so clients pick features safely,
  including how many days an unused sync is kept (`syncExpiryDays`, `0` = forever).

Keepmark for browsers and Keepmark for Android use v1.2 automatically when
the server offers it.

## Quick start

```sh
cp .env.example .env          # set real passwords (chmod 600 .env)
docker compose up --build -d
curl localhost:8080/info
```

The [developer guide](docs/DEVELOPMENT.md#docker) covers volume ownership,
TLS and backups.

## Configuration

### Docker Compose (`.env`)

Set these in `.env` (copied from `.env.example`) before `docker compose up`.

| Variable | Default | What it does |
|---|---|---|
| `MONGO_ROOT_USER` / `MONGO_ROOT_PASSWORD` | — (required) | MongoDB admin account, created on the first start only |
| `XBROWSERSYNC_DB_USER` / `XBROWSERSYNC_DB_PWD` | — (required) | Least-privilege account the API uses to connect |
| `API_PORT` | `8080` | Host port the API is published on |
| `LOG_LEVEL` | `info` | `trace`, `debug`, `info`, `warn`, `error` or `fatal` |
| `DAILY_NEW_SYNCS_LIMIT` | `3` | New syncs allowed per IP per UTC day, `0` = no limit |
| `SYNC_EXPIRY_DAYS` | `0` | Delete syncs not read or written for this many days, `0` = never |

### Every setting

All settings live in `src/KeepmarkApi/appsettings.json`, with the same names
as the xBrowserSync `settings.json`. Override any of them with an environment
variable, using `__` for nested keys (`Status__Message`, `Throttle__MaxRequests`),
either in the `environment:` block of `docker-compose.yml` or in your shell.

| Setting | Default | What it does |
|---|---|---|
| `AllowedOrigins` | `[]` | Allowed `Origin` values (exact strings), empty = any; as env: `AllowedOrigins__0`, `AllowedOrigins__1`, … |
| `DailyNewSyncsLimit` | `3` | New syncs per IP per UTC day, `0` = no limit |
| `Db__Host` / `Db__Port` / `Db__Name` | `127.0.0.1` / `27017` / `xbrowsersync` | MongoDB location (`mongo` in Docker) |
| `Db__Username` / `Db__Password` | empty | Empty = use `XBROWSERSYNC_DB_USER` / `XBROWSERSYNC_DB_PWD` |
| `Db__AuthSource` | `admin` | Database that holds the user |
| `Db__ConnTimeout` | `30000` | Connection timeout, ms |
| `Db__UseSrv` / `Db__Ssl` | `false` | `mongodb+srv://` URL / TLS to MongoDB |
| `Location` | empty | Two-letter country code shown in `/info` |
| `Log__Stdout__Enabled` / `Log__Stdout__Level` | `true` / `info` | Console log |
| `Log__File__Enabled` / `Log__File__Level` | `true` / `info` | File log |
| `Log__File__Path` | `/var/log/xBrowserSync/api.log` | Log file (`/app/logs/api.log` in Docker) |
| `Log__File__RotationPeriod` / `Log__File__RotatedFilesToKeep` | `1d` / `5` | Log rotation |
| `MaxSyncs` | `5242` | Total syncs before new ones are refused, `0` = unlimited |
| `MaxSyncSize` | `1048576` | Max sync size in bytes (1 MB) |
| `Server__Port` | `8080` | Listening port (all interfaces) |
| `Server__RelativePath` | `/` | Base path when served under a sub-path (e.g. `/api`) |
| `Server__Host` | `127.0.0.1` | Informational only, not a bind address: restrict exposure with a firewall or proxy |
| `Server__BehindProxy` | `false` | Trust `X-Forwarded-For`; enable **only** behind your own proxy |
| `Server__Https__Enabled` / `CertPath` / `KeyPath` | `false` | Serve HTTPS directly with PEM cert and key |
| `Status__Online` | `true` | `false` = service reported offline, requests refused |
| `Status__AllowNewSyncs` | `true` | `false` = existing syncs work, new ones refused |
| `Status__Message` | empty | Message shown to users in `/info` (markdown) |
| `SyncExpiryDays` | `0` | Delete syncs not read or written for N days, `0` = never ([details](docs/DEVELOPMENT.md#inactive-sync-expiry)) |
| `Throttle__MaxRequests` / `Throttle__TimeWindow` | `1000` / `300000` | Requests per IP per window (ms), `0` = off |

Only the log level changes without a restart (edit `appsettings.Local.json`);
everything else applies at startup.

## Already running xBrowserSync?

Switching takes about ten minutes with no data conversion: Keepmark Server
uses your xBrowserSync MongoDB database as it is, your users change nothing,
and going back means starting Node again. In short:

```sh
docker build -t keepmark-server .
sudo systemctl stop xbrowsersync
docker run -d --name keepmark-server --restart unless-stopped --network host \
  -e Db__Host=127.0.0.1 -e XBROWSERSYNC_DB_USER=xbrowsersyncdb \
  -e XBROWSERSYNC_DB_PWD='your-db-password' -e Log__File__Enabled=false \
  keepmark-server
```

Read the **[migration guide](docs/migration.md)** before switching: backup,
settings mapping, the optional sync expiry, checks and rollback.

## The Keepmark family

| | |
|---|---|
| **Keepmark Server** | This service (ASP.NET Core on .NET 10, MongoDB) |
| **Keepmark for browsers** | Extension for Firefox and Chromium-based browsers (Chrome, Edge, Brave) |
| **Keepmark for Android** | Android 10+ app with search, share-to-Keepmark and app lock |

Project home: <https://github.com/Keepmark>.

## Documentation

- [Migration guide](docs/migration.md): move from xBrowserSync API, step by step.
- [Operator guide](docs/operators.md): why switch, FAQ.
- [Developer and operations guide](docs/DEVELOPMENT.md): layout, API
  contracts, configuration, Docker, logging, backups, testing, security notes.
- [Verification spec](docs/verification-spec.md): behavioural requirements
  for code review.
- [Contributing](CONTRIBUTING.md).

## Feedback

Questions, bug reports and ideas: [open an issue](../../issues).

## License

Copyright (C) 2026 Yari Melzani. Free software: you can redistribute it
and/or modify it under the terms of the **GNU Affero General Public License,
version 3 only** (AGPL-3.0-only) — see [`LICENSE.md`](LICENSE.md).
Distributed without any warranty. If you run a modified version as a network
service, you must offer its source code to the users of that service
(AGPL §13).

Contributions require signing the [CLA](CLA.md) — see
[`CONTRIBUTING.md`](CONTRIBUTING.md).

Independent implementation, written from scratch; compatible with the
xBrowserSync protocol. Not affiliated with or endorsed by xBrowserSync.
