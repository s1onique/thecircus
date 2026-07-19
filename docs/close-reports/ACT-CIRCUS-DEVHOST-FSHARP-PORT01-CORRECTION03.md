# ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION03 — Close Report

**Starting commit:** `8d9a7b6` (CORRECTION02 close report)
**Implementation commit:** `<recorded at commit time>`
**Final tested commit:** same as implementation commit

## Verdict

**ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION03 — CLOSED**

All R1 blockers raised against CORRECTION02 are resolved. The archive
rollback is state-driven; the `finally` block preserves the recovery
copy; the launcher policy test derives the expected image from the
parsed manifest; the digest validator enforces strict lowercase
hexadecimal; and the committed tree regenerates a `pass` detached gate.

## What changed in this commit

* `tools/Circus.DevHost/Archives.fs` — `extractAtomicWith` now inspects
  the live filesystem before disposing any directory. The
  `previousDir` is preserved unless the previous install is observably
  back in `absoluteFinal`; otherwise the rollback reports the failure
  with the recovery-copy path intact.
* `tools/Circus.DevHost/ToolchainManifest.fs` — `isPinnedSha256` now
  validates the full 64-character payload against the lowercase hex
  alphabet.
* `tests/Circus.DevHost.Tests/ArchivesTests.fs` — every fault test
  asserts both the previous install's `old.txt` and the absence of
  the failed candidate's `new.txt`. A new cold-start case proves the
  candidate is not left behind on disk after a mid-move failure.
* `tests/Circus.DevHost.Tests/LauncherPolicyTests.fs` — derives
  `BOOTSTRAP_IMAGE='reference@sha256'` from the parsed manifest, and
  adds a manifest-mutation case that proves launcher equality fails
  when the manifest digest is changed. A negative digest test
  exercises the strict-hex validation.
* `docs/acts/ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION03.md` —
  this ACT.
* `docs/close-reports/ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION03.md` —
  this report.

CORRECTION01 and CORRECTION02 documents remain as historical records
and are not edited.

## Evidence (all captured against the implementation commit)

| claim | result |
| --- | --- |
| `dotnet build tools/Circus.DevHost/Circus.DevHost.fsproj -c Release` | 0 warnings, 0 errors |
| `dotnet build tests/Circus.DevHost.Tests/Circus.DevHost.Tests.fsproj -c Release` | 0 warnings, 0 errors |
| `dotnet run … -- --summary` (Expecto) | 29/29 passing, 0 failed, 0 errored |
| `sh -n scripts/circus-dev` | parses |
| `shellcheck scripts/circus-dev` | clean |
| `grep -nE '\bpython(3)?\b|jq ' scripts/circus-dev` | no match |
| `BOOTSTRAP_IMAGE` line | `BOOTSTRAP_IMAGE='mcr.microsoft.com/dotnet/sdk:10.0.202-noble@sha256:caa1a2d363812eb21df9c56f01fa59d5d81bbb03103cb6a48f32dd7e80855616'` |
| `eng/devhost-toolchain.json` `bootstrap_sdk_image` | matches the launcher line |
| Detached gate summary | `pass (3/3 pass) tree=b0b8f4754b2a` |
| `git status` after build and tests | working tree clean (gate summary is regenerated and ignored) |
| `git diff --check HEAD` | clean |
