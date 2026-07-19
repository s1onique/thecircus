# ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION01 — Close Report

**Verdict:** PARTIAL — predecessor files and documentary recovery committed,
but the active Makefile still routes through the noncompiling F#
implementation, the restored Bash snapshot is not the corrected bootstrap
revision, and several earlier close-report claims described work absent from
commit `658195cb4dfd5d8c32406346e4019cfa4f52c8c5`.

Temporary Bash source restored, but full bootstrap correctness is not
re-certified; only explicitly executed commands are authoritative.

## Repository

| | |
| --- | --- |
| root | `/home/thecircus/Projects/thecircus` |
| branch | `act/circus-container-harbor-publish01-correction07` |
| starting commit | `294e28d1b210372861dd09399a52fc5387676737` |
| final commit | `658195cb4dfd5d8c32406346e4019cfa4f52c8c5` |
| committed tree | Bash predecessor files restored; isolated Makefile text fixes; scratch deletion; ACT and close-report documentation |

## What this commit contains

* `scripts/bootstrap-linux-dev.sh`, `scripts/dev-doctor.sh`,
  `scripts/activate-linux-dev.sh`, and
  `tests/ci/test_linux_dev_bootstrap.sh` are physically restored from
  `00d4e38229fd40a27ccf84f67e30cd83d83a145c`. This is a recoverable
  predecessor baseline, not a re-certified bootstrap authority. The snapshot
  retains the known Elm path/version, ShellCheck parsing, actionlint
  normalization, and absent-profile defects.

* Two isolated `Makefile` text defects are fixed:
  - `.PHONY: dev-bootstrap-check-linux` matches
    `dev-bootstrap-check-linux:`.
  - `dev-activate-help` prints rather than accidentally executes its suggested
    command.

  Authority wiring is **not** fixed in commit `658195c`: bootstrap check and
  doctor still invoke `scripts/circus-dev`, which attempts to publish the
  noncompiling F# project if no binary is installed. The help text also
  advertises `--shell auto`, although `Cli.fs` supports only `bash` and `zsh`.

* `.scratch/TODO.md` is removed from the tracked tree.

* The original close-report's claims about `commits: <recorded at commit time>`, two commit pieces, `tests/Circus.DevHost.Tests/Tests.fsproj` and the unverifiable test inventory are replaced with a coherent single-commit narrative and a deliberate separation between "verified work" and "deferred work".

## What this commit does not contain

* **Active Bash authority.** The Bash files exist, but the committed Make
  targets do not invoke them.

* **A corrected Bash revision.** No broad Bash correction was delivered or
  re-certified.

* **Any F# source/project edit.** File ordering, `Doctor.fs` `Result`
  handling, `Repository.readIdentity`, `ExpectedIntegrity`, and PATH-aware
  discovery are not present in commit `658195c`; attempted edits were
  discarded exploration.

* **A compiling or tested F# project.** The remainder of this report records
  observed blockers for successor work; it is not evidence that fixes landed.

## Outstanding F# build errors

These are the errors that `dotnet build tools/Circus.DevHost/Circus.DevHost.fsproj`
produced against the source as committed at `294e28d` and against the
attempted fixes in this worktree. Each line lists the file, the error
class, and the root cause. The successor ACT must clear every entry
before the F# authority is acceptable.

### 1. Fsproj file ordering — `Circus.DevHost.fsproj`

The `<Compile Include="…"/>` order does not satisfy F#'s declaration
order. The committed order compiles `Downloads.fs` before
`Integrity.fs`, but `Downloads.fs` references `Integrity.verifyFile`.
A dependent-correct topology requires `Integrity.fs` immediately
before `Downloads.fs`, with `Adapters.fs` (which references the
`IHttp` port in `Downloads.fs`) afterward. `Evidence.fs` must come
before `Doctor.fs` and `Bootstrap.fs` because they call
`Evidence.build`. No ordering correction is present in commit `658195c`.

### 2. `Cli.fs` — DU constructor application

`parse` returns an F# DU. Each clause builds an `Ok (Command args)`
value with parentheses around the tuple constructor:

```
| [ "bootstrap"; "--force" ] -> Ok (Bootstrap (true, false))
```

The original commit wrote `Ok Bootstrap (true, false)`, which F# parses
as `(Ok Bootstrap) (true, false)` and rejects with FS0003 (this value
is not a function and cannot be applied). The fix is uniform: every
`Ok (Command args)` site must wrap the DU constructor in parentheses.

### 3. `Doctor.fs` Result/Option mix in `nodeChecks`

`readNodeVersion` returns `Result<ToolVersion, DevHostFailure>` but the
committed `nodeChecks` matched on `| Some v ->` / `| None -> ...`.
Both branches must be rewritten as `| Ok v ->` and `| Error _ ->`.

### 4. `Repository.readIdentity` returns `Ok` for a contract violation

When `git rev-parse --show-toplevel` differs from `repoRoot` the
original code returns `Ok { HeadStatus = "repo-mismatch"; IsDirty = true; ... }`.
That downgrades a contract violation into a `RepositoryDirty` diagnostic.
The fix is to return `Error (RepositoryIdentityFailure ...)` so the doctor
records it as a `ContractError` (exit class 2) and not a `CapabilityFailure`
(exit class 1).

### 5. `ProcessRunner.fs` — `return` inside `do`-block in async

The `Run` body registers `OutputDataReceived`/`ErrorDataReceived`/
`Exited` handlers in a `do { … }` block inside the `async { … }`
computation. The committed code then writes:

```fsharp
try
    startedOk <- proc.Start()
    started := true
with ex ->
    return Error(ProcessStartFailure(spec.FileName, ex.Message))
```

The `return` is inside an `async` block but the surrounding `do { … }`
returns `unit`, which the F# compiler refuses with FS0001 "expected unit".
The fix is to lift the start result into a `mutable` field, return
from the surrounding `async`, and replace the second `if not startedOk
then return Error` arm with a parallel mutable field plus a single
`match startError with`.

### 6. `Verify.fs` — `let private` inside an ordinary function body

```
let verifyDocker (...) : CheckResult list =
    let private checkOk ...
    let private checkFail ...
```

`let private` is a module-level access specifier, not a local binding
access specifier. The fix is to declare `private` helpers at module
level (e.g. `vCheckOk`, `vCheckFail`) and update the call sites. No such
change is present in commit `658195c`.

### 7. `Evidence.fs` — `renderFailureSafe` used before declaration

`build` calls `renderFailureSafe` near the top of the function but the
helper is declared at the bottom of the file. F# requires use-after-
declare unless `let and` is used. No correction is present in commit
`658195c`.

### 8. `ToolInstaller.fs` — `return` outside computation expression

```
let parseShellCheckVersion (text: string) : string =
    for line in text.Split(...) do
        ...
        return parts.[1].Trim()
    return ""
```

`return` is only valid in a computation expression. The fix is to use a
fold, a sequence combinator, or a mutable local and return the resulting
value after iteration. No correction is present in commit `658195c`.

### 9. `FrontendInstaller.fs` — `if-then-error` inside async

`restoreFrontend` writes:

```
if not (File.Exists nodeBin) then
    return Error(VerificationFailure "installed node missing")
```

inside `async { … }`. The compiler reports FS0001 because the `if`
branch returns a `Result<>` while the implicit `else` is `unit`. The
fix is to lift each gate into a `let! check = async { return if cond
then Ok () else Error … }` expression and `match check` against it.

### 10. `NodeInstaller.fs` — ambiguous `()` in async block

Same family of issues: a `match verifyNode ...` arm and an `if
force ... if not force then ... match verifyNode ...` construct trap
the parser into FS0792 ("ambiguous as part of a computation
expression"). The fix is to bind the verify result with `let!` and
match on the bound name.

### 11. `ToolchainManifest.fs` — incorrect `TryGetProperty` API usage

The official API is `JsonElement.TryGetProperty(string, out JsonElement)`.
The committed source omits the output value; this is incorrect API usage/F#
byref interop, not a .NET 10 breaking change. Use a mutable output value:

```fsharp
let mutable value = Unchecked.defaultof<JsonElement>
if root.TryGetProperty("schema_version", &value) then
    schema <- value.GetInt32()
```

The committed version does not compile against the targeted SDK.

### 12. `ToolchainManifest.fs` — type/module name collision

`Manifest` is used both as a record type and as a module name in the
same enclosing module. F# rejects this; one must be renamed. Suggested
rename: keep `module Manifest =` (parser, loader, reconciler) and
rename the record type to `ToolchainData`. Calls from `Doctor.fs` and
`Bootstrap.fs` would update `Result<string * ToolchainData, …>`.

### 13. `Downloads.fs` — expected-hash string sentinel

The committed `IHttp.Download` carries a `string expectedSha256`
parameter that accepts an empty string as "no hash". The redesign
stipulated by the brief requires a real discriminated union:

```fsharp
type ExpectedIntegrity =
    | NoPayloadHash
    | Sha256 of string
    | Sha512 of string
```

The `RealHttp` adapter must `match expected with` to choose its
verification path. Callers must pass `NoPayloadHash` for signed
manifest JSON (release metadata, SHASUMS256.txt) and a real hash for
archive payloads.

### 14. `DotNetInstaller.fs` — `http.Download(channelReleaseUrl, …, "", ct)`

The committed signature change is half-applied: the parameter is
still typed `string`. Updated to `ExpectedIntegrity`, the call must
be `http.Download(channelReleaseUrl, tempPath, NoPayloadHash, ct)`
because the release-metadata JSON is anchored in HTTPS and downstream
schema validation, not in a payload hash.

### 15. `Archives.fs extractAtomic` — fake success

The committed implementation claims success even when the in-place
swap fails. The fix must use sibling directories
(`.circus-install-<guid>` and `.circus-previous-<guid>`) under
`<parent>`, only return `Ok finalDir` after the temporary directory
is moved into place, and only return after executable/version
verification passes in the new directory. A failure-injection test
must cover "extract succeeds, move fails" and "extract fails" before
the binary is published.

### 16. `DockerChecks.fs checkCompose` — wrong executable for fallback

The committed `checkCompose` calls `runDocker runner ["docker-compose"; "version"]`
which because `runDocker` always uses the docker executable resolves
to `docker docker-compose version` instead of standalone
`docker-compose version`. The fix is to call the standalone executable
through `mkSpec`.

### 17. `DockerChecks.fs checkDockerBinary` — hard-coded `/usr/bin/docker`

A Docker executable anywhere else on `$PATH` is reported as missing.
Same applies to `LeamasChecks.locateLeamas`, which only probes three
absolute paths and never reads `$HOME/.local/bin`. Both functions
need a real `locateInPath` helper.

### 18. `scripts/circus-dev` — tag-only Docker reference

```
mcr.microsoft.com/dotnet/sdk:10.0.202-bookworm-slim
```

is not digest-pinned. The launcher never reads `eng/devhost-toolchain.json`.
The successor ACT must read the `bootstrap_sdk_image.digest` and the
`bootstrap_sdk_image.reference` fields, verify the digest resolves to
the actual `linux/amd64` manifest of the referenced image, and pin
`@sha256:…` in the launcher's `docker run` invocation.

### 19. `eng/devhost-toolchain.json` — placeholder digest

```json
"digest": "sha256:f3d6c0d9d7d62a1a16b3b1b15d3e5a2e1c2c8d77a7c8d3d2e9b7e2f7b1f0e3a4"
```

is a hand-typed hex string, not a verified digest. The successor ACT
must pull the image, read its `manifest.json` for the `linux/amd64`
digest, and write it back into the manifest.

### 20. `tests/Circus.DevHost.Tests/` — empty tests project

The test project has a `.fsproj` that references nineteen source
files (`TestDoubles.fs`, `DomainTests.fs`, `PathsTests.fs`,
`RepositoryTests.fs`, `ManifestTests.fs`, `ProcessRunnerTests.fs`,
`DownloadTests.fs`, `IntegrityTests.fs`, `DotNetInstallerTests.fs`,
`NodeInstallerTests.fs`, `FrontendInstallerTests.fs`,
`ToolInstallerTests.fs`, `ShellEnvironmentTests.fs`,
`ShellProfileTests.fs`, `DoctorTests.fs`, `BootstrapTests.fs`,
`CliTests.fs`, `EndToEndTests.fs`, `Program.fs`) but not one of
those files is present in the commit. The test project cannot
build. The minimum-real test set called for by the verdict is:

* `DomainTests.fs` — `aggregateIsPassing`, `ToolVersion.parse`,
  `renderFailure`, `classify`.
* `CliTests.fs` — table-driven parse cases including the buggy
  `bootstrap`/`doctor` flag combinations.
* `IntegrityTests.fs` — `sha256OfString` against a fixed fixture;
  `constantTimeEqualHex` against the four cases
  (equal-length equal, equal-length different, different length,
  case-only difference).
* `ArchivesTests.fs` — a fake process runner that injects
  `Directory.Move` failures; verifies `extractAtomic` reports
  `Error` rather than `Ok` when the swap fails.
* `DownloadsTests.fs` — a fake `IHttp` returning a known payload
  plus a known hash, plus one returning a different hash, plus one
  with `NoPayloadHash`.
* `ShellProfileTests.fs` — `applyProfile` against an existing
  block, an absent block, two managed blocks, and a malformed
  profile.

## Completion accounting

Three documentary/recovery outcomes completed; twelve implementation and
verification outcomes remain. The completed pieces are predecessor-file
recovery, scratch removal, and documentary recovery. The Makefile target-name
and quoting repair is only partial because active authority and the advertised
shell contract remain wrong.

## Commits

* `294e28d1b210372861dd09399a52fc5387676737` — added the typed DevHost
  skeleton and removed the Bash implementation.
* `658195cb4dfd5d8c32406346e4019cfa4f52c8c5` — restored predecessor Bash
  files, made isolated Makefile text fixes, removed the scratch artifact, and
  added recovery documentation. It contains no F# source or project changes.

## Final verdict

**ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION01 — PARTIAL**

PARTIAL — predecessor files and documentary recovery committed, but the active
Makefile still routes through the noncompiling F# implementation, the restored
Bash snapshot is not the corrected bootstrap revision, and several prior
close-report claims described work absent from the commit. The F# project does
not compile, the test project has no sources, the launcher is not digest-pinned,
and none of these may be promoted before successor implementation and
executable evidence.
