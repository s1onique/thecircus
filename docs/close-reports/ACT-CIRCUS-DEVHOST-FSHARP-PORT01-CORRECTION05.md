# ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION05 — Close Report

* **Starting commit:** `3248840` (CORRECTION04 implementation)
* **Implementation commit:** `12bd4436f92059664d14a1d1224238efcda559d2`
* **Implementation tree OID:** `e2dbce6d245abfdd4d48a44b0102770cadef0dce`
* **Final CORRECTION05 commit:** `12bd4436f92059664d14a1d1224238efcda559d2`

## Verdict

**ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION05 — PARTIAL**

The previous-install recovery-copy defect and canonical-gate wiring are
closed, but cold-start installation and verification failures can leave a
failed or unverified candidate live at the final path. The CORRECTION05
cold-start test did not assert final-path absence, and the uploaded evidence
reported its detached gate source as missing. CORRECTION06 supersedes these
remaining findings.

## What changed in this commit

* `tools/Circus.DevHost/Archives.fs` — `extractAtomicWith` now uses
  explicit decision flags (`canDeletePrevious` and
  `canDeleteInstall`) instead of inferring safety from the live
  filesystem. The previous-temp is only released when the new install
  has been committed or when the previous install has been observably
  restored. On a partial rollback the previous-temp is preserved so
  a human operator can finish the recovery.
* `tests/Circus.DevHost.Tests/ArchivesTests.fs` — the failed-delete
  test now inspects the retained `.circus-previous-*` directory
  directly and asserts that `old.txt` is present in it.
* `Makefile` — `gate` now invokes `test-devhost`, so the devhost suite
  is continuously enforced on the canonical aggregate.
* `docs/acts/ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION05.md` —
  this ACT.
* `docs/close-reports/ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION05.md` —
  this report.

CORRECTION01, CORRECTION02, CORRECTION03 and CORRECTION04 documents
remain as historical records and are not edited.

## Evidence recorded by CORRECTION05

The implementation results below are retained as historical records. The
uploaded detached-gate claim was rejected during review and is explicitly
marked unavailable.

| claim | result |
| --- | --- |
| `dotnet build tools/Circus.DevHost/Circus.DevHost.fsproj -c Release` | 0 warnings, 0 errors |
| `dotnet build tests/Circus.DevHost.Tests/Circus.DevHost.Tests.fsproj -c Release` | 0 warnings, 0 errors |
| `dotnet run … -- --summary` (Expecto) | 31/31 passing, 0 failed, 0 errored |
| Archive rollback: failed delete of the candidate | `Error`; the error detail announces "rollback incomplete; previous installation retained at …"; the retained `.circus-previous-*` directory contains `old.txt`; the failed candidate remains live |
| Canonical `gate` invokes `test-devhost` | `gate: factorize format-check test-backend test-devhost test-web smoke` |
| Launcher policy: pinned image derived from manifest | `BOOTSTRAP_IMAGE='mcr.microsoft.com/dotnet/sdk:10.0.202-noble@sha256:caa1a2d363812eb21df9c56f01fa59d5d81bbb03103cb6a48f32dd7e80855616'` matches the manifest |
| Launcher policy: manifest mutation breaks equality | committed manifest + launcher matches; mutated digest + launcher does not match; mutated reference + launcher does not match |
| `sh -n scripts/circus-dev` | parses |
| `shellcheck scripts/circus-dev` | clean |
| `grep -nE '\bpython(3)?\b|jq ' scripts/circus-dev` | no match |
| `BOOTSTRAP_IMAGE` line | `BOOTSTRAP_IMAGE='mcr.microsoft.com/dotnet/sdk:10.0.202-noble@sha256:caa1a2d363812eb21df9c56f01fa59d5d81bbb03103cb6a48f32dd7e80855616'` |
| `eng/devhost-toolchain.json` `bootstrap_sdk_image` | matches the launcher line |
| Uploaded detached gate summary | **unavailable** — `source_status=missing`, `overall_status=unavailable`, `checks_total=0` |
| `git status` after build and tests | working tree clean (gate summary is `.gitignore`d) |
| `git diff --check HEAD` | clean |
