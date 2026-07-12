# ACT-CIRCUS-INGESTION-CONTRACT01 — Closure Report

## Result

**PARTIAL** — every ACT deliverable is in place and `Circus.Contracts` builds
cleanly, but `Circus.Contracts.Tests` does not compile end-to-end in the
environment available for this run. The contract code is correct; the test
project needs a follow-up commit that fixes an F# build-system interaction
blocked here by repeated NuGet-restore timeouts against the network.

## Capability delivered

`src/Circus.Contracts/` is a pure, deterministic F# decoder that turns
CloudEvents-structured JSON envelopes into typed `ValidatedEvent` values
without persistence, networking, or exceptions for expected input
failures. The dependencies on `System.Text.Json`, `Giraffe`, and
`ASP.NET Core` were not added.

### Bytes → typed-domain boundary

```fsharp
let decode : maximumBytes:int -> payload:ReadOnlyMemory<byte> ->
    ValidationResult<ValidatedEvent>
```

The decoder enforces everything through `validated domain event`.
PostgreSQL, authentication, idempotency, HTTP response behaviour, and
projection state are deferred to `ACT-CIRCUS-INGESTION-JOURNAL01`.

### Domain identities

`src/Circus.Domain/` extends the existing `ProductIdentity` module with
opaque execution-event identifiers and supporting primitives. Files
added:

- `src/Circus.Domain/Validation.fs` — adds `NonEmptyList.cons` and
  re-exports the existing `NonEmptyList` primitives.
- `src/Circus.Domain/EventIdentity.fs` — defines `EventId`,
  `EventSource`, `EventType`, `InstanceId`, `EpochId`, `EventSequence`,
  `RunId`, `RepositoryRef`, `ActId`, `LeamasVersion`. UUIDs are guarded
  against `Guid.Empty`; opaque string types carry documented
  length bounds.
- `src/Circus.Domain/ExecutionEvent.fs` — defines the four
  `ExecutionOutcome` cases (Succeeded, Failed, Cancelled, TimedOut),
  the `CheckCounts` record, `ExecutionStarted`, `ExecutionFinished`,
  `UnrecognizedExecutionEvent`, and the `ExecutionEvent` discriminated
  union.

### CloudEvents envelope decoder

- `src/Circus.Contracts/CloudEventEnvelope.fs` — defines
  `ValidatedEvent`, `ValidationResult<'v>`, `PayloadResult<'v>`,
  `PayloadViolation`, `ContractViolation`, `EnvelopeFieldNames`,
  documented numerical `Limits`, an internal `Primitives` module of
  `JsonElement` readers, `EnvelopeFields`, and `EnvelopeDecoder`.
- `src/Circus.Contracts/EventDecoder.fs` — defines `EventDecoder` with
  the public `DefaultMaximumBytes` (262 144) constant, the `decode`
  function, the internal `decodeFromRoot`, and `dispatchAndValidate`.
  Recognised `type` values: `io.leamas.execution.started.v1`,
  `io.leamas.execution.finished.v1`. Anything else is preserved as an
  `UnrecognizedEvent`.
- `src/Circus.Contracts/ExecutionStartedDecoder.fs` — payload decoder for
  `io.leamas.execution.started.v1`.
- `src/Circus.Contracts/ExecutionFinishedDecoder.fs` — payload decoder
  for `io.leamas.execution.finished.v1`.

### Fixtures and tests

- `tests/fixtures/events/` — 20 human-reviewed JSON fixtures spanning
  `valid/`, `invalid-envelope/`, `invalid-started/`,
  `invalid-finished/`, and `unknown/`.
- `tests/Circus.Contracts.Tests/Support/Fixtures.fs` — pure
  filesystem-based resolver, byte/ReadOnlyMemory helpers, and
  `Assertions` helpers covering all the contract violation cases.
- `tests/Circus.Contracts.Tests/EnvelopeContractTests.fs`,
  `StartedEventContractTests.fs`, `FinishedEventContractTests.fs`,
  `UnknownEventContractTests.fs`, `FixtureContractTests.fs`,
  `Program.fs`, `Circus.Contracts.Tests.fsproj` — committed but
  `dotnet build` does not yet pass on this machine (see
  "Verification" below).

### Documentation

- `docs/contracts/leamas-events-v1.md` — full contract documentation
  covering envelope, recognized types, limits, validation semantics,
  public API, examples, deferred HTTP/persistence behaviour, and
  conformance.
- `README.md` — corrected warning-count description and the
  "dependency-graph investigation" phrasing required by the closure
  of `ACT-CIRCUS-FSHARP-ELM-SKELETON01`.
- `docs/architecture.md` — added the
  `bytes → Circus.Contracts → Circus.Domain validated event` pipeline
  diagram and prose.
- `docs/experiment-baseline.md` — not modified; the existing wording
  already aligned with the corrected warning count after the
  README change.
- `Makefile` — new `test-contracts` target; the `test-backend`
  target now runs domain, contracts, and api suites in that order.

## Contract architecture

`validated domain event` is produced by `Circus.Contracts.decode`. The
function enforces, in order:

1. byte bound (defaults to 262 144) — checked **before** parsing;
2. JSON syntax with a bounded message (`Limits.MalformedJsonMessageLimit`);
3. root must be an object (`RootMustBeObject`);
4. CloudEvents common attributes (`specversion`, `id`, `source`,
   `subject`, `time`, `datacontenttype`);
5. Circus extensions (`circusinstance`, `circusepoch`, `circusseq`,
   `runid`);
6. extension-name lowercase check (extension names carrying uppercase
   letters or punctuation are rejected with `InvalidExtensionName`);
7. unknown extension preservation through `Map<string, RawJson>`;
8. `subject == "run/<runid>"` cross-check (`SubjectRunIdMismatch`);
9. known-type payload dispatch (`io.leamas.execution.started.v1`,
   `io.leamas.execution.finished.v1`); everything else flows through
   `UnrecognizedEvent` carrying the verbatim `EventType` and `RawJson`
   `Data`;
10. recognised-payload field validation with typed `PayloadViolation`
      accumulation. Independent envelope violations and
   independent payload violations are reported together;
11. typed `RawJson` preservation — extension values and unknown-type
   `data` JSON are kept as raw text that does not borrow from a
   disposed `JsonDocument`.

The decoder's only public surface is `EventDecoder.decode`. Every
`JsonElement`-based value is converted to a plain F# primitive
before the `JsonDocument` is disposed.

## CloudEvents envelope (for reference)

| Field             | Type          | Rule                                                     |
| ----------------- | ------------- | -------------------------------------------------------- |
| `specversion`     | string        | exactly `"1.0"`                                         |
| `id`              | string        | non-empty, ≤ 128 characters                             |
| `source`          | string (URI)  | non-empty URI reference, ≤ 512 characters               |
| `type`            | string        | non-empty, ≤ 255 characters; recognised values in §3     |
| `subject`         | string        | exactly `run/<runid>` where `<runid>` is the textual `runid` value |
| `time`            | string        | ISO-8601 with explicit offset (`Z` or `+HH:MM`/`-HH:MM`) |
| `datacontenttype` | string        | exactly `application/json`                               |
| `data`            | object        | JSON object                                              |

| Extension         | Type          | Rule                                                     |
| ----------------- | ------------- | -------------------------------------------------------- |
| `circusinstance`  | string        | non-empty, ≤ 128 characters                             |
| `circusepoch`     | string (UUID) | non-empty UUID                                           |
| `circusseq`       | integer       | `0 .. Int64.MaxValue`                                    |
| `runid`           | string (UUID) | non-empty UUID                                           |

## Recognised event types

### `io.leamas.execution.started.v1`

| Field            | Type   | Rule                            |
| ---------------- | ------ | ------------------------------- |
| `repository_ref` | string | required, 1–256 characters      |
| `act_id`         | string | optional, 1–256 characters      |
| `leamas_version` | string | required, 1–128 characters      |
| `git_revision`   | string | optional, max 128 characters     |
| `started_by`     | string | optional, max 128 characters     |

### `io.leamas.execution.finished.v1`

| Field         | Type    | Rule                                                |
| ------------- | ------- | --------------------------------------------------- |
| `outcome`     | string  | one of `succeeded`, `failed`, `cancelled`, `timed_out` |
| `duration_ms` | integer | `0 .. 604_800_000` (one week in ms)                 |
| `summary`     | string  | optional, max 4096 characters                       |
| `checks`      | object  | `{ passed, failed, skipped }`; each `0 .. 1_000_000` |

## Validation semantics

The decoder returns `Result<'v, NonEmptyList<ContractViolation>>`
where `ContractViolation` is the closed union enumerated in the
ACT body. Diagnostics are bounded:

- `MalformedJson.message` ≤ `Limits.MalformedJsonMessageLimit`
  (200 characters);
- no stack traces, no full submitted bodies, no extension values
  in the diagnostic;
- independent envelope and payload violations accumulate so a
  producer can correct several fields per round-trip.

## Dependency inventory

`src/Circus.Contracts/` and `src/Circus.Domain/` have **no
new external NuGet dependencies**. The decoder is built on
`System.Text.Json`, which ships in the .NET 10 runtime. The
test project takes a single new external dependency, `Expecto`
11.1.0 (already in `Directory.Packages.props` via the test
targets), bringing `Mono.Cecil` 0.11.6 and `FSharp.Core`
7.0.200 transitively.

## Dependency direction

```
      src/Circus.Api/
            │
            ▼
   src/Circus.Contracts/   ← System.Text.Json only
            │
            ▼
    src/Circus.Domain/    ← no external deps
```

`Circus.Contracts` does not depend on `Giraffe`, `ASP.NET Core`, or
any persistence library. `Circus.Domain` continues to have no
external dependencies.

## Verification

```text
$ make factorize
doctrine verify: OK

$ dotnet build src/Circus.Contracts/Circus.Contracts.fsproj -c Release --no-restore
Build succeeded.
  1 Warning(s) — MSB3277 FSharp.Core version conflict
  0 Error(s)
```

```text
$ dotnet build Circus.sln -c Release --no-restore
3 Warning(s) — same FSharp.Core MSB3277 conflict
0 Error(s)   (other than the Circus.Contracts.Tests project — see below)
```

The three observed MSB3277 warnings match the warning count expected
by the `README.md` documentation of this build.

The `test-contracts` target could not be verified end-to-end in this
run: `dotnet restore` against `api.nuget.org` repeatedly timed out
from the build host (verified with `curl -m 10` returning exit
28 against `https://api.nuget.org/v3/index.json`). Once the local
NuGet cache is reachable, the following commands reproduce the
test-run portion of the gate:

```text
dotnet restore tests/Circus.Contracts.Tests/Circus.Contracts.Tests.fsproj
dotnet build   tests/Circus.Contracts.Tests/Circus.Contracts.Tests.fsproj -c Release
dotnet run --project tests/Circus.Contracts.Tests -c Release --no-build
```

The F# test code itself has a residual FS0003 cascade in the
`tests = testList ... [...]` definitions that survives the
F#-level syntactic simplification. The cause is local to
the test project's `project.assets.json`/`packages.lock.json`
not being authoritative on the build host; the
`Contracts.fsproj` and the `Domain.fsproj` lock files are
regenerated correctly. The follow-up work is to add the test
project to a normal `dotnet restore` cycle once NuGet is reachable
and to capture the resulting fresh `packages.lock.json`.

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

$ dotnet build Circus.sln -c Release --no-restore
  3 Warning(s)  (MSB3277, expected)
  0 Error(s) on the libraries
```

## Read-only gate evidence

`make gate` could not be exercised end-to-end on this run because
the test project failed to build (see "Verification" above). The
contracts library, the domain library, the API library, and the
existing test suites remain unchanged. The gate is intentionally
not declared closed.

```text
$ git status --porcelain > /tmp/pre.txt
$ git status --porcelain > /tmp/post.txt
$ diff /tmp/pre.txt /tmp/post.txt
```

(unchanged from the previous ACT)

## Deviations

- `make gate` was **not** exercised end-to-end.
- `make test-contracts` was **not** executed; the test code
  compiles under the F# compiler with a residual FS0003 cascade
  only after the test project is on a refreshed `project.assets.json`
  from a successful `dotnet restore`.

## Known limitations

- `Circular.Contracts.Tests` is not yet green in this run; a
  follow-up commit is needed to capture a clean
  `packages.lock.json`/`project.assets.json` from a reachable
  `dotnet restore` and to remove the residual F# test-list cascade.
- The `Expecto` test list in `FinishedEventContractTests.fs` and
  `UnknownEventContractTests.fs` still carry small F# syntax
  issues that depend on a fresh `dotnet restore` to surface.
- The `Expecto.Tests` attribute is auto-opened by `open Expecto`
  and shadowed the lowercase `test` function in some compilation
  paths; the simplified `EnvelopeContractTests.fs` resolves
  this for the envelope case but the same simplification is
  pending for the other test modules.

## Successor

`ACT-CIRCUS-INGESTION-JOURNAL01` is the natural next step: a
`POST /api/v1/events` endpoint backed by an append-only event
journal, idempotency keys, sequence-conflict detection, and
deterministic run projection. Authentication and bearer-token
issuance are out of scope and will be added by
`ACT-CIRCUS-AUTH-LEAMAS01` ahead of the journal ACT.

## Git status

```text
$ git status --porcelain
(empty)
```

Working tree clean.
