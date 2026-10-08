# KeepmarkApi — Verification Specification (for external code review)

**Version:** 1.2.0 (server reports `1.2.0` in `/info.version`) · **Date:** 2026-09-26
**Purpose:** enable an independent party to verify, without prior context, that this
service is a correct, safe, drop-in replacement for the xBrowserSync API v1.1.13
plus the documented opt-in v1.2 extensions — and nothing more.
**Reference implementation:** `../api` (Node + Express + Mongoose, package version 1.1.13).
**Repo under review:** `KeepmarkApi/` (`KeepmarkApi.slnx`: `src/KeepmarkApi`,
`src/KeepmarkApi.Core`, `tests/KeepmarkApi.Tests`).

> How to use this document: each numbered requirement (`REQ-…`) is independently
> checkable. §12 turns them into an acceptance checklist. Anything the code does
> beyond these requirements is a finding (scope creep); anything missing is a defect.

---

## 1. Scope and non-goals

**In scope**

- HTTP contract parity with the Node API for every v1.1 route, status code and
  `{code,message}` body (§3–§4), verified by unit tests + live scripts (§11).
- The opt-in v1.2 contract (§6): metadata envelope, revision CAS, conditional
  GET, HEAD, DELETE, capabilities. Legacy shapes must be byte-identical when the
  extended contract is not negotiated.
- Persistence layout compatibility (same collections, same `_id` encoding, §7).
- Cross-cutting behaviour: config, logging/redaction, throttling, CORS/origins,
  security headers, startup/exit rules (§8–§10).

**Non-goals (must NOT exist)**

- No plaintext handling or merge logic: the server stores opaque ciphertext and
  never decrypts, re-encrypts, validates plaintext, or stores password verifiers.
- No auth, accounts, sessions, or per-user quotas beyond the documented global
  caps (`MaxSyncs`, `MaxSyncSize`, daily new-sync limit, throttle).
- No telemetry, no outbound calls, no background jobs except log-level watching
  and Mongo TTL expiry.
- No data migration: old documents are read as-is, never backfilled (§6.2).

---

## 2. Global conventions (apply to every endpoint)

- **REQ-G1 — Error shape.** Every error response is JSON `{code, message}` with
  `Content-Type: application/json`. `code` is the exception class name
  (`XbsExceptions.cs`), e.g. `{"code":"SyncNotFoundException","message":"Sync does not exist"}`.
- **REQ-G2 — Status table.** Exact mapping (no deviations permitted):

| Exception | HTTP | `code` |
|---|---|---|
| `InvalidSyncIdException` | 401 | `InvalidSyncIdException` |
| `SyncNotFoundException` | 401 | `SyncNotFoundException` |
| `RequiredDataNotFoundException` | 400 | `RequiredDataNotFoundException` |
| `NewSyncsForbiddenException` | 405 | `NewSyncsForbiddenException` |
| `NewSyncsLimitExceededException` | 406 | `NewSyncsLimitExceededException` |
| `RouteNotImplementedException` (wire `NotImplementedException`) | 404 | `NotImplementedException` |
| `SyncConflictException` | 409 | `SyncConflictException` |
| `UnsupportedVersionException` | 412 | `UnsupportedVersionException` |
| `SyncDataLimitExceededException` | 413 | `SyncDataLimitExceededException` |
| `RequestThrottledException` | 429 | `RequestThrottledException` |
| `OriginNotPermittedException` | 500 | `OriginNotPermittedException` |
| `UnspecifiedException` (incl. malformed JSON) | 500 | `UnspecifiedException` |
| `ServiceNotAvailableException` | 503 | `ServiceNotAvailableException` |

- **REQ-G3 — Timestamps.** All instants on the wire are UTC ISO-8601 with exactly
  three fractional digits (`yyyy-MM-ddTHH:mm:ss.fffZ`, e.g. `2026-09-25T12:00:00.000Z`),
  enforced by `IsoDateTimeConverter` for every `DateTime`. Whole-second values keep
  `.000` so echoes match Node's strict string comparison.
- **REQ-G4 — Timestamp precision.** BSON stores ms precision. The server truncates
  every write instant to whole milliseconds (`SyncTime.TruncateToMs`) and compares
  parsed instants at ms precision — never raw strings. Equivalent spellings
  (`.87` vs `.870`) MUST agree; unparseable bases MUST be treated as mismatch (§5.3).
  Parsing is ISO-8601 only and culture-invariant (`TryParseExact`): loose local
  formats (`09/25/2026 12:00:00`) never match.
- **REQ-G5 — Sync IDs.** Wire form: 32 lowercase hex chars; dashed UUID form
  accepted and stripped (`SyncId.Parse`). Any other input → `401
  InvalidSyncIdException` with message `Argument is not a valid UUID string`.
  Stored form: the 16 UUID bytes in RFC 4122 (network) order as BSON Binary
  subtype 4 (`UuidStandard`). `Guid.ToByteArray()` (mixed-endian) is forbidden.
  IDs are bearer tokens (capability URLs): see redaction §9.3.
- **REQ-G6 — ID validation order.** On read/update/delete paths the ID is parsed
  BEFORE the service-availability check (mirrors Node router order). An invalid ID
  returns 401 even when the service is offline.
- **REQ-G7 — Unknown routes/methods.** Any unmatched path AND any matched-route /
  wrong-method (ASP.NET would answer 405, e.g. `GET /bookmarks`) MUST be normalized
  to `404 {code:NotImplementedException,…}` — but only when nothing was written yet
  (explicit bodies such as `405 NewSyncsForbidden` keep their own response).
- **REQ-G8 — Malformed JSON.** A syntactically invalid body MUST return
  `500 {code:UnspecifiedException,…}` (Node behaviour), never 400. Bodies are
  read like `express.json()` (`BookmarkRequests.ParseBody`): a missing or
  non-JSON `Content-Type`, or an empty body, is `{}` (→ `400
  RequiredDataNotFoundException`), never a bare `415`/empty response.
- **REQ-G9 — Null omission.** DTOs omit null-valued keys (`WhenWritingNull`), so
  legacy shapes contain exactly the keys Node sends (§4).

---

## 3. Version negotiation

The client sends `Accept-Version`; the server never negotiates content types.

- **REQ-V1 — Naive dispatch (quirk, intentional).** `POST|PUT /bookmarks` route by
  the MINOR version only: `1.0.x` → v1 handlers, any other `1.x` (including
  `1.1.0`–`1.1.2`) → v2 handlers; any non-`1` major → `412
  UnsupportedVersionException`. This mirrors `express-routes-versioning`, which
  compares only the major for `^` ranges. A SemVer-strict implementation (412 on
  `1.1.2`) is a DEFECT here.
- **REQ-V2 — Read gates.** `GET /bookmarks/:id`, `GET …/lastUpdated`,
  `GET …/version` accept any `1.x`, else 412. (`GET …/version` served on `1.0.x`
  too — same naive-match reason.)
- **REQ-V3 — Extended contract.** `IsExtendedContract` ⇔ major == 1 AND minor >= 2
  (`^1.2.0`). Only then: `meta`/`capabilities` keys, `baseRevision` honoured,
  `If-None-Match` honoured, `HEAD`/`DELETE` served. Anything older gets the
  byte-identical v1.1.x shapes (`ResponseShapes.Strip`).
- **REQ-V4 — Missing header.** No `Accept-Version` header → behave as `1.1.13`
  (Node defaulted to its package version). The server MUST NOT default to the
  extended contract. Header parsing tolerates a leading `v` (`v1.2.0`) and
  non-numeric suffixes (numeric prefix kept, rest → 0).
- **REQ-V5 — `HEAD`/`DELETE` on legacy.** Requested without the extended contract
  → `404 {code:NotImplementedException}` (Node's catch-all answer for unknown
  routes), NOT 412.
- **REQ-V6 — `PUT` v1 route ignores new fields.** On the v1 route (`1.0.x`),
  `lastUpdated`/`version`/`baseRevision` in the body MUST be ignored (blind write).

---

## 4. Endpoint specifications

Notation: `L:` = legacy shape (header-less or `Accept-Version: 1.1.13`);
`X:` = extended (`Accept-Version: ^1.2.0`). All success bodies are `200`
except `304` (§6.4) and `204` (favicon missing).

### 4.1 `GET /info`

- **REQ-I1 — Shape.** `L: {location, maxSyncSize, message, status, version}`.
  `X:` adds `capabilities: {revision:true, conditionalGet:true, delete:true}`.
- **REQ-I2 — Fields.** `location`: config value uppercased, or `""` (key present,
  empty string — never omitted, never null). `maxSyncSize`: configured value.
  `message`: status message with `<script>…</script>` stripped (and ONLY that —
  see §10.3). `version`: always `1.2.0` (safe: clients enforce only the minimum
  `1.1.9`).
- **REQ-I3 — Status mapping.** `status`: `1` Online / `2` Offline / `3` NoNewSyncs.
  Start `2`; if `Status.Online` is false → stay `2`; else count syncs: accepting
  (§5.1) → `1`, else `3`. A failing count keeps `2` AND logs a Warning
  (operators must see a likely-DB-down signal; Node silently stays offline).
  With `MaxSyncs = 0` the acceptance check needs no count, so `/info` still
  performs one store round trip (`ProbeStoreAsync`) to detect a dead DB.

### 4.2 `POST /bookmarks`

- **REQ-C1 — v1 route (`1.0.x`).** Required: non-empty `bookmarks` (scalar-coerced,
  §4.8). Creates `{bookmarks, lastAccessed=now, lastUpdated=now, version:null,
  revision:1, hash, size, createdAt:now}`. Returns `L: {id (32 hex), lastUpdated}`;
  `X:` impossible on this route (1.0.x never extended) but shaping rule still applies.
- **REQ-C2 — v2 route (any other `1.x`).** Required: non-empty `version`. Creates
  `{bookmarks:null, …, version, revision:1, hash=sha256(""), size:0,
  createdAt:now}`. Returns `L: {id, lastUpdated, version}`; `X:` adds `meta`.
- **REQ-C3 — Guard order (both).** Availability → accepting-new-syncs (405) →
  daily-limit (406) → insert → write sync-log entry. Missing client IP skips
  limit tracking entirely (no refusal, no log row).
- **REQ-C4 — Missing/empty required field** → `400 RequiredDataNotFoundException`.

### 4.3 `GET /bookmarks/:id`

- **REQ-R1.** Touch `lastAccessed=now` (single atomic `findOneAndUpdate`), return
  the post-touch document. `L: {bookmarks, version, lastUpdated}`;
  `X:` adds `meta`. Missing → `401 SyncNotFoundException`.
- **REQ-R2 — Conditional (X only).** If `If-None-Match` present: read metadata
  WITHOUT touching; if stored `hash` non-null and equals the tag after stripping
  surrounding `"` (case-insensitive) → `304` with no body. Otherwise fall through
  to the normal touching read (exactly one touch total).

### 4.4 `PUT /bookmarks/:id`

- **REQ-U1 — v1 route (`1.0.x`).** Blind write: no existence check, no base
  comparison. Missing sync → `200 {}` (empty object, NO `lastUpdated` key — Node
  quirk, frozen). Present → update data + `lastUpdated/lastAccessed=now`,
  revision bump, refreshed hash/size; `version` field untouched (v1 has no version
  input). Returns `L: {lastUpdated}`.
- **REQ-U2 — v2 no-base.** No `lastUpdated` AND no `baseRevision` → check
  existence (missing → `401 SyncNotFoundException`), then blind update.
  `version` replaced only when the new value is non-empty; empty/absent preserves
  the stored version.
- **REQ-U3 — v2 timestamp base.** `lastUpdated` present, no `baseRevision`:
  unparseable → `409 SyncConflictException`. Else atomic CAS on
  (`_id`, `lastUpdated`==base, ms precision): exactly one same-base winner gets
  `200`; losers get `409`; missing → `401`.
- **REQ-U4 — v2 revision base (X only).** `baseRevision` present (number or numeric
  string; other types ignored → treated as absent) takes precedence over
  `lastUpdated` when both sent. Atomic CAS on (`_id`, `revision`==base). Win → `200`
  with new `meta`; loss → `409`; missing → `401`.
- **REQ-U5 — Write payload (all paths).** Every successful write sets
  `bookmarks, lastAccessed=now, lastUpdated=now`, increments `revision` by 1,
  refreshes `hash=SHA256-hex(UTF-8 blob)` and `size=UTF-8 byte count`, preserves
  `version` unless a non-empty replacement was sent. `createdAt` never changes.
  Write instants are ms-truncated BEFORE persistence.

### 4.5 `GET /bookmarks/:id/lastUpdated` · `GET /bookmarks/:id/version`

- **REQ-M1.** Both touch `lastAccessed` (like all Node reads). Return
  `{lastUpdated}` / `{version}` (version key omitted when null). Missing →
  `401 SyncNotFoundException`. Gate: any `1.x`, else 412.

### 4.6 `HEAD /bookmarks/:id` (X only)

- **REQ-H1.** Read-only: MUST NOT touch `lastAccessed` (metadata polling neither
  extends TTL nor costs a write). Operator note: with the optional
  `bookmarks.lastAccessed` TTL index (`api/README.md`, 3 weeks), a client that
  polled ONLY via `HEAD`/`304` would let its sync expire; Keepmark clients also
  call `GET …/lastUpdated` (touching) on every poll. Headers: `ETag: "<hash>"` (only when hash
  non-null), `Last-Modified` (`.000` format), `X-Sync-Revision`, `X-Sync-Size`.
  `200` with empty body. Missing → `401`. Legacy → 404 (§REQ-V5).

### 4.7 `DELETE /bookmarks/:id` (X only)

- **REQ-D1.** Delete by `_id`; deleted → `200 {}`; missing → `401
  SyncNotFoundException`. Legacy → 404. Logged at Info with truncated ID.

### 4.8 Body validation (all POST/PUT)

- **REQ-B1.** Required strings: missing, empty, or non-scalar (object/array/null)
  → `400 RequiredDataNotFoundException`. Scalar coercion mirrors Mongoose:
  numbers → their JSON text (`42` → `"42"`), `true`/`false` → `"true"`/`"false"`.
- **REQ-B2.** Optional `lastUpdated`/`version`: same coercion; objects/arrays/null
  count as absent. Unknown keys ignored (no mass assignment — the update sets
  exactly `bookmarks/lastAccessed/lastUpdated/revision/hash/size` + optional
  `version`).

### 4.9 `/` and `/favicon.ico`

- **REQ-F1.** `GET /` returns the static landing page (`text/html`) with `{version}`
  replaced by `1.2.0`; it fetches `info` client-side for the status pill.
- **REQ-F2.** `GET /favicon.ico` serves `wwwroot/favicon.png` as `image/png`, or
  `204 No Content` when absent (never 404-HTML, never throws when `WebRootPath`
  is unset).

---

## 5. Quotas, limits, availability

- **REQ-Q1 — New-sync acceptance.** Refuse with `405 NewSyncsForbiddenException`
  when `Status.AllowNewSyncs == false` OR (`MaxSyncs > 0` AND count >= `MaxSyncs`).
  `MaxSyncs == 0` means unlimited. Count uses `EstimatedDocumentCount`.
- **REQ-Q2 — Daily per-IP limit.** When `DailyNewSyncsLimit > 0` and client IP
  present: `countByIp >= limit` → `406 NewSyncsLimitExceededException`. Limit `0`
  disables BOTH the check and the log writes. Missing IP → never refused, nothing
  logged. `expiresAt` = start of tomorrow UTC (`now.Date.AddDays(1)`); Mongo TTL
  (`expireAfter: 0`) deletes rows at that instant, so the window is the calendar
  day, not a rolling 24 h.
- **REQ-Q3 — Body cap.** Kestrel `MaxRequestBodySize = EffectiveMaxSyncSize`
  (`MaxSyncSize` when > 0, else `1048576`). Over-limit bodies → `413
  SyncDataLimitExceededException` via the `BadHttpRequestException` mapping.
  Default `MaxSyncSize = 1048576` (1 MB, official-service parity — deliberately
  above Node's 512 kB default; operators may raise/lower it).
- **REQ-Q4 — Offline mode.** `Status.Online == false` → every bookmark operation
  throws `503 ServiceNotAvailableException`. (`/info` still answers, reporting
  status 2.)
- **REQ-Q5 — Disk model.** Nothing is preallocated: `MaxSyncSize` is a validation
  ceiling, Mongo grows per document. Worst-case budget = `MaxSyncs × MaxSyncSize`.

---

## 6. v1.2 semantics (extended contract only)

- **REQ-X1 — Envelope.** `meta = {revision, hash, size, createdAt}` on create/get/
  update responses. Legacy shaping MUST remove the entire `meta` key (and
  `capabilities`), not null it.
- **REQ-X2 — Revision lifecycle.** Starts at 1 on create; +1 on EVERY write under
  ANY contract (including v1 blind writes). Syncs written by older servers report
  `revision: 0` with no `hash`/`size: 0`/`createdAt: null` until their next write.
  No backfill, by design.
- **REQ-X3 — Hash/size.** `hash` = lowercase hex SHA-256 over the UTF-8 bytes of
  the stored ciphertext blob (`SHA256("")` for version-only creates);
  `size` = UTF-8 byte count of the blob. The blob stays opaque to the server.
- **REQ-X4 — Conditional GET.** See REQ-R2. `If-None-Match` is IGNORED outside
  the extended contract (full read, no 304).
- **REQ-X5 — `baseRevision` ignored outside X.** A `baseRevision` field sent with
  a legacy version MUST be ignored (timestamp path or no-base path applies).

---

## 7. Persistence

- **REQ-P1 — Collections.** `bookmarks` and `newsynclogs`, same names as Node.
  `BookmarkDocument` fields: `_id, bookmarks, lastAccessed, lastUpdated, version,
  revision (default 0), hash, size (default 0), createdAt`. Extra stored fields
  tolerated (`BsonIgnoreExtraElements`).
- **REQ-P2 — `_id`.** BSON Binary subtype `0x04` holding the 16 RFC 4122 bytes;
  all by-ID filters pin the subtype. Declared as `BsonBinaryData`, never `Guid`.
- **REQ-P3 — Indexes (created at every startup).** `newsynclogs.expiresAt` TTL
  (`expireAfter: Zero`); single `ipAddress`; compound `(ipAddress, expiresAt)`
  keeping the per-create count an index-only scan.
- **REQ-P4 — Connection.** URL-escaped `user:password@`, `authSource` appended when
  non-empty; `UseSrv` → `mongodb+srv://` + TLS; `Ssl` → TLS; `ConnTimeout` honoured.
  Empty username/password fall back to `XBROWSERSYNC_DB_USER` / `XBROWSERSYNC_DB_PWD`.
  Unreachable Mongo at startup → log `Unable to connect to database`, exit code 1.
- **REQ-P5 — Atomicity.** Timestamp CAS and revision CAS are EACH a single
  `findOneAndUpdate` filtered on (`_id` + base). Win returns the post-write
  document; loss performs exactly one follow-up read to distinguish `401`
  (missing) from `409` (conflict). The v1 blind write and the v2 no-base update
  are intentionally read-then-write (Node parity: last-writer-wins, §REQ-U1/U2).
  Concurrent same-base pushes MUST resolve to exactly one `200` + N−1 `409`s
  (Node returns `[200,200]` — that upstream race is the bug being fixed, §11.3).
- **REQ-P6 — Touch.** Read-touch sets ONLY `lastAccessed` (single-field update,
  returns post-touch doc). Metadata reads (`GetMetadataAsync`) and CAS losers
  MUST NOT touch.

---

## 8. Configuration

Precedence: `appsettings.json` ← `appsettings.Local.json` (optional, watched) ←
environment (`__` nests, e.g. `Db__Host=mongo`). Empty `Db.Username/Password`
fall back to env (REQ-P4). Secrets belong in `.env` (chmod 600), never in the
tracked `{}` placeholder `appsettings.Local.json`.

| Key | Default | Verifier note |
|---|---|---|
| `AllowedOrigins` | `[]` (all allowed) | exact-string match only (§8.1) |
| `DailyNewSyncsLimit` | `3` | `0` disables check+logging |
| `Db.Host/Port/Name/AuthSource` | `127.0.0.1:27017/xbrowsersync/admin` | — |
| `Db.Username/Password` | `""` → env fallback | URL-escaped |
| `Db.ConnTimeout` | `30000` ms | — |
| `Db.UseSrv/Db.Ssl` | `false` | TLS triggers |
| `Location` | `""` | ISO code, uppercased; invalid → exit 1 (§8.2) |
| `Log.Stdout.Enabled/Level` | `true/info` | `false` removes console sink |
| `Log.File.Enabled/Level/Path` | `true/info//var/log/xBrowserSync/api.log` | §9.2 |
| `Log.File.RotationPeriod` | `1d` | `$n$scope` ms/s/m/h/d/w/M/y or hourly/daily/weekly/monthly/yearly |
| `Log.File.RotatedFilesToKeep` | `5` | retention |
| `MaxSyncs` | `5242` | `0` = unlimited |
| `MaxSyncSize` | `1048576` | `0` → 1 MB fallback (keeps Node's 0-means-default); Kestrel cap |
| `Server.Host/Port/RelativePath` | `127.0.0.1:8080:/` | Kestrel binds `AnyIP:Port`; `RelativePath` → `UsePathBase` |
| `Server.BehindProxy` | `false` | §8.3 |
| `Server.Https.Enabled/CertPath/KeyPath` | disabled | §8.4 |
| `Status.Online/AllowNewSyncs/Message` | `true/true/""` | message: markdown kept, scripts stripped |
| `Throttle.MaxRequests/TimeWindow` | `1000` / `300000` ms | `0` disables; §8.5 |

- **REQ-CFG1 — Hot vs restart.** ONLY `Log.Stdout.Level` is hot (watched file,
  applies in seconds, change is logged). Every other key requires restart/recreate.
- **REQ-CFG2 — Level names.** `trace/debug/info/warn|warning/error/fatal`
  (case-insensitive, trimmed); unknown/empty → `info`.

### 8.1 Origins and CORS

- REQ-O1: `AllowedOrigins` empty → CORS `AllowAnyOrigin/Header/Method` (Node parity).
  Non-empty → `WithOrigins(exact list)`.
- REQ-O2: `OriginGuardMiddleware` (inside the error handler so rejections keep the
  `{code,message}` shape): throws `OriginNotPermittedException` (500) ONLY when the
  list is non-empty AND an `Origin` header is present AND not exactly contained.
  Missing `Origin` header → passes. `RegExp` entries (Node-supported) are NOT
  supported — documented limitation.

### 8.2 Location

- REQ-L1: `IsValidLocation`: exactly two ASCII letters AND known to `RegionInfo`
  (culture names like `en-US` / 3-letter codes refused); empty = valid; invalid → stderr
  `Location is not a valid country code, exiting`, exit 1. (Node uses
  `country-list`; codes agree, names may differ — accepted.)

### 8.3 Proxy

- REQ-PX1: `ForwardedHeaders (XForwardedFor|XForwardedProto)` applied ONLY when
  `BehindProxy == true`, with `ForwardLimit = 1` (single trusted hop; longer chains
  would let clients forge IPs past throttle/quota). Default false → direct remote
  IPs used for throttle + daily-limit identity.

### 8.4 TLS / HSTS / headers

- REQ-T1: `Server.Https.Enabled` → Kestrel `UseHttps(certPath, keyPath)` + `UseHsts()`.
  Behind a proxy: NO HSTS from the app (proxy's job).
- REQ-T2: Every response carries `X-Content-Type-Options: nosniff`,
  `X-Frame-Options: DENY`, `Referrer-Policy: no-referrer`,
  `X-DNS-Prefetch-Control: off`, `Cross-Origin-Resource-Policy: same-origin`,
  `Cache-Control: no-store`. Kestrel `Server` header suppressed
  (`AddServerHeader = false`).

### 8.5 Throttle

- REQ-TH1: Per-IP fixed window (`MaxRequests` per `TimeWindow` ms); `0` disables
  entirely. Rejection: `429 {code:RequestThrottledException,…}` + `Retry-After`
  header = `ceil(TimeWindow/1000)` seconds (min 1). Unknown IP buckets as `"unknown"`.

---

## 9. Logging and observability

- **REQ-LOG1 — Levels.** Bunyan-compatible names (§REQ-CFG2). Default `info`.
  `info`: one-line request summaries (`METHOD path status ms`, written in a
  `finally` outside the error handler, so error responses and 429s too), creations,
  daily-limit refusals, conflicts, 5xx, throttle rejections, startup line.
  `debug`/`trace` (verbose): per-request IP/version/sizes, per-Mongo-op timings,
  every logic decision with values, effective settings dump.
- **REQ-LOG2 — Hot verbosity.** `LogLevelWatcher` moves the shared gate on
  `appsettings.Local.json` changes without restart; the change itself is logged.
- **REQ-LOG3 — Redaction (critical).** Bodies, keys, and full sync IDs MUST NEVER
  appear in any sink. IDs render as `ab12…ef` (first 4 + `…` + last 2;
  ≤8 chars → `…`); request paths are scrubbed for both dashed and 32-hex ID forms
  before logging. The DB password is excluded from the settings dump. Verifier:
  grep logs after exercising every endpoint with known IDs and fail on any full
  match.
- **REQ-LOG4 — File sink.** Honors path/level/period/keep; creates directories;
  rotation `api.log → api.log.1 → .2 …`, dropping beyond keep. Period
  syntax: `$number$scope` (`10ms, 5s, 30m, 12h, 1d, 2w, 3M, 1y`) or
  `hourly/daily/weekly/monthly/yearly` (weeks start Sunday, bunyan-compatible).
  Unwritable path → console warning + sink disables itself; service keeps running.
- **REQ-LOG5 — Startup lines.** `Service started at {url} (log level: {level})`
  at Info; full settings (minus password) at Debug.

---

## 10. Security properties to verify

- **REQ-S1 — NoSQL injection.** All body access requires scalar JSON per field
  (objects/arrays → treated as absent → 400); all Mongo access uses typed
  `Builders<T>` filters/updates; no raw BSON, no string-built queries. Verifier:
  send `{"bookmarks":{"$gt":""}}`, arrays, nested objects on every route → expect
  400/409-contract responses, never a match or server error.
- **REQ-S2 — BSON confusion.** By-ID filters pin `UuidStandard`; document `_id`
  is `BsonBinaryData`, never `Guid`. Cross-check a doc written by Node reads
  identically here and vice versa.
- **REQ-S3 — No mass assignment.** Extra body keys ignored; update touches exactly
  the fields in REQ-U5.
- **REQ-S4 — Zero-knowledge.** Ciphertext stored verbatim; no decryption attempt,
  no key/password storage anywhere (grep for crypto/encryption calls outside tests
  → expect none).
- **REQ-S5 — Capability-URL model.** No auth by design; 122-bit random IDs
  (`Guid.NewGuid` RFC 4122 bytes). IDs must not leak via logs (§REQ-LOG3), error
  details (only `{code,message}`), or URLs beyond the request path itself.
- **REQ-S6 — DoS caps.** Kestrel body cap (§REQ-Q3), throttle (§REQ-TH1), bounded
  log retention; regexes used (`HtmlSanitizer`, `LogSanitizer` generated regexes)
  must be linear-time — ReDoS-check `StripScripts` with adversarial nested input.
- **REQ-S7 — Error hygiene.** Unhandled exceptions → `500 Unspecified` + server-side
  Error log with scrubbed path; no stack traces, no BSON/connection details on
  the wire. `HasStarted` respected (never double-write).
- **REQ-S8 — Secrets.** `.env` gitignored + `chmod 600`; backup script passes
  credentials via container env (invisible to host `ps`); `mongo-init` reads
  passwords from env into `mongosh` without JS-string interpolation.

---

## 11. Test evidence (what exists, what must stay green)

- **REQ-T1 — Unit.** `dotnet test` — 156 xUnit, no infrastructure. Covers: version
  dispatch matrix (incl. `1.1.2 → v2`, `v1.2.0 → v2`), request coercion
  (`42 → "42"`, objects → absent), error map, timestamp truncation/parsing
  (`.87 == .870`), SyncId round-trip/rejection, CAS win/lose/missing via fakes
  (timestamp AND revision), revision lifecycle incl. legacy `revision: 0`,
  legacy shape freeze (no `meta`/`capabilities` keys), levels/redaction, file
  rotation determinism (injected clock), retention.
- **REQ-T2 — Live v1 parity.** `BASE=… ./scripts/e2e-v1.sh` — 26 curl checks
  (info, create/get/put/lastUpdated/version, stale-put 409, bad-ID 401, guards,
  unknown-route 404 shape, v1-missing-put `200 {}`, malformed-JSON 500). Requires
  fresh-ish DB (wipe `bookmarks` + `newsynclogs` first; daily limit is 3/IP).
- **REQ-T3 — Live v1.2.** `BASE=… ./scripts/e2e-v12.sh` — 19 checks (meta on all
  responses, `baseRevision` win/conflict, `HEAD` headers, conditional `304`,
  `DELETE`, legacy `404` on HEAD/DELETE, legacy shape regression).
- **REQ-T4 — Concurrency proof.** Same-base race harness → `[200,409]` on this
  server vs `[200,200]` on Node (documents the fixed upstream race), for BOTH
  timestamp and revision bases; plus dirty/pull-refusal scenarios per client rules.
- **REQ-T5 — Compat matrix (last verified).** v1.1 contract 24/24 against both
  servers; .NET client live 6/6 (legacy fallback vs Node, v1.2 vs here);
  v1.2 19/19 here (n/a on Node).

---

## 12. Acceptance checklist for the reviewer

For each item: state PASS/FAIL with file:line + reproduction (command or test name).

**Contract (C)**
- [ ] C1. Every row of §REQ-G2 returns the exact status + `code` (script all error paths).
- [ ] C2. `1.1.2` routes to v2; `GET …/version` works on `1.0.x`; non-`1` majors 412.
- [ ] C3. Header-less requests behave as `1.1.13` (no `meta`/`capabilities` keys anywhere).
- [ ] C4. `PUT` v1 missing sync → `200 {}` (empty object, no `lastUpdated`).
- [ ] C5. Malformed JSON → `500 Unspecified`; `GET /bookmarks` → `404 NotImplemented`.
- [ ] C6. Whole-second instants serialize with `.000`; `.87` base matches `.870` stored.
- [ ] C7. Legacy `HEAD`/`DELETE` → `404 NotImplemented`, not 412.
- [ ] C8. Scalar coercion (`42→"42"`, `true→"true"`) vs objects/arrays → 400.

**Concurrency & data (D)**
- [ ] D1. Same-base race (timestamp) → exactly one 200 + one 409, live against Mongo.
- [ ] D2. Same-base race (revision) → same `[200,409]` outcome.
- [ ] D3. `baseRevision` wins over `lastUpdated` when both sent; ignored on legacy versions.
- [ ] D4. Every write bumps `revision`, refreshes `hash/size`, preserves `version`
      unless replaced, never touches `createdAt`.
- [ ] D5. Legacy docs read with `revision: 0`, no hash, `size: 0`, no `createdAt`;
      no backfill writes observed.
- [ ] D6. `GET`-family touches `lastAccessed`; `HEAD`/conditional-precheck do not.
- [ ] D7. `_id` subtype `0x04` byte-compatible with Node-written docs (read both ways).

**Quotas & config (Q)**
- [ ] Q1. `405` when `AllowNewSyncs=false` or count reached; `0` = unlimited.
- [ ] Q2. `406` on 4th create/IP/day; `0` disables; missing IP never tracked.
- [ ] Q3. Over-`MaxSyncSize` body → `413`; `/info.maxSyncSize` reflects config.
- [ ] Q4. Only `Log.Stdout.Level` is hot; all else needs restart (prove one of each).
- [ ] Q5. Invalid `Location` → stderr + exit 1; Mongo down → log + exit 1.

**Security & hygiene (S)**
- [ ] S1. NoSQL-injection battery (§REQ-S1) — all rejected within contract.
- [ ] S2. No full sync ID in any log sink after full endpoint exercise.
- [ ] S3. No stack/BSON/secret leakage on forced 500s; `Server` header absent.
- [ ] S4. `ForwardLimit=1`, proxy headers ignored unless `BehindProxy`.
- [ ] S5. `StripScripts` removes only `<script>` blocks (parity) — confirm and note
      the residual-XSS boundary as accepted (README documents it).

**Ops (O)**
- [ ] O1. `docker compose up` from clean clone works per README (incl. one-time
      volume ownership); API waits for healthy Mongo; both healthchecks pass.
- [ ] O2. Backup script: prod-only by default, `INCLUDE_TEST_DB=1` opts in,
      7-dump retention, concurrent runs serialize, password with spaces/`$`/quotes works.
- [ ] O3. Restore procedure in README works; API recreates indexes on boot.
- [ ] O4. Every README command/flag/port/volume/env name verified against the files.

---

## 13. Known deliberate divergences (NOT defects — do not file)

1. CAS is stronger than Node: `[200,409]` vs upstream `[200,200]` — the fixed race.
2. `Default MaxSyncSize` 1 MB vs Node's 512 kB (official-service parity); `0`
   still means "default" in both.
3. `AllowedOrigins`: exact strings only (Node also takes `RegExp`).
4. `Location` validated via `RegionInfo`, not `country-list`.
5. `GET /info` logs a Warning (not silent) when the count fails.
6. `GetMetadataAsync` does not touch `lastAccessed` (pure metadata polling).
7. File rotation is `.1`-based (`api.log → .1 → .2…`); README's `.0` shorthand is loose.
8. `message` sanitizer strips only `<script>` blocks (Node parity; residual XSS is an
   accepted, documented operator boundary — the message is operator-authored).
9. `expiresAt` uses the UTC calendar day while Node's `moment()` uses the server
   local day — identical when `TZ=UTC` (all containers), one-day-boundary skew
   possible on bare metal with non-UTC TZ.

## 14. Deliverables expected from the reviewer

1. Finding list: severity (High/Med/Low) × (file:line, requirement ID, reproduction,
   exploitability/impact, suggested fix). False positives checked against §13.
2. Checklist (§12) with PASS/FAIL + evidence per item.
3. Confirmation of green: `dotnet test` count, both e2e scripts against a live
   container, one same-base race run.
4. Statement of what was NOT reviewed (if anything in §1 was skipped).

## 15. Quick-start for the reviewer

```sh
cd KeepmarkApi
dotnet test                                   # unit, no infra (expect 156 pass)
cp .env.example .env                          # set real passwords, chmod 600 .env
docker compose up --build -d                  # API :8080 in-container
BASE=http://localhost:<API_PORT> ./scripts/e2e-v1.sh    # expect 26/26
BASE=http://localhost:<API_PORT> ./scripts/e2e-v12.sh   # expect 19/19
```

Wipe between live runs (daily limit is 3/IP):
`mongosh … --eval "db.getSiblingDB('xbrowsersync').bookmarks.deleteMany({});
db.getSiblingDB('xbrowsersync').newsynclogs.deleteMany({});"`.
Key sources: `src/KeepmarkApi.Core/Logic/Logic.cs` (rules),
`src/KeepmarkApi/Controllers/Controllers.cs` (routing/shaping),
`src/KeepmarkApi.Core/Routing/ApiVersioning.cs`,
`src/KeepmarkApi.Core/Requests/BookmarkRequests.cs`,
`src/KeepmarkApi/Data/MongoStores.cs` (atomicity),
`src/KeepmarkApi/Middleware/Middleware.cs`, `src/KeepmarkApi/Program.cs`,
`src/KeepmarkApi.Core/Errors/*`, `src/KeepmarkApi.Core/Time/SyncTime.cs`,
`src/KeepmarkApi.Core/Uuid/SyncId.cs`, `src/KeepmarkApi.Core/Content/BlobMeta.cs`.
