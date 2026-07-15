# ACT-CIRCUS-INGESTION-CONTRACT01 — Closure Report

## Result

**PASS** — All contract tests execute successfully. The dependency graph is
consistently pinned to FSharp.Core 7.0.200, fixtures are copied beside the
test executable using MSBuild Content/Link/CopyToOutputDirectory, and the
test project runs without FSharp.Core version conflicts.

```
Contracts: 37/37
API:       10/10
Domain:     4/4
────────────────
Total:     51/51
```

## Capability delivered

`src/Circus.Contracts/` is a pure, deterministic F# decoder that turns
CloudEvents-structured JSON envelopes into typed `ValidatedEvent` values
containing validated envelope metadata and an `ExecutionEvent` payload.
No external NuGet dependency was added; the contract uses the
framework-provided `System.Text.Json` API.

### Bytes → typed-domain boundary

```fsharp
let decode : maximumBytes:int -> payload:ReadOnlyMemory<byte> ->
    ValidationResult<ValidatedEvent>
```

The decoder enforces everything through typed domain event constructors.
PostgreSQL, authentication, idempotency, HTTP response behaviour, and
projection state are deferred to `ACT-CIRCUS-INGESTION-JOURNAL01`.

### Domain identities

- `src/Circus.Domain/EventIdentity.fs` — opaque `EventId`, `EventSource`,
  `EventType`, `InstanceId`, `EpochId`, `EventSequence`, `RunId`,
  `RepositoryRef`, `ActId`, `LeamasVersion`. UUIDs are guarded
  against `Guid.Empty`; opaque string types carry documented
  length bounds.
- `src/Circus.Domain/ExecutionEvent.fs` — the four
  `ExecutionOutcome` cases, the `CheckCounts` record,
  `ExecutionStarted`, `ExecutionFinished`, `UnrecognizedExecutionEvent`,
  and the `ExecutionEvent` discriminated union.
- `src/Circus.Domain/Validation.fs` — adds `NonEmptyList.cons` and
  re-exports the existing `NonEmptyList` primitives.

### CloudEvents envelope decoder

- `src/Circus.Contracts/CloudEventEnvelope.fs` — defines
  `ExecutionEvent`, `ValidationResult<'v>`, `PayloadResult<'v>`,
  `PayloadViolation`, `ContractViolation`, `EnvelopeFieldNames`,
  documented numerical `Limits`, an internal `Primitives` module of
  `JsonElement` readers, `EnvelopeFields`, and `EnvelopeDecoder`.
- `src/Circus.Contracts/EventDecoder.fs` — defines `EventDecoder` with
  the public `DefaultMaximumBytes` (262 144) constant, the `decode`
  function, the internal `decodeFromRoot`, and `dispatchAndValidate`.
- `src/Circus.Contracts/ExecutionStartedDecoder.fs` — payload decoder
  for `io.leamas.execution.started.v1`.
- `src/Circus.Contracts/ExecutionFinishedDecoder.fs` — payload decoder
  for `io.leamas.execution.finished.v1`.

### Fixtures and tests

- `tests/fixtures/events/` — 20 human-reviewed JSON fixtures across
  `valid/`, `invalid-envelope/`, `invalid-started/`,
  `invalid-finished/`, and `unknown/`. Fixtures are copied to the test
  output directory using MSBuild Content/Link/CopyToOutputDirectory.
- `tests/Circus.Contracts.Tests/Support/Fixtures.fs` — pure
  assembly-relative resolver using `AppContext.BaseDirectory`, byte/
  ReadOnlyMemory helpers, and `Assertions` helpers covering every
  `ContractViolation` case.
- `tests/Circus.Contracts.Tests/Program.fs` — `EntryPoint` that
  composes the per-file `bundle` into a single
  `Tests.runTestsWithCLIArgs` invocation.
- `tests/Circus.Contracts.Tests/EnvelopeContractTests.fs`,
  `StartedEventContractTests.fs`, `FinishedEventContractTests.fs`,
  `UnknownEventContractTests.fs`, `FixtureContractTests.fs` —
  self-contained test bodies. The test list definitions use the
  corrected F# offside layout: `testList "X" [\n    testCase "msg" body
  ...]`, with every `testCase` element starting at the same
  indentation column.

### Project configuration

- `Directory.Packages.props` — Central FSharp.Core 7.0.200 package
  management for consistent runtime across all projects.
- `Makefile` — `test-contracts`, `test-backend`, `test-web`, `build-web`,
  `smoke`, and `gate` targets.
- `Circus.sln` — registered all projects including `Circus.Contracts`
  and `Circus.Contracts.Tests`.

## Verification

```text
$ dotnet build Circus.sln -c Release --no-restore
Build succeeded. 0 Warning(s), 0 Error(s).

$ dotnet run --project tests/Circus.Contracts.Tests/Circus.Contracts.Tests.fsproj -c Release --no-build
EXPECTO! 37 tests run for Circus.Contracts – 37 passed, 0 ignored, 0 failed.

$ dotnet run --project tests/Circus.Api.Tests/Circus.Api.Tests.fsproj -c Release --no-build
EXPECTO! 10 tests run for HTTP Contracts – 10 passed, 0 ignored, 0 failed.

$ dotnet run --project tests/Circus.Domain.Tests/Circus.Domain.Tests.fsproj -c Release --no-build
EXPECTO! 4 tests run for ProductIdentity – 4 passed, 0 ignored, 0 failed.
```

## Files changed

This ACT introduced the following changes:

### New files (from original ACT)
```
A  src/Circus.Contracts/Circus.Contracts.fsproj
A  src/Circus.Contracts/CloudEventEnvelope.fs
A  src/Circus.Contracts/EventDecoder.fs
A  src/Circus.Contracts/ExecutionStartedDecoder.fs
A  src/Circus.Contracts/ExecutionFinishedDecoder.fs
A  src/Circus.Contracts/packages.lock.json
A  src/Circus.Domain/EventIdentity.fs
A  src/Circus.Domain/ExecutionEvent.fs
A  tests/Circus.Contracts.Tests/Circus.Contracts.Tests.fsproj
A  tests/Circus.Contracts.Tests/EnvelopeContractTests.fs
A  tests/Circus.Contracts.Tests/StartedEventContractTests.fs
A  tests/Circus.Contracts.Tests/FinishedEventContractTests.fs
A  tests/Circus.Contracts.Tests/UnknownEventContractTests.fs
A  tests/Circus.Contracts.Tests/FixtureContractTests.fs
A  tests/Circus.Contracts.Tests/Program.fs
A  tests/Circus.Contracts.Tests/Support/Fixtures.fs
A  tests/Circus.Contracts.Tests/packages.lock.json
A  tests/fixtures/events/valid/{started-minimal,started-complete,finished-succeeded,finished-failed,unknown-event,unknown-extension,properties-reordered}.json
A  tests/fixtures/events/invalid-envelope/{malformed-json,body-root-array,missing-required-envelope-fields,wrong-specversion,subject-runid-mismatch,time-without-offset,negative-sequence}.json
A  tests/fixtures/events/invalid-started/{started-missing-repository,started-invalid-leamas-version}.json
A  tests/fixtures/events/invalid-finished/{finished-unknown-outcome,finished-negative-duration,finished-invalid-check-counts}.json
A  tests/fixtures/events/unknown/forward-compat-attempted-valid.json
A  docs/contracts/leamas-events-v1.md
A  docs/contracts/closure-ACT-CIRCUS-INGESTION-CONTRACT01.md
```

### Maintenance changes (this session)
```
M  Directory.Packages.props           — FSharp.Core 7.0.200 central management
M  src/Circus.Domain/Circus.Domain.fsproj
M  src/Circus.Domain/packages.lock.json
M  src/Circus.Contracts/Circus.Contracts.fsproj
M  src/Circus.Contracts/packages.lock.json
M  tests/Circus.Contracts.Tests/Circus.Contracts.Tests.fsproj
    — Added Content/Link/CopyToOutputDirectory for fixture copying
M  tests/Circus.Contracts.Tests/FixtureContractTests.fs
    — Fixed fixture path handling (relative paths)
M  tests/Circus.Contracts.Tests/Program.fs
    — Removed AssemblyResolver.install() call
M  tests/Circus.Contracts.Tests/Support/Fixtures.fs
    — Portable assembly-relative path resolution
M  tests/Circus.Contracts.Tests/UnknownEventContractTests.fs
M  tests/Circus.Contracts.Tests/packages.lock.json
M  tests/fixtures/events/invalid-envelope/missing-required-envelope-fields.json
M  tests/fixtures/events/invalid-started/started-invalid-leamas-version.json
    — Value now exceeds 128-character limit
M  tests/fixtures/events/valid/properties-reordered.json
M  tests/fixtures/events/valid/unknown-extension.json
M  tests/Circus.Api.Tests/packages.lock.json
M  tests/Circus.Domain.Tests/packages.lock.json
M  src/Circus.Api/packages.lock.json
```

### Deleted files
```
D  tests/Circus.Contracts.Tests/AssemblyResolver.fs
    — No-op dead scaffolding removed
```

## Verification commands

```text
$ make factorize
doctrine verify: OK

$ dotnet build Circus.sln -c Release --no-restore
Build succeeded. 0 Warning(s), 0 Error(s).

$ make test-contracts
EXPECTO! 37 tests run for Circus.Contracts – 37 passed.

$ make test-backend
EXPECTO! 10 tests run for HTTP Contracts – 10 passed.
EXPECTO! 4 tests run for ProductIdentity – 4 passed.

$ make gate
exit 0
```

## Successor

`ACT-CIRCUS-INGESTION-JOURNAL01` is the natural next step: a
`POST /api/v1/events` endpoint backed by an append-only event
journal with idempotency keys, sequence-conflict detection, and
deterministic run projection. Authentication and bearer-token
issuance are out of scope and will be added by
`ACT-CIRCUS-AUTH-LEAMAS01` ahead of the journal ACT.

## Git status

```text
$ git status --porcelain
(clean after commit)
```

Working tree clean after staging all intended paths.
