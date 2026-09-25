# KeepmarkApi — .NET 10 rewrite of the xBrowserSync API (v1.1.13 parity)

ASP.NET Core port of `../api` (Node + Express + Mongoose). Same wire contract,
same error shape `{code,message}`, same Mongo layout — old `app/` clients and
`Keepmark` / `KeepmarkApp` keep working unchanged. **No v2 extensions here**
(no `revision`/`hash`/crypto-suite): parity first.

## Layout

- `src/KeepmarkApi` (net10.0, `Microsoft.NET.Sdk.Web`)
  - `Program.cs` — hosting: Kestrel limits (`MaxSyncSize`), CORS, per-IP fixed-window
    throttle, `ForwardedHeaders` iff `BehindProxy`, security + `no-store` headers,
    `Accept-Version` routing, error mapping, redacted request log, Mongo index bootstrap.
  - `Controllers/` — `BookmarksController` (`POST|GET|PUT /bookmarks…`,
    `GET /:id/lastUpdated`, `GET /:id/version`), `InfoController` (`GET /info`),
    `RootController` (placeholder — Node serves the webpack docs site at `/`).
  - `Services/` — `BookmarksService`, `InfoService`, `NewSyncLogsService`
    (logic mirrors `bookmarks/info/newSyncLogs.service.ts`, quirks included:
    v1 update on missing sync returns `200 {}`).
  - `Config/` — `XbsSettings` (1:1 with `settings.default.json`) + `XbsVersions`.
  - `Data/` — `MongoContext` (connection string builder + `newsynclogs`
    `expiresAt` TTL and `ipAddress` indexes from `api/README.md`).
  - `Middleware/` — `ErrorHandlerMiddleware` (`XbsException` → status,
    413 → `SyncDataLimitExceeded`, rest → `Unspecified`), `OriginGuardMiddleware`.
  - `Models/` — `BookmarkDocument` (`_id` = BSON Binary subtype 4, 16 RFC 4122 bytes),
    `NewSyncLogDocument`.
  - `Logging/` — `LogSanitizer` (sync IDs truncated in logs; IDs are bearer tokens).
- `src/KeepmarkApi.Core` (net10.0, runtime-free) — `Errors`, `Uuid/SyncId`,
  `Routing/ApiVersioning`, `Text/HtmlSanitizer`. No ASP.NET Core reference so unit
  tests run anywhere.
- `tests/KeepmarkApi.Tests` (xUnit, 18 tests, no Mongo) — SyncId, version routing,
  script-stripping.

## Config

`src/KeepmarkApi/appsettings.json` mirrors `api/config/settings.default.json`
(same keys). Local overrides: `appsettings.Local.json` (git-ignored) or env vars.
Empty `Db.Username/Password` fall back to `XBROWSERSYNC_DB_USER` / `XBROWSERSYNC_DB_PWD`.

## Run

This machine has the SDK but **no** `Microsoft.AspNetCore.App` shared runtime, so:

```sh
# dev machine with the ASP.NET Core runtime:
dotnet run --project src/KeepmarkApi
# here / deploy: self-contained (bundles the runtime)
dotnet publish src/KeepmarkApi -r linux-x64 --self-contained
```

Needs MongoDB with the `xbrowsersyncdb` user + indexes per `../api/README.md`
(indexes are also auto-created at startup). E2E against Mongo is still to be done.

## API contracts

- **v1.1.x** (`Accept-Version: 1.1.9`–`1.1.13`): frozen Node parity. Shapes are
  byte-identical to the original; the server reports `1.2.0` in `/info.version`
  but old clients only enforce the minimum, so nothing breaks.
- **v1.2** (`Accept-Version: ^1.2.0`, opt-in): same endpoints plus
  `meta {revision, hash, size, createdAt}` on create/get/update,
  `PUT` accepting `baseRevision` (atomic CAS on revision instead of timestamp),
  `HEAD /bookmarks/:id` (`ETag`, `Last-Modified`, `X-Sync-Revision`,
  `X-Sync-Size`), conditional `GET` via `If-None-Match` → `304`,
  `DELETE /bookmarks/:id`, and `capabilities` in `/info`.
  `revision` starts at 1 and bumps on every write (any contract);
  `hash` = SHA-256 of the ciphertext; old syncs show `revision: 0` until
  their next write (no backfill, by design).

## Logging & verbosity

Levels mirror bunyan (`trace|debug|info|warn|error|fatal`), configured at
`Log.Stdout.Level` (`Log.File.Level` is parsed but no file sink is wired yet).

- `info` (default): request summary lines (`METHOD path status ms`), creations,
  refusals, conflicts, 5xx errors.
- `debug`/`trace` (verbose): every decision with values (version routing,
  quota counts, limit checks, base vs current timestamps on conflict), per-request
  details (IP, `Accept-Version`, byte sizes, timing), per-Mongo-op timings,
  effective-settings dump at startup.
- Never logged: bookmark bodies, passwords/keys, full sync IDs (truncated
  `ab12…ef` — IDs are bearer tokens). The DB password is excluded from the dump.

Change it statically (`appsettings.json`, `appsettings.Local.json`, or env
`Log__Stdout__Level=debug`) or **on the fly without restart**: the verbosity
gate re-reads settings on reload, so editing the watched
`appsettings.Local.json` applies within seconds (the change itself is logged).

## Docker

```sh
cp .env.example .env   # set real passwords
touch appsettings.Local.json  # only if missing ({} is committed as placeholder)
# One-time volume ownership (named volumes are root-owned by default):
docker run --rm -v keepmarkapi_api-logs:/data alpine chown 1654:1654 /data
docker run --rm -v keepmarkapi_mongo-dumps:/data alpine chown 999:999 /data
docker compose up --build -d
curl localhost:8090/info
```

Verbosity in Docker, two ways:

1. Static: `LOG_LEVEL=debug` in `.env`, then `docker compose up -d` (recreate).
2. On the fly: edit the mounted `appsettings.Local.json` on the host, e.g.
   `{"Log":{"Stdout":{"Level":"trace"}}}`, wait a few seconds, watch
   `docker compose logs -f api`. Revert the file to go back. Only `Log`
   (and display-only `Status`) sections belong there; DB/auth come from env.

Mongo data persists in the `mongo-data` volume; the app user
(`xbrowsersyncdb`) is created once at first init by
`mongo-init/01-create-user.sh`. The API also creates the TTL (`expiresAt`) +
`ipAddress` indexes at startup.

## Tests

```sh
dotnet test   # 109 xUnit tests, no Mongo/Docker needed
```

`KeepmarkApi.Core` is runtime-free (logic, stores contracts, versioning, levels,
redaction) so the whole rule matrix runs anywhere against in-memory fakes;
only the thin Mongo adapters and middleware need a live server (e2e pending).

## Known limitations (parity gaps, deliberate)

- `AllowedOrigins` supports exact strings only (Node also accepts `RegExp`).
- `Location` validation uses `RegionInfo` instead of the `country-list` package.

## Operations

### File logs

`log.file` settings are honored: path, level, `rotationPeriod` (`1d`, `12h`,
`hourly`… — Node format) and `rotatedFilesToKeep` (`api.log` → `api.log.0` →
`api.log.1`…). The gate still applies, so on-the-fly level changes affect the
file too. In Docker the path is preset to `/app/logs/api.log` (named volume `api-logs`;
home-dir encryption blocks container writes on bind mounts here). Read with
`docker compose exec api cat /app/logs/api.log`. If the path is not writable
the sink disables itself with a warning.

### Mongo backup

```sh
./scripts/mongo-backup.sh   # mongodump of both DBs into the mongo-dumps volume, keeps 7
```

Cron: `0 3 * * * cd /path/to/KeepmarkApi && ./scripts/mongo-backup.sh`.
List: `docker compose exec mongo ls /dump`.
Fetch: `docker cp keepmarkapi-mongo-1:/dump/<stamp> ./restores/`.
Restore: `mongorestore` the dump dir into `xbrowsersync` with the app user.

### TLS

Two supported layouts (the API speaks plain HTTP inside Docker by default):

1. TLS proxy in front (recommended): terminate at Caddy/nginx/Traefik, set
   `Server.BehindProxy=true` (`Server__BehindProxy=true`) so client IPs come
   from `X-Forwarded-For` for throttle + abuse logs. HSTS is the proxy's job.
2. Direct HTTPS: mount cert+key and set `Server.Https.Enabled/CertPath/KeyPath`
   (`Server__Https__*`); the API then also emits HSTS itself.

### Rate-limit tuning

`throttle.maxRequests` per `throttle.timeWindow` (defaults `1000` / 5 min),
per client IP. Rejections are `429 {code:RequestThrottledException}` with a
`Retry-After` header (seconds to window reset). `0` disables. Note: throttle
values are read at startup (restart to apply); only the log level is hot.
