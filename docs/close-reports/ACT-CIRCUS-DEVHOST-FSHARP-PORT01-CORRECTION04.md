# ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION04 — Close Report

**Starting commit:** `c04b0b6` (CORRECTION03 implementation)
**Implementation commit:** recorded at commit time
**Final tested commit:** same as implementation commit

## Verdict

**ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION04 — CLOSED**

The remaining R1 blockers are resolved. The archive rollback tracks
explicit recovery state and never destroys the recovery copy on a
partial rollback; the ordinary pre-effect failure case is now tested;
the launcher mutation test is a real equality assertion; the
committed tree reproduces a `pass` detached gate.

## What changed in this commit

* `tools/Circus.DevHost/Archives.fs` — `extractAtomicWith` now records
  an explicit recovery report (`notes`, `previousRecovered`,
  `previousPreserved`) and only releases the recovery copy when the
  previous install is observably back in `absoluteFinal` (or the
  cold-start path was taken). The error label announces "rollback
  incomplete; previous installation retained at …" when the recovery
  could not be committed.
* `tests/Circus.DevHost.Tests/ArchivesTests.fs` — adds four new cases:
  ordinary failed first-move (pre-effect), ordinary failed
  second-move (pre-effect), failed delete of the candidate (rollback
  detail + remaining final directory), and a tightened cold-start
  case that asserts no `.circus-install-*` directory is leaked.
* `tests/Circus.DevHost.Tests/LauncherPolicyTests.fs` — the manifest
  mutation case now derives `BOOTSTRAP_IMAGE='reference@sha256'` from
  the parsed manifest and asserts the launcher equality against the
  *mutated* manifest. It also exercises a mutated `reference` to
  prove the equality depends on both halves of the pinned value.
* `docs/acts/ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION04.md` —
  this ACT.
* `docs/close-reports/ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION04.md` —
  this report.

CORRECTION01, CORRECTION02 and CORRECTION03 documents remain as
historical records and are not edited.

## Evidence (all captured against the implementation commit)

| claim | result |
| --- | --- |
| `dotnet build tools/Circus.DevHost/Circus.DevHost.fsproj -c Release` | 0 warnings, 0 errors |
| `dotnet build tests/Circus.DevHost.Tests/Circus.DevHost.Tests.fsproj -c Release` | 0 warnings, 0 errors |
| `dotnet run … -- --summary` (Expecto) | 31/31 passing, 0 failed, 0 errored |
| Archive rollback: failed delete of the candidate | `Error`; the error detail announces "rollback incomplete; previous installation retained at …"; the final directory is still on disk |
| Archive rollback: cold-start failed second move | `Error`; no `.circus-install-*` directory remains in the temp root |
| Launcher policy: manifest mutation breaks equality | committed manifest + launcher matches; mutated digest + launcher does not match; mutated reference + launcher does not match |
| `sh -n scripts/circus-dev` | parses |
| `shellcheck scripts/circus-dev` | clean |
| `grep -nE '\bpython(3)?\b|jq ' scripts/circus-dev` | no match |
| `BOOTSTRAP_IMAGE` line | `BOOTSTRAP_IMAGE='mcr.microsoft.com/dotnet/sdk:10.0.202-noble@sha256:caa1a2d363812eb21df9c56f01fa59d5d81bbb03103cb6a48f32dd7e80855616'` |
| `eng/devhost-toolchain.json` `bootstrap_sdk_image` | matches the launcher line |
| Detached gate summary | `pass (3/3 pass) tree=104a53d9f652` |
| `git status` after build and tests | working tree clean (gate summary is `.gitignore`d) |
| `git diff --check HEAD` | clean |
