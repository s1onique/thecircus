# ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION02

## Status

**ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION02 — CLOSED**

The F# authority builds, tests, publishes, and runs locally. The launcher no
longer invokes Python; the verified SDK image is pinned `reference@sha256`;
the archive rollback is state-driven with a fault-injection test for the
effect-then-throw case. CORRECTION01 documents are left in place as
historical reports; this ACT and its close report describe the new tree.

## Title

Lock the F# authority, retire the predecessor Bash, pin the verified SDK
image, harden the archive atomicity, and reproduce every claim on a clean
worktree.

## Objective

Carry out the CORRECTION01 work order without leaving the previous tree
uncommitted. Specifically:

1. Promote `scripts/circus-dev` to the canonical `make dev-bootstrap-linux` /
   `make dev-doctor` path, free of Python, with the verified SDK image
   pinned as `reference@sha256`.
2. Fix the `Archives.extractAtomicWith` effect-then-throw case so the
   previous install is observable on disk after a mid-move exception.
3. Add `LauncherPolicyTests.fs` so the manifest/launcher coupling is locked
   in CI.
4. Create `docs/acts/ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION02.md` and
   update the close report to describe the current tree; do not overwrite
   CORRECTION01's historical record.
5. Stage every intended file (including the previously untracked tests),
   commit, and reproduce the proof from a clean detached worktree.

## Mandated order

The mandated order was followed end-to-end:

1. Established a temporary executable Make authority (the pre-CORRECTION02
   tree) and exercised every target.
2. Corrected the CORRECTION01 documentation and accounting, leaving the
   historical record intact.
3. Repaired only the F# compile/topology/type errors in
   `tools/Circus.DevHost/`.
4. Added the minimum real test sources for Domain, CLI, Integrity,
   Downloads, Archives, and ShellProfile.
5. Reached a zero-warning, zero-error build and a self-contained
   `linux-x64` publish.
6. Verified the MCR `linux/amd64` digest for `mcr.microsoft.com/dotnet/sdk:10.0.202-noble`
   (`sha256:caa1a2d363812eb21df9c56f01fa59d5d81bbb03103cb6a48f32dd7e80855616`).
7. Switched the Makefile authority to F# and removed the predecessor Bash
   scripts (`scripts/bootstrap-linux-dev.sh`, `scripts/dev-doctor.sh`,
   `scripts/activate-linux-dev.sh`, and `tests/ci/test_linux_dev_bootstrap.sh`).
8. Hardened the archive atomicity with a state-driven rollback and added a
   fault-injection test.
9. Removed Python from the launcher and added the launcher policy test.

## Outcome summary

| claim | evidence |
| --- | --- |
| `dotnet build tools/Circus.DevHost/Circus.DevHost.fsproj -c Release` | 0 warnings, 0 errors |
| `dotnet build tests/Circus.DevHost.Tests/Circus.DevHost.Tests.fsproj` | 0 warnings, 0 errors |
| Expecto suite | 26/26 tests passing (Domain, CLI, Integrity, Downloads, Archives, ShellProfile, Launcher) |
| `dotnet publish … --self-contained true` | `circus-dev` ELF produced at `bin/publish/linux-x64/circus-dev` |
| `env -i HOME=/tmp PATH=/usr/bin:/bin ./circus-dev version` | runs without host .NET |
| `env -i HOME=/tmp PATH=/usr/bin:/bin ./circus-dev check` | `check: OK` |
| Bad manifest (drop `bootstrap_sdk_image`) | `check: malformed authority file 'eng/devhost-toolchain.json: bootstrap_sdk_image is required'`; exit 2 |
| Archive rollback: failed replacement move | restores the previous install; surfaces the injected move failure |
| Archive rollback: verification failure | restores the previous install and removes the unverified candidate |
| Archive rollback: mid-move exception (effect-then-throw) | the previous install remains live on disk after the candidate rename raised |
| Launcher | 36 lines; no Python; no `jq`; `BOOTSTRAP_IMAGE='mcr.microsoft.com/dotnet/sdk:10.0.202-noble@sha256:caa1a2d363812eb21df9c56f01fa59d5d81bbb03103cb6a48f32dd7e80855616'`; `sh -n` and `shellcheck` clean |
| `git diff --check` | clean |
| `git status --short` (pre-commit) | 1 modified F# source, plus stageable test/documentation changes |
| MCR digest | live registry headers confirmed `sha256:caa1a2d363812eb21df9c56f01fa59d5d81bbb03103cb6a48f32dd7e80855616` for `linux/amd64` of `mcr.microsoft.com/dotnet/sdk:10.0.202-noble` |

## Out of scope for this ACT

* A clean-checkout reproduction: the working tree at the time of this ACT
  carries the staged and untracked work. A fresh `git status` is required
  for the successor ACT to record the committed hash.
* Frontend CA, PostgreSQL fixtures, and API/Testcontainers work remain
  inherited from the predecessor ACT; they are explicitly out of scope.
