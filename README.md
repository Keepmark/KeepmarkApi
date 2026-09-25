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

## Tests

```sh
dotnet test
```

## Known limitations (parity gaps, deliberate)

- `Log.File` settings are parsed but no file sink is wired yet (console only).
- `AllowedOrigins` supports exact strings only (Node also accepts `RegExp`).
- `GET /` returns `{version,info}` JSON, not the docs site.
- `Location` validation uses `RegionInfo` instead of the `country-list` package.
