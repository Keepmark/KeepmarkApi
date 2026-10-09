# CLAUDE.md — KeepmarkApi (.NET 10 sync service)

Source of truth is `docs/DEVELOPMENT.md` (layout, v1.1/v1.2 contracts,
config table, Docker, ops, test matrix). Edit that file, not this one.
Repo-wide layout is in `../AGENTS.md` (loaded via `../CLAUDE.md`). Operator
guide: `docs/operators.md`; behavioral spec for review: `docs/verification-spec.md`.

`README.md` is the user-facing GitHub landing page (no build details there).

Quick reminders (details in docs/DEVELOPMENT.md):

- Own git repo (`KeepmarkApi/`); the parent dir is not a repo.
- `dotnet test` needs no infra; live contract checks:
  `BASE=http://localhost:8090 ./scripts/e2e-v1.sh` and `./scripts/e2e-v12.sh`.
- Logic stays in `KeepmarkApi.Core` (no ASP.NET reference), testable with fakes.
- v1.1 response shapes are frozen (`Shaping/ResponseShapes`, unit-pinned);
  v1.2 is opt-in via `Accept-Version: ^1.2.0`.
- Updates are atomic CAS (`findOneAndUpdate` on `_id` + base), never
  read-then-write. Timestamps are ms-precision (`SyncTime`).
- Sync IDs are bearer tokens: truncate in logs (`ab12…ef`), never log bodies.
- Bookmark `_id` is BSON Binary subtype 4 (16 bytes), never `Guid`.
- No secrets in `appsettings.Local.json` (tracked placeholder); use `.env`.

@docs/DEVELOPMENT.md
