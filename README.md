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
  new-sync quota, an origin allow-list, security headers, and sync IDs
  truncated in every log line.
- **Small and auditable.** One runtime dependency (`MongoDB.Driver`), about
  2k lines of logic, 156 unit tests plus live contract scripts.

## What v1.2 adds

Clients opt in with `Accept-Version: ^1.2.0`; everyone else gets v1.1.

- **Revisions** on every sync (`revision`, `hash`, `size`, `createdAt`) and
  revision-based conflict detection.
- **Cheap "anything new?" checks:** `HEAD` and conditional `GET` with `304`.
- **Delete a sync** from the server (`DELETE`).
- **Capabilities** advertised in `/info`, so clients pick features safely.

Keepmark for browsers and Keepmark for Android use v1.2 automatically when
the server offers it.

## Quick start

```sh
cp .env.example .env          # set real passwords (chmod 600 .env)
docker compose up --build -d
curl localhost:8080/info
```

The [developer guide](docs/DEVELOPMENT.md#docker) covers volume ownership,
TLS, backups and every configuration key.

## Already running xBrowserSync?

Switching takes about ten minutes with no data migration: the collections,
the ID format and the settings keys are the same, and rollback is a DNS
change. Read the [operator guide](docs/operators.md).

## The Keepmark family

| | |
|---|---|
| **Keepmark Server** | This service (ASP.NET Core on .NET 10, MongoDB) |
| **Keepmark for browsers** | Extension for Firefox and Chromium-based browsers (Chrome, Edge, Brave) |
| **Keepmark for Android** | Android 10+ app with search, share-to-Keepmark and app lock |

Project home: <https://github.com/Keepmark>.

## Documentation

- [Operator guide](docs/operators.md): why switch, migration, FAQ.
- [Developer and operations guide](docs/DEVELOPMENT.md): layout, API
  contracts, configuration, Docker, logging, backups, testing, security notes.
- [Verification spec](docs/verification-spec.md): behavioural requirements
  for code review.
- [Contributing](CONTRIBUTING.md).

## Contact

Questions, feedback, security reports: <keepmark@proton.me>.

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
