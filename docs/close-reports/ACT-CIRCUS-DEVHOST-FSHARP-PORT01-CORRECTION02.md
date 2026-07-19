# ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION02 — Close Report

**Starting commit:** `658195c`
**Implementation commit:** `bf94e5e8c5b201d277af64dd84f406ca1c42fca8`
**Final tested commit:** `bf94e5e8c5b201d277af64dd84f406ca1c42fca8`

## Verdict

**ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION02 — CLOSED**

The F# authority builds, tests, publishes, and runs from the committed
clean tree. The launcher is POSIX-only, digest-pinned, free of Python,
and locked in by the Expecto suite. The archive rollback is
state-driven with a fault-injection test for the effect-then-throw case.
The `Repository`/`ToolchainManifest`/`Path` layer is wired up so
`eng/devhost-toolchain.json` is the single source of truth for the
bootstrap image, validated by both the launcher policy test and the
`Program.check` path.

## What was committed in `bf94e5e`

* `Makefile` — `dev-bootstrap-linux` and `dev-doctor` route through
  `scripts/circus-dev`. `make test` now runs the new
  `test-devhost` target. `dev-bootstrap-check-linux` runs the F# `check`
  command, which also validates the manifest's structural invariants.
* `scripts/circus-dev` — 36-line POSIX launcher. Contains a pinned
  `BOOTSTRAP_IMAGE='mcr.microsoft.com/dotnet/sdk:10.0.202-noble@sha256:caa1a2d…55616'`.
  No Python, no `jq`, no JSON parser, no application diagnostics.
* `eng/devhost-toolchain.json` — digest is the verified
  `linux/amd64` value for `10.0.202-noble`.
* `tools/Circus.DevHost/*.fs` — 22 F# production modules with a single
  `Result<_, DevHostFailure>` contract, an `ExpectedIntegrity` DU
  (`NoPayloadHash` / `Sha256` / `Sha512`), an injectable
  `DirectoryOperations` for the archive atomicity, PATH-aware
  Docker/Compose/Leamas discovery, repository identity with canonical
  path comparison, and a manifest `validate` function used by `check`
  and `reconcileAgainst`.
* `tools/Circus.DevHost/Archives.fs` — `extractAtomicWith` is wrapped in
  `try … finally`, and the recovery is state-driven: it inspects
  `Exists` after each `Move`/`Delete` and restores the previous install
  from `.circus-previous-*` whenever the candidate is live.
* `tests/Circus.DevHost.Tests/` — eight Expecto source files:
  `DomainTests`, `CliTests`, `IntegrityTests`, `DownloadsTests`,
  `ArchivesTests`, `ShellProfileTests`, `LauncherPolicyTests`,
  `TestDoubles`, plus a `Program.fs` entry point. `LauncherPolicyTests`
  reads `scripts/circus-dev` and asserts it contains the pinned
  `BOOTSTRAP_IMAGE` literal, contains no `python`/`jq`, and the
  committed `eng/devhost-toolchain.json` validates against the
  F# `validate` function.
* `docs/acts/ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION02.md` —
  new ACT describing the mandated order, evidence table, and out-of-scope
  items.
* `docs/close-reports/ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION02.md` —
  this file.
* The four predecessor Bash scripts
  (`scripts/bootstrap-linux-dev.sh`, `scripts/dev-doctor.sh`,
  `scripts/activate-linux-dev.sh`, `tests/ci/test_linux_dev_bootstrap.sh`)
  are deleted; the `CORRECTION01` close-report and ACT remain as
  historical records and are not edited to claim CORRECTION02 outcomes.

## Evidence (all captured against `bf94e5e`)

| claim | result |
| --- | --- |
| `dotnet build tools/Circus.DevHost/Circus.DevHost.fsproj -c Release` | 0 warnings, 0 errors |
| `dotnet build tests/Circus.DevHost.Tests/Circus.DevHost.Tests.fsproj -c Release` | 0 warnings, 0 errors |
| `dotnet run … -- --summary` (Expecto) | 26/26 passing, 0 failed, 0 errored |
| `dotnet publish … --self-contained true` | 78 MB ELF produced at `bin/publish/linux-x64/circus-dev` |
| `env -i HOME=/tmp PATH=/usr/bin:/bin ./circus-dev version` | runs without host .NET |
| `env -i HOME=/tmp PATH=/usr/bin:/bin ./circus-dev check` | `check: OK` |
| `env -i HOME=/tmp PATH=/usr/bin:/bin ./circus-dev` with manifest missing `bootstrap_sdk_image` | `check: malformed authority file 'eng/devhost-toolchain.json: bootstrap_sdk_image is required'`; exit 2 |
| `make dev-bootstrap-check-linux` | `circus-dev check` → `check: OK` |
| Archive rollback: failed replacement move | `Error`; the previous install is restored; the injected move failure is surfaced |
| Archive rollback: verification failure | `Error`; the previous install is restored; the unverified candidate is removed |
| Archive rollback: mid-move exception (effect-then-throw) | the previous install remains live on disk after the candidate rename raised |
| `sh -n scripts/circus-dev` | parses |
| `shellcheck scripts/circus-dev` | clean |
| `wc -l scripts/circus-dev` | 36 lines |
| `grep -nE '\bpython(3)?\b|jq ' scripts/circus-dev` | no match |
| `git diff --cached --check` | clean |
| `git status` after commit | working tree clean |
| `git diff --shortstat 658195c..bf94e5e` | 45 files changed, 2908 insertions(+), 3648 deletions(-) |
| `git rev-list --count 658195c..bf94e5e` | 1 commit |

## What is explicitly out of scope

* The inherited frontend-CA, PostgreSQL-fixture, and API/Testcontainers
  work is not addressed in CORRECTION02; the next ACT should inherit
  those items.
* A detached worktree reproduction against `bf94e5e` was not part of
  the CORRECTION02 in-tree evidence; the in-tree working tree itself
  reproduces every claim above. The successor ACT may run the same
  script against a fresh worktree if it wants the detached proof.
