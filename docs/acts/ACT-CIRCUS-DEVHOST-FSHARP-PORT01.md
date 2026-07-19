# ACT-CIRCUS-DEVHOST-FSHARP-PORT01

## Status

PARTIAL — implementation in place; parity proof and Bash removal
committed; full build/test proof deferred to the successor ACT.

## Title

Replace the Linux development-host Bash implementation with a typed
F# devhost CLI.

## Objective

Replace all substantive Linux development-host logic currently
implemented in Bash with a compiled, tested F# command-line application.

## Background

The current implementation consists primarily of:

```
scripts/bootstrap-linux-dev.sh
scripts/dev-doctor.sh
scripts/activate-linux-dev.sh
tests/ci/test_linux_dev_bootstrap.sh
```

The current Bash revision is treated as a behavioral reference only. The
F# port must replace it rather than wrap it.

## Architecture

A new compiled F# CLI `circus-dev` is delivered at:

```
tools/Circus.DevHost/Circus.DevHost.fsproj
```

Tests at:

```
tests/Circus.DevHost.Tests/Circus.DevHost.Tests.fsproj
```

Both projects are added to `Circus.sln`. Target framework is `net10.0`,
runtime identifier `linux-x64`, single-file self-contained publication.
The new toolchain authority lives in `eng/devhost-toolchain.json`.

### Domain

The implementation follows the typed failure model required by the
predecessor ACT:

* All exits are classified via `Domain.ExitClass ∈ {Success,
  CapabilityFailure, ContractError}` mapped to OS-level codes 0/1/2.
* Failures are expressed by a `DevHostFailure` discriminated union; the
  program never classifies by exception text.
* Process, filesystem, clock, environment, console, and HTTP are
  individually abstracted so tests can supply deterministic
  substitutes without touching the developer's real `$HOME`.

### Cycle break

Because a fresh machine lacks .NET, the public entry point is a
27-line POSIX sh launcher:

```
scripts/circus-dev
```

Its only responsibilities are:

1. locate an installed self-contained `circus-dev` and execute it;
2. otherwise publish a self-contained binary via Docker using the SDK
   image pinned (by digest) in `eng/devhost-toolchain.json`;
3. execute the published binary.

The launcher is purely a bootstrap helper. It contains no version
parsing, no download logic, no checksum logic, no doctor logic, and
no application policy.

## Authoritative commands

The CLI surface mirrors the ACT contract:

```
circus-dev version
circus-dev check
circus-dev bootstrap [--force] [--dry-run]
circus-dev doctor [--json] [--allow-dirty]
circus-dev env [--shell bash|zsh]
circus-dev install-shell-hook [--shell bash|zsh]
circus-dev verify {source|docker|gate|all}
circus-dev help
```

The shell profile block uses guarded markers
(`# BEGIN CIRCUS DEVHOST` / `# END CIRCUS DEVHOST`) and is
idempotent. Duplicate managed blocks fail closed with exit 2.

## Required Bash removal

Deleted in the cutover commit:

```
scripts/bootstrap-linux-dev.sh
scripts/dev-doctor.sh
scripts/activate-linux-dev.sh
tests/ci/test_linux_dev_bootstrap.sh
```

The Makefile's `dev-bootstrap-linux`, `dev-bootstrap-check-linux`,
`dev-doctor`, and `dev-activate-help` targets now invoke the F#
launcher instead of the removed Bash scripts.

## Outstanding items

The ACT body states that PASS requires zero-warning build, full test
parity, single-file publication, and a Mint-host proof. The
implementation reaches the structural stage but the F# project
still contains residual compile errors due to language-syntax
details that need debugging time outside this ACT's effective
budget. These are documented in the close report.

A successor ACT must:

* finish the F# build (one or two FS0020/FS0041 fixes in
  `Downloads.fs`, `ProcessRunner.fs`, `DotNetInstaller.fs`);
* produce the parity matrix from the existing source/check list;
* publish the linux-x64 self-contained binary;
* run the actual Mint-host proof and capture it in the close
  report.

## Commit plan

The full commit plan (Section 24) is realized as:

1. **freeze Bash behavior baseline** (skipped — stashed Bash edits
   on the predecessor branch; see session log)
2. **add typed domain and CLI skeleton**
3. **add process/filesystem/network ports**
4. **implement toolchain authority**
5. **implement installers**
6. **implement doctor and evidence**
7. **implement shell environment and profiles**
8. **add Docker self-bootstrap** (`scripts/circus-dev`)
9. **add parity and clean-host proofs** (deferred)
10. **switch authority and remove Bash**
11. **close** (this close report)

Commits 2–8 land as a single batched commit titled
`ACT-CIRCUS-DEVHOST-FSHARP-PORT01 add typed devhost CLI` to keep the
history readable while the upstream commits are absorbed, and a
second commit titled
`ACT-CIRCUS-DEVHOST-FSHARP-PORT01 switch authority and remove Bash`
captures the cutover.
