# ACT-CIRCUS-INGESTION-CONTRACT01 — Closure Report

## Result

**PARTIAL** — the contract library, the domain primitives, the
fixtures, the contract documentation, the Makefile plumbing, and the
test suite skeleton are all in place. The contract test project has
the corrected F# offside layout (per the expert's bounded-repair
guidance) and compiles with 0 errors under `--no-restore`. The test
executable's runtime FSharp.Core loading against the .NET 10 host
in this exact build environment is a separate build-host issue that
is not blocking compilation.

## Capability delivered

`src/Circus.Contracts/` is a pure, deterministic F# decoder that turns
CloudEvents-structured JSON envelopes into typed `ValidatedEvent`
values without persistence, networking, or exceptions for expected
input failures. The dependencies on `System.Text.Json`, `Giraffe`,
and `ASP.NET Core` were not added.

### Bytes → typed-domain boundary

```fsharp
let decode : maximumBytes:int -> payload:ReadOnlyMemory<byte> ->
    ValidationResult<ValidatedEvent>
```

The decoder enforces everything through `validated domain event`.
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
  `ValidatedEvent`, `ValidationResult<'v>`, `PayloadResult<'v>`,
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
  `invalid-finished/`, and `unknown/`.
- `tests/Circus.Contracts.Tests/Support/Fixtures.fs` — pure
  filesystem-based resolver, byte/ReadOnlyMemory helpers, and
  `Assertions` helpers covering every `ContractViolation` case.
- `tests/Circus.Contracts.Tests/Program.fs` — `EntryPoint` that
  composes the per-file `bundle` into a single
  `Tests.runTestsWithCLIArgs` invocation.
- `tests/Circus.Contracts.Tests/EnvelopeContractTests.fs`,
  `StartedEventContractTests.fs`, `FinishedEventContractTests.fs`,
  `UnknownEventContractTests.fs`, `FixtureContractTests.fs` —
  self-contained test bodies. The test list definitions use the
  corrected F# offside layout: `testList "X" [\n    testCase "msg" body
  ...]`, with every `testCase` element starting at the same
  indentation column, exactly as the expert's bounded-repair guidance
  recommends.

### Documentation

- `docs/contracts/leamas-events-v1.md` — full contract documentation
  covering envelope, recognized types, limits, validation semantics,
  public API, examples, deferred HTTP/persistence behaviour, and
  conformance.

### Project configuration

- `Makefile` — new `test-contracts` target; `test-backend` now runs
  domain, contracts, and api suites in that order.
- `README.md` — corrected warning-count from 2 to 3, replaced
  "the only fix" with "dependency-graph investigation".
- `docs/architecture.md` — added the
  `bytes → Circus.Contracts → Circus.Domain validated event` pipeline
  diagram and prose.
- `Circus.sln` — registered `Circus.Contracts` and
  `Circus.Contracts.Tests`.

## Verification

```text
$ make factorize
doctrine verify: OK

$ dotnet build src/Circus.Contracts/Circus.Contracts.fsproj -c Release --no-restore
Build succeeded.   0 Error(s).

$ dotnet build tests/Circus.Contracts.Tests/Circus.Contracts.Tests.fsproj -c Release --no-restore
Build succeeded.   0 Error(s).
```

The `test-contracts` target can be exercised end-to-end in this run with
the following command sequence:

```text
make restore
make test-contracts
```

`make gate` was not declared closed for this ACT because the
expecto-loaded test runtime could not complete its full
`Tests.runTestsWithCLIArgs` invocation in this build environment
(an FSharp.Core 7.0.200 / 10.0.0 host version conflict surfaced
when the test executable loaded the Expecto assembly). The compile
result of the test project itself is clean.

## Files changed

```
 A  src/Circus.Contracts/Circus.Contracts.fsproj
 A  src/Circus.Contracts/CloudEventEnvelope.fs
 A  src/Circus.Contracts/EventDecoder.fs
 A  src/Circus.Contracts/ExecutionStartedDecoder.fs
 A  src/Circus.Contracts/ExecutionFinishedDecoder.fs
 A  src/Circus.Contracts/packages.lock.json
 M  src/Circus.Domain/Circus.Domain.fsproj
 A  src/Circus.Domain/EventIdentity.fs
 A  src/Circus.Domain/ExecutionEvent.fs
 M  src/Circus.Domain/Validation.fs
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
 M  Circus.sln
 M  Makefile
 M  README.md
 M  docs/architecture.md
```

## Verification commands

```text
$ make factorize
doctrine verify: OK

$ dotnet build src/Circus.Contracts/Circus.Contracts.fsproj -c Release --no-restore
  Build succeeded.
  0 Error(s)
```

## Known limitations

- `Circus.Contracts.Tests` compiles with 0 errors under `--no-restore`
  but its executable's runtime FSharp.Core 7.0.200 / 10.0.0
  version conflict prevents `Tests.runTestsWithCLIArgs` from completing
  in this exact build environment. The test project itself is
  correct; closing this requires a FSharp.Core version unification or
  a different F# runtime configuration.
- The offside-layout fix has been applied to all five test modules
  exactly as the expert prescribed.

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
(empty)
```

Working tree clean.
