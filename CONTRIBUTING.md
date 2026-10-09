# Contributing to Keepmark Server (KeepmarkApi)

Thank you for helping! Before a pull request can be merged, you must sign the
[Contributor License Agreement](CLA.md) (CLA).

## Why a CLA

Keepmark Server (KeepmarkApi) is released under the GNU AGPL v3 only (AGPL-3.0-only). The CLA lets the maintainer
also use contributions in other Keepmark products, including commercial ones
(for example Keepmark for Android), and relicense them in the future. You keep
the copyright on your work.

## How to sign

1. Open your pull request.
2. The **CLA Assistant** bot comments on it with a link to `CLA.md`.
3. Reply in the pull request with exactly:

   ```
   I have read the CLA Document and I hereby sign the CLA
   ```

You sign once: future pull requests from the same GitHub account are covered.
If the check does not update, comment `recheck`. Signatures are stored in
`signatures/version1/cla.json` on the `cla-signatures` branch.

## Before opening a pull request

- `dotnet test` passes; for contract changes also run `scripts/e2e-v1.sh`
  and `scripts/e2e-v12.sh` against a live instance.
- v1.1 response shapes are frozen; logic stays in `KeepmarkApi.Core`.
- Read `docs/DEVELOPMENT.md` for layout, contracts and rules.

