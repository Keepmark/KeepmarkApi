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
docker compose up --build -d
curl localhost:8080/info
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
dotnet test   # 93 xUnit tests, no Mongo/Docker needed
```

`KeepmarkApi.Core` is runtime-free (logic, stores contracts, versioning, levels,
redaction) so the whole rule matrix runs anywhere against in-memory fakes;
only the thin Mongo adapters and middleware need a live server (e2e pending).

## Known limitations (parity gaps, deliberate)

- `Log.File` settings are parsed but no file sink is wired yet (console only).
- `AllowedOrigins` supports exact strings only (Node also accepts `RegExp`).
- `GET /` returns `{version,info}` JSON, not the docs site.
- `Location` validation uses `RegionInfo` instead of the `country-list` package.
