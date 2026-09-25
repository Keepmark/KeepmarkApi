# KeepmarkApi — .NET 10 sync service (xBrowserSync-compatible)

ASP.NET Core port of [`../api`](../api) (Node + Express + Mongoose), extended with
an opt-in **v1.2** contract. Same Mongo layout and byte-identical v1.1 shapes —
existing clients (`../app`, `../Keepmark`, `../KeepmarkApp`, official clients)
work unchanged. Independent reimplementation, not affiliated with xBrowserSync.

## Status

- Contract: v1.1.x frozen (Node parity) + v1.2 extended (opt-in, see below).
- Tests: 144 xUnit (no infra) + `scripts/e2e-v1.sh` (26 checks) +
  `scripts/e2e-v12.sh` (19 checks) against live Mongo, all green.
- Verified compat matrix: new⇄old clients/servers, concurrent-push chaos —
  see [Testing](#testing).
- **Run a public service?** Start with [`docs/operators.md`](docs/operators.md):
  why switch, 10-minute zero-impact migration, FAQ.

## Layout

- `src/KeepmarkApi` (net10.0, `Microsoft.NET.Sdk.Web`) — hosting + adapters:
  - `Program.cs` — Kestrel (`MaxSyncSize` cap), CORS, per-IP fixed-window throttle
    (+ `Retry-After`), `ForwardedHeaders` iff `BehindProxy`, HSTS iff direct HTTPS,
    security + `no-store` headers, hot verbosity gate, redacted settings dump,
    Mongo index bootstrap.
  - `Controllers/` — `BookmarksController` (CRUD + `HEAD`, conditional `GET`,
    contract shaping legacy/extended), `InfoController` (`/info` + v1.2
    `capabilities`), `RootController` (landing page with logo, status, credits).
  - `Data/` — `MongoContext` (connection builder, `XBROWSERSYNC_DB_*` fallback,
    TTL + `ipAddress` indexes) + `MongoStores` (timed operations).
  - `Middleware/` — `ErrorHandlerMiddleware` (`XbsException` → status,
    body-too-large → 413, routing 404/405 → 404 shape, rest → 500),
    `OriginGuardMiddleware` (exact-string origins only).
  - `Models/` — `BookmarkDocument` (`_id` = BSON Binary subtype 4, 16 RFC 4122
    bytes — never `Guid`), `NewSyncLogDocument`.
  - `Logging/` — `LogLevelWatcher` (on-the-fly level switch, no restart).
  - `wwwroot/` — `logo.png`, `favicon.png` (from `../Keepmark/brand`).
- `src/KeepmarkApi.Core` (net10.0, runtime-free — no ASP.NET Core reference):
  `Config` (settings + versions), `Errors` (+ `ErrorMap`), `Uuid/SyncId`,
  `Routing/ApiVersioning`, `Text/HtmlSanitizer`, `Time/SyncTime`, `Stores`
  (contracts), `Content/BlobMeta`, `Logic` (rules), `Requests` (body validation),
  `Shaping/ResponseShapes` (legacy freeze), `Logging` (levels, gate, sanitizer,
  rotating file sink).
- `tests/KeepmarkApi.Tests` (xUnit) — in-memory fakes, full rule matrix.
- `scripts/` — `e2e-v1.sh`, `e2e-v12.sh` (curl contract checks),
  `mongo-backup.sh`.
- `mongo-init/01-create-user.sh` — least-privilege DB user on first init.

## API contracts

Negotiation via the `Accept-Version` header (missing → server version).
Error shape is always `{code, message}` with the Node status codes.

| Call | v1.1 | v1.2 (`^1.2.0`) |
|---|---|---|
| `GET /info` | `{location, maxSyncSize, message, status, version}` | + `capabilities {revision, conditionalGet, delete}` |
| `POST /bookmarks` | `~1.0.0`: `{bookmarks}` · `^1.1.3`: `{version}` | same, + `meta` in response |
| `GET /bookmarks/:id` | `{bookmarks, version, lastUpdated}` | + `meta`; honors `If-None-Match` → `304` (ignored outside v1.2) |
| `PUT /bookmarks/:id` | v1: blind write · v2: `lastUpdated` base check → `409` | + `baseRevision` (atomic CAS on revision, wins over timestamp) |
| `GET /:id/lastUpdated`, `GET /:id/version` | as Node | unchanged |
| `HEAD /bookmarks/:id` | `412` | `ETag`, `Last-Modified`, `X-Sync-Revision`, `X-Sync-Size` |
| `DELETE /bookmarks/:id` | `412` | `200`, sync removed |
| `GET /` | landing page (logo, live status, credits) | same |

`meta = {revision, hash, size, createdAt}`. `revision` starts at 1 and bumps on
**every** write under any contract; `hash` = SHA-256 of the ciphertext (opaque
to the server); old syncs report `revision: 0` with no hash until their next
write (no backfill, by design). The server reports `1.2.0` in `/info.version`;
legacy clients only enforce the minimum (`1.1.9`), so nothing breaks.

### Sync model and guarantees

- Whole-blob optimistic concurrency. v1 `PUT` has no base check (silent
  last-writer-wins, Node quirk, preserved). v2 checks are **atomic**
  (`findOneAndUpdate` filtered on `_id` + base): concurrent same-base pushes
  resolve to exactly one `200` + one `409` — verified live (`[200,409]` vs
  Node's `[200,200]`).
- Timestamps are millisecond-precision (`toISOString` format). The server
  truncates writes to ms and compares instants, not strings, so equivalent
  spellings (`.87` vs `.870`) agree; unparseable bases conflict.
- `GET` (all forms) refreshes `lastAccessed` (TTL source). Missing sync →
  `401 SyncNotFoundException` (Node convention, kept). Malformed ID → `401
  InvalidSyncIdException`.
- The server never sees plaintext and cannot merge: conflict resolution is
  always client-side (pull, merge locally, push with fresh base).

## Configuration

`src/KeepmarkApi/appsettings.json` mirrors `api/config/settings.default.json`
key-for-key. Precedence: `appsettings.json` ← `appsettings.Local.json`
(hot-reloads `Log.Stdout.Level` only — other keys apply at startup) ←
environment (`__` nests, e.g. `Db__Host=mongo`, `Log__Stdout__Level=debug`).
Empty `Db.Username/Password` fall back to `XBROWSERSYNC_DB_USER` /
`XBROWSERSYNC_DB_PWD`. Never put secrets in `appsettings.Local.json`: it is a
tracked `{}` placeholder — credentials belong in `.env` (chmod 600).

| Key | Default | Notes |
|---|---|---|
| `AllowedOrigins` | `[]` (all) | exact strings only (no `RegExp`) |
| `DailyNewSyncsLimit` | `3` | per IP/day, `0` disables |
| `Db.*` | `127.0.0.1:27017/xbrowsersync`, `authSource=admin` | `UseSrv` for `mongodb+srv://` |
| `Location` | `""` | ISO country code, uppercased in `/info` |
| `Log.Stdout.Level` / `Log.File.*` | `info` / file on | `trace/debug/info/warn/error/fatal` |
| `MaxSyncs` / `MaxSyncSize` | `5242` / `1048576` | `0` = unlimited syncs / 1 MB body cap (official-service parity) |
| `Server.Host/Port/RelativePath` | `127.0.0.1:8080/` | Kestrel binds `AnyIP:Port` |
| `Server.BehindProxy` | `false` | trust `X-Forwarded-For` **only** behind a proxy |
| `Server.Https.*` | disabled | direct HTTPS + HSTS; else terminate at a proxy |
| `Status.*` | online, new syncs ok | `message` supports markdown, scripts stripped |
| `Throttle.*` | `1000` / 5 min | per IP, `0` disables, `Retry-After` on 429 |

## Run

```sh
# Native (needs the ASP.NET Core runtime + MongoDB):
dotnet run --project src/KeepmarkApi
# Self-contained:
dotnet publish src/KeepmarkApi -r linux-x64 --self-contained
```

MongoDB needs the `xbrowsersyncdb` user (`readWrite` on the app DB);
indexes are auto-created at startup. Native DB user setup:

```sh
mongosh --eval "db.getSiblingDB('admin').createUser({user:'xbrowsersyncdb',pwd:'<pwd>',roles:[{role:'readWrite',db:'xbrowsersync'},{role:'readWrite',db:'xbrowsersynctest'}]})"
```

## Docker

```sh
cp .env.example .env   # set real passwords (chmod 600 .env), set API_PORT if 8080 is taken
# One-time volume ownership (named volumes start root-owned):
docker run --rm -v keepmarkapi_api-logs:/data alpine chown 1654:1654 /data
docker run --rm -v keepmarkapi_mongo-dumps:/data alpine chown 999:999 /data
docker compose up --build -d
curl localhost:${API_PORT:-8080}/info
```

Health: API `HEALTHCHECK` hits `/info`, mongo pings itself; the API waits for
a healthy mongo (`depends_on: service_healthy`). Container stdout is capped
(`max-size 10m`, 3 files per service).

Notes: the API listens on 8080 in-container (mapped via `API_PORT`, default
`8080` — here `8090` to avoid a host clash). Home-directory encryption on some
hosts breaks container **writes** on bind mounts, hence named volumes for logs
and dumps. `appsettings.Local.json` is mounted for hot edits (log level).

## Logging & verbosity

Levels mirror bunyan. `info` (default): request summaries, creations, refusals,
conflicts, 5xx. `debug`/`trace`: every decision with values, per-request
details (IP, version header, byte sizes), per-Mongo-op timings, settings dump.
Bodies, keys and full sync IDs are never logged (IDs truncated `ab12…ef`; DB
password excluded from the dump).

Change statically (`Log__Stdout__Level=debug` / `LOG_LEVEL` in Docker) or **on
the fly**: edit the watched `appsettings.Local.json`
(`{"Log":{"Stdout":{"Level":"trace"}}}`) — applies in seconds, the change is
logged. Only the log level is hot; throttle and the rest need a restart.

## Operations

### File logs

`log.file` settings honored: path, level, `rotationPeriod` (`1d`, `12h`,
`hourly`…, Node format), `rotatedFilesToKeep` (`api.log` → `.0` → `.1`…).
In Docker: `/app/logs/api.log` (`api-logs` volume); read with
`docker compose exec api cat /app/logs/api.log`. Unwritable path → sink
disables itself with a warning.

### Mongo backup

```sh
./scripts/mongo-backup.sh   # mongodump of xbrowsersync into mongo-dumps, keeps 7
```

Cron: `0 3 * * * cd /path/to/KeepmarkApi && ./scripts/mongo-backup.sh`.
List: `docker compose exec mongo ls /dump`.
Fetch: `docker compose cp mongo:/dump/<stamp> ./restores/`.
Restore (indexes are recreated by the API at startup):

```sh
docker compose exec -T mongo sh -c 'mongorestore -u "$MONGO_INITDB_ROOT_USERNAME" -p "$MONGO_INITDB_ROOT_PASSWORD" --authenticationDatabase admin --drop --db xbrowsersync /dump/<stamp>/xbrowsersync'
```

Set `INCLUDE_TEST_DB=1` to also dump `xbrowsersynctest` (skipped by default).
DB passwords apply at **first init only** (`mongo-init/` runs on empty volumes):
rotating them later needs `db.updateUser()` in mongosh, then a restart.

### TLS

1. **Proxy (recommended):** terminate at Caddy/nginx/Traefik, set
   `Server__BehindProxy=true` so IPs are real. HSTS is the proxy's job.
2. **Direct:** mount cert+key, set `Server__Https__*`; the API emits HSTS itself.

### Rate limits

Per-IP fixed window, `429 {code:RequestThrottledException}` + `Retry-After`
seconds. Tune `throttle.*`; `0` disables (restart to apply).

## Testing

```sh
dotnet test                                # 144 xUnit, no infra
BASE=http://localhost:8090 ./scripts/e2e-v1.sh    # 26 contract checks, live
BASE=http://localhost:8090 ./scripts/e2e-v12.sh   # 19 v1.2 checks, live
```

`Core` is runtime-free, so the whole rule matrix (quotas, limits, CAS win/lose,
revision lifecycle, legacy shape freeze, levels, redaction) runs against
in-memory fakes. Live-only: real-concurrency races and HTTP middleware — covered
by the scripts plus the chaos harness (`rev-race` → `[200,409]`).

Verified compat matrix:

| | Node 1.1.13 server | KeepmarkApi 1.2.0 |
|---|---|---|
| v1.1 contract (24) | 24/24 | 24/24 |
| .NET client live (6) | 6/6 (legacy fallback) | 6/6 (v1.2 negotiated) |
| v1.2 contract (19) | n/a | 19/19 |
| same-base race | `[200,200]` (upstream bug) | `[200,409]` |

## Security & anonymity notes

- Sync IDs are bearer tokens: truncated in logs, never in error details beyond
  the code/message contract.
- No auth by design (capability URLs); brute force is infeasible (122-bit IDs)
  but IDs must stay secret — URLs leak into proxies/history.
- Passphrase strength is entirely client-side; the server cannot help and
  must stay zero-knowledge (no verifiers stored).
- `OriginNotPermitted` (500) only when `AllowedOrigins` is non-empty and the
  `Origin` mismatches; CORS `AllowAny*` when empty (Node parity).
- Dependencies: `MongoDB.Driver` only (+ test SDK). No Node, no Mongoose.

## Known limitations (deliberate)

- `AllowedOrigins`: exact strings only (Node also accepts `RegExp`).
- `Location` validated via `RegionInfo`, not `country-list`.
- v1 `PUT` has no conflict detection (upstream quirk, frozen).
- No per-sync quota accounting beyond the global body cap.
