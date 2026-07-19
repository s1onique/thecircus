# ACT-CIRCUS-DEVHOST-FSHARP-PORT01 Progress

## Phase 0: Safety / Baseline
- [x] Record initial repo state
- [x] Stash dirty Bash edits
- [x] Capture starting commit (00d4e38)
- [ ] Freeze Bash behavior baseline commit

## Phase 1: F# project skeleton + domain types
- [ ] Circus.DevHost.fsproj + Tests project
- [ ] Domain.fs (types: Tool, Failure, CheckStatus, Shell, etc.)
- [ ] ExitCodes.fs
- [ ] Paths.fs
- [ ] Platform.fs
- [ ] Repository.fs (resolves Git paths, identity)
- [ ] Cli.fs (argument parser)
- [ ] Program.fs (entry point wiring)

## Phase 2: Toolchain authority
- [ ] eng/devhost-toolchain.json (actionlint/shellcheck, python policy, sdk image)
- [ ] ToolchainManifest.fs (loader/reconciliation)

## Phase 3: Adapters
- [ ] ProcessRunner.fs (typed Process wrapper)
- [ ] Downloads.fs (HttpClient + temp file + atomic move)
- [ ] Integrity.fs (SHA-256/512, constant-time compare)
- [ ] Archives.fs (tar xJ/xz extraction, traversal protection)
- [ ] FileSystem port + Clock + Env port + Console port

## Phase 4: Installers
- [ ] DotNetInstaller.fs (downloads linux-x64 SDK from release metadata)
- [ ] NodeInstaller.fs (downloads official SHASUMS archive)
- [ ] FrontendInstaller.fs (npm ci in web/, locks Elm version)
- [ ] PolicyEnvironment.fs (venv + pinned pip/PyYAML)
- [ ] ToolInstaller.fs (actionlint, ShellCheck)

## Phase 5: Docker / Leamas
- [ ] DockerChecks.fs (binary, daemon, buildx, compose)
- [ ] LeamasChecks.fs (binary, version, factory digest)

## Phase 6: Doctor / Bootstrap / Verify
- [ ] SourceVerification.fs (dotnet restore + build)
- [ ] Bootstrap.fs (planning + execution)
- [ ] Doctor.fs (read-only fail-closed)
- [ ] Evidence.fs (JSON schema)

## Phase 7: Shell environment + Profile
- [ ] ShellEnvironment.fs (bash + zsh quoting, PATH dedup)
- [ ] ShellProfile.fs (idempotent block management)

## Phase 8: Bootstrap cycle break
- [ ] scripts/circus-dev (≤50 LOC POSIX sh launcher)
- [ ] Docker self-publish inside F# program (fallback)

## Phase 9: Tests
- [ ] TestDoubles.fs
- [ ] Pure unit tests
- [ ] Adapter tests
- [ ] Process tests (fake PATH executables)
- [ ] Download tests (HTTP server)
- [ ] Installer tests
- [ ] Shell env / profile tests
- [ ] Bootstrap tests
- [ ] Doctor tests
- [ ] CLI tests

## Phase 10: Makefile authority switch
- [ ] Replace dev-* targets

## Phase 11: Bash removal + docs
- [ ] Delete Bash scripts
- [ ] docs/acts/ACT-CIRCUS-DEVHOST-FSHARP-PORT01.md
- [ ] docs/architecture/devhost.md
- [ ] docs/linux-development.md updated
- [ ] docs/close-reports/ACT-CIRCUS-DEVHOST-FSHARP-PORT01.md

## Phase 12: Validation
- [ ] Single-file linux-x64 self-contained binary built
- [ ] Clean-host proof
- [ ] Idempotence proof
- [ ] shellcheck scripts/circus-dev clean
- [ ] wc -l scripts/circus-dev ≤ 50
- [ ] No grep hits to deleted scripts
