# Switching to KeepmarkApi — a note for xBrowserSync service operators

You run a public xBrowserSync service. Thank you — the network lives on
volunteers like you. KeepmarkApi exists so you can keep offering the exact
same service, cheaper to run and harder to break, without asking a single
user to change anything.

## Why switch

**Nothing changes for your users.** The v1.1 wire contract is byte-identical:
same endpoints, same error codes, same Mongo layout. Official clients,
`../app` builds, Keepmark and KeepmarkApp keep syncing against your host
exactly as before — no migration on their side, ever.

**What changes for you:**

| | xBrowserSync API (Node) | KeepmarkApi (.NET 10) |
|---|---|---|
| Runtime | Node.js + 50+ npm packages (2021-era) | Single self-contained binary, one dependency (`MongoDB.Driver`) |
| Silent overwrites | Concurrent same-base pushes both return `200` — one user's sync silently lost | Atomic compare-and-swap: loser gets `409`, data never silently lost |
| Timestamps | String comparison of `lastUpdated` | Millisecond instants (`.87` == `.870`), writes truncated to BSON precision |
| Observability | Bunyan to file/stdout | Leveled logs (console + rotating file), on-the-fly verbosity, redacted sync IDs, per-op timings |
| Ops | Manual install, manual indexes | Docker Compose (API + Mongo + backups), auto-created indexes, healthchecks, `Retry-After` |
| Backups | DIY | `scripts/mongo-backup.sh` + cron, 7 rolling dumps |
| Future | Frozen upstream | Opt-in v1.2 (revision metadata, conditional `304` pulls, `DELETE`) — legacy untouched |

**Security posture.** Same zero-knowledge design (ciphertext only, capability-URL
IDs), plus: security headers + `no-store` by default, per-IP throttling with
`Retry-After`, origin allow-list, proxied deployments via `X-Forwarded-For`
only when you say so. The codebase is small enough to audit in an afternoon
(~2k LOC of logic, 141 unit tests).

## Migration in 10 minutes (zero user impact)

No data migration. The collections (`bookmarks`, `newsynclogs`), the `_id`
format (UUID binary subtype 4) and the settings keys are the same.

1. **Back up** your Mongo (`mongodump`), as always.
2. **Start KeepmarkApi** with Docker Compose, pointed at a *copy* first:
   ```sh
   cp .env.example .env   # set passwords + API_PORT
   docker compose up --build -d
   ```
   Either reuse your existing Mongo (`Db__Host`, credentials) or `mongorestore`
   the dump into the bundled one.
3. **Translate your `settings.json`** (keys are 1:1; only the file format
   changes to `appsettings` / env):
   | Node `settings.json` | KeepmarkApi |
   |---|---|
   | `server.port`, `server.host` | `Server__Port`, `Server__Host` |
   | `db.*`, `XBROWSERSYNC_DB_*` | same names, same env vars |
   | `maxSyncs`, `maxSyncSize`, `throttle.*` | same |
   | `status.message`, `status.online`, `location` | same (`Status__*`, `Location`) |
   | `log.file.path`, `log.stdout.level` | `Log__File__Path`, `Log__Stdout__Level` (+ hot-reload) |
4. **Smoke-test** against the copy: `scripts/e2e-v1.sh` (24 checks) plus one
   real client pointed at the new host.
5. **Cut over DNS.** If anything misbehaves, point DNS back — the data format
   never changed, so rollback is instant.
6. Old syncs show `revision: 0` until their next write; everything else works
   from second one.

## FAQ

- **Do my users need to update?** No. Not now, not for v1.2 either (opt-in).
- **Does it phone home?** No. No telemetry, no external calls. Logs stay on your disk.
- **Resource usage?** The API idles at tens of MB; Mongo dominates as before.
  Your `maxSyncs × maxSyncSize` budget math is unchanged.
- **Can I keep my domain/message/location?** Yes — all surfaced in `/info` as before.
- **License?** GPL-3.0, like upstream. Independent project, not affiliated with
  or endorsed by xBrowserSync.
