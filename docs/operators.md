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
| Ops | Manual install, manual indexes (incl. the optional 3-week expiry TTL) | Docker Compose (API + Mongo + backups), auto-created indexes, inactive-sync expiry as one setting (`SyncExpiryDays`), healthchecks, `Retry-After` |
| Backups | DIY | `scripts/mongo-backup.sh` + cron, 7 rolling dumps |
| Future | Frozen upstream | Opt-in v1.2 (revision metadata, conditional `304` pulls, `DELETE`) — legacy untouched |

**Security posture.** Same zero-knowledge design (ciphertext only, capability-URL
IDs), plus: security headers + `no-store` by default, per-IP throttling with
`Retry-After`, origin allow-list, proxied deployments via `X-Forwarded-For`
only when you say so. Open source (AGPL-3.0) and small enough to audit in an afternoon (~2k LOC of
logic, 193 unit tests, live contract scripts).

## Migration in 10 minutes (zero user impact)

No data migration: the collections (`bookmarks`, `newsynclogs`), the `_id`
format (UUID binary subtype 4) and the settings keys are the same. Back up,
stop Node, start KeepmarkApi on the same database and port; rollback is
starting Node again.

**Step by step, with copy-paste commands, settings mapping and checks:
[migration guide](migration.md).**

> **Binding note:** Kestrel listens on `AnyIP:Port` — `Server__Host` is display
> only, NOT a bind restriction. A reused `127.0.0.1` does not make you
> localhost-only on bare metal; restrict exposure via firewall or proxy.
> **Proxy note:** set `Server__BehindProxy=true` ONLY behind a proxy you control
> (single trusted hop, `ForwardLimit=1`). Without it, all clients share the
> gateway IP for throttling and the daily new-sync quota; with it on an exposed
> port, `X-Forwarded-For` can be forged to rotate identities past both.
> Terminate TLS at the proxy (recommended) — the app emits HSTS only in direct
> HTTPS mode.

## FAQ

- **Do my users need to update?** No. Not now, not for v1.2 either (opt-in).
- **Does it phone home?** No. No telemetry, no external calls. Logs stay on your disk.
- **Resource usage?** The API idles at tens of MB; Mongo dominates as before.
  Your `maxSyncs × maxSyncSize` budget math is unchanged.
- **I created the 3-week `lastAccessed` TTL index by hand. What now?** Set
  `SyncExpiryDays=21` and KeepmarkApi adopts it unchanged. Any other value
  replaces it at startup; `0` (the default) drops it, so syncs stop expiring.
  Details: [Inactive sync expiry](DEVELOPMENT.md#inactive-sync-expiry).
- **I never created that index. Can I turn expiry on?** Yes, but the first
  pass deletes every sync already idle longer than the limit, within about a
  minute of startup. Back up first, and count the affected syncs:
  `db.bookmarks.countDocuments({lastAccessed: {$lt: new Date(Date.now() - 21*86400000)}})`.
- **Can I keep my domain/message/location?** Yes — all surfaced in `/info` as before.
- **License?** AGPL-3.0-only (© 2026 Yari Melzani, see `LICENSE.md`): free to
  run, including public and commercial services. Running it unmodified needs
  nothing; if you modify it, offer your modified source to your users.
  Independent alternative compatible with xBrowserSync, not affiliated with
  or endorsed by xBrowserSync.
