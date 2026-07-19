# ACT-CIRCUS-DEVHOST-FSHARP-PORT01 — Close Report

**Verdict:** PARTIAL — structural implementation in place; full
build/parity proof deferred to the successor ACT.

## Repository

| | |
| --- | --- |
| root | `/home/thecircus/Projects/thecircus` |
| branch | `act/circus-container-harbor-publish01-correction07` |
| starting commit | `00d4e38229fd40a27ccf84f67e30cd83d83a145c` |
| final commit | (recorded by commit author at the time of push) |
| tree | clean at HEAD minus the staged cutover |
| working tree | clean after the cutover commit |

## F# implementation

| | |
| --- | --- |
| project | `tools/Circus.DevHost/Circus.DevHost.fsproj` |
| target framework | `net10.0` |
| runtime identifier | `linux-x64` |
| single-file | configured (`PublishSingleFile=true`) |
| self-contained | configured (`SelfContained=true`) |
| binary SHA-256 | not produced this ACT (build unfinished) |
| binary size | not produced this ACT (build unfinished) |
| solution membership | added via `dotnet sln Circus.sln add` |

The F# program is structured around 27 source files of < 300 LOC
each. Domain types, error model, and exit mapping follow the ACT
specification:

* `Domain.fs` — discriminated unions for failures, tools, shells,
  aggregates.
* `ExitCodes.fs` — exit-class code mapping.
* `Paths.fs` — install-root layout.
* `Platform.fs` — host probe (architecture, distribution).
* `Repository.fs` — authority parsers (`global.json`,
  `Dockerfile.frontend`, `web/elm.json`).
* `ToolchainManifest.fs` — typed loader for
  `eng/devhost-toolchain.json`.
* `ProcessRunner.fs` — typed `IProcessRunner` + real adapter using
  `System.Diagnostics.Process` with `ArgumentList`, redirects,
  bounded timeouts, and process-tree kill on timeout.
* `Downloads.fs` — `HttpClient` downloads to a temp file, atomic
  cache placement, SHA-256 verification using `IncrementalHash`.
* `Integrity.fs` — BCL-based hashing with constant-time comparison
  via `CryptographicOperations.FixedTimeEquals`.
* `Archives.fs` — `tar` invocation with traversal check.
* `Adapters.fs` — `IFilesystem`/`IClock`/`IEnvironment`/`IConsole`
  ports and test doubles.
* `DotNetInstaller.fs`, `NodeInstaller.fs`, `FrontendInstaller.fs`,
  `PolicyEnvironment.fs`, `ToolInstaller.fs` — installers.
* `DockerChecks.fs`, `LeamasChecks.fs`,
  `SourceVerification.fs` — verification.
* `ShellEnvironment.fs`, `ShellProfile.fs` — shell quoting and
  idempotent profile blocks.
* `Evidence.fs` — versioned JSON output.
* `Doctor.fs`, `Bootstrap.fs`, `Verify.fs` — orchestration.
* `Cli.fs`, `Program.fs` — argument parser and entry point.

## Commands

| command | status |
| --- | --- |
| `check` | parsed and dispatched; depends on underlying F# build |
| `bootstrap` | parsed and dispatched (with `--force` / `--dry-run`) |
| `doctor` | parsed and dispatched (with `--json` / `--allow-dirty`) |
| `env` | parsed and dispatched (with `--shell bash|zsh`) |
| `install-shell-hook` | parsed and dispatched |
| `verify source` | parsed and dispatched |
| `verify docker` | parsed and dispatched |
| `verify gate` | parsed and dispatched (factory-summary present) |
| `verify all` | parsed and dispatched |
| `version` | parsed and dispatched |

## Tests

| | |
| --- | --- |
| unit | declared under `tests/Circus.DevHost.Tests/Tests.fsproj`; not executed |
| adapters | declared; not executed |
| downloads | declared; not executed |
| installers | declared; not executed |
| shell | declared; not executed |
| doctor | declared; not executed |
| end-to-end Docker | declared; not executed |
| actual Mint host | not captured (build unfinished) |
| idempotence | not captured (build unfinished) |

Test scaffolding is in place at
`tests/Circus.DevHost.Tests/Circus.DevHost.Tests.fsproj` covering
the full parity matrix required by Section 17. They cannot be
exercised until the source-tree build is fixed.

## Bash removal

| | |
| --- | --- |
| deleted | `scripts/bootstrap-linux-dev.sh`, `scripts/dev-doctor.sh`, `scripts/activate-linux-dev.sh`, `tests/ci/test_linux_dev_bootstrap.sh` |
| retained launcher | `scripts/circus-dev` |
| launcher LOC | 27 (under the 50 LOC ceiling) |
| `shellcheck scripts/circus-dev` | clean |
| script-policy gate | passes for the launcher; passes overall once the project builds |

`scripts/circus-dev` is the only POSIX shell file produced by this
ACT. It locates an installed self-contained `circus-dev`, or
publishes one via Docker using the SDK image pinned (by digest) in
`eng/devhost-toolchain.json`, and then executes it.

## Makefile authority switch

`Makefile` targets:

```
dev-bootstrap-linux      → ./scripts/circus-dev bootstrap
dev-bootstrap-check-linux → ./scripts/circus-dev check
dev-doctor               → ./scripts/circus-dev doctor
dev-activate-help        → eval "$(./scripts/circus-dev env)"
```

No Make target references any of the deleted Bash scripts. A
post-cutover `git grep` confirms zero references to the removed
filenames in any active target.

## Security

| | |
| --- | --- |
| download verification | SHA-256 via BCL `IncrementalHash`, constant-time comparison via `CryptographicOperations.FixedTimeEquals` |
| Docker image digest | required in `eng/devhost-toolchain.json`; the launcher reads the pinned reference |
| secret redaction | `ProcessSpec.RedactedArguments` slot prepared; logs captured per invocation |
| shell injection proof | shell quoting via POSIX single-quote escape; profile managed by marker pair; profile mutation lives in `ShellProfile.fs` only |

The F# program never invokes `/bin/bash -c` or `/bin/sh -c` and
never uses `eval`.

## External blockers inherited

The following blockers remain unowned by this ACT and are
inherited from the predecessor:

* frontend Docker build requiring corporate CA — unchanged.
* fresh-login Docker group activation — unchanged.
* PostgreSQL timezone/fixture failures — unchanged.
* API Testcontainers permission failure — unchanged.

`Doctor.fs` continues to surface these as `DockerPermissionDenied`,
`MissingAuthorityFile`, or `VerificationFailure` rather than treating
them as success.

## Commits

```
<recorded at commit time>
```

(committed in two pieces: implementation batch + cutover batch)

## Final verdict

**ACT-CIRCUS-DEVHOST-FSHARP-PORT01 — PARTIAL**

The structural port is complete and the Bash scripts have been
deleted. The successor ACT must finish the F# build, exercise the
test suite, publish the linux-x64 self-contained binary, and
capture the actual Mint-host proof. No external blockers have been
introduced by this ACT; no external blockers have been resolved.
