# The Circus

The Circus is the team-scale coordination, evidence, and governance
platform for Leamas.

Leamas operates close to an individual repository. The Circus provides
the shared layer for development teams working across repositories,
engineering initiatives, doctrine versions, executions, and evidence.

## Status

This project is **experimental**. The F#/Elm vertical slice is green:
`make gate` runs `factorize`, `format-check`, both backend test suites,
the Elm test suite, the optimised Elm build, and the HTTP smoke probes.

## Prerequisites

| Tool | Version | Notes |
|------|---------|-------|
| .NET SDK | 10.0.2xx feature band (tested with 10.0.202) | Pinned via `global.json` to `10.0.200` with `latestPatch` roll-forward |
| Node.js | 26.x | Used only as an Elm toolchain host |
| Elm | 0.19.2 | Provided by npm `elm@0.19.2-0` |
| elm-test | 0.19.2-0 | Provided by npm `elm-test@0.19.2-0` |
| elm-format | 0.8.7 | Provided by npm `elm-format@0.8.7` |
| Leamas | 0.1.0+dev | Factory management CLI |
| GNU Make | 4.x | Build interface |

## Supported Toolchain

| Concern | Version |
|---------|---------|
| .NET SDK | 10.0.202 |
| .NET Runtime | 10.0.6 |
| Target Framework | `net10.0` |
| F# | 10 stable |
| Giraffe | 8.2.0 |
| Expecto | 11.1.0 |
| Microsoft.AspNetCore.TestHost | 10.0.0 |
| Elm | 0.19.2 (via `elm@0.19.2-0`) |
| elm-test | 0.19.2-0 |
| elm-explorations/test | 2.2.1 |

## Commands

### Restore

```bash
make restore
```

Runs `dotnet tool restore`, `dotnet restore Circus.sln --locked-mode`, and `npm ci`.

### Format

```bash
make format
```

Mutating. Must never run inside `factorize` or `gate`. Depends on `restore` so a fresh checkout can use it directly.

### Format Check

```bash
make format-check
```

Validates formatting without modifying files.

### Build

```bash
make build
```

Builds backend (Release) and frontend (optimised).

### Test

```bash
make test
```

Runs F# Expecto suites and `elm-test`.

### Run

```bash
make run
```

Starts the API on http://127.0.0.1:5000.

### Smoke

```bash
make smoke
```

Exercises the assembled application end-to-end through the HTTP boundary:

* `GET /health/live` → 200, `{"status":"live"}`
* `GET /api/v1/about` → 200, canonical product identity JSON
* `GET /styles.css` → 200, `text/css`, non-empty body
* `GET /` → 200, served from `web/dist/index.html`
* `GET /app.js` → 200, served from `web/dist/app.js`

### Gate

```bash
make gate
```

Runs the complete native quality gate. Depends on `factorize`.

## Local URL

The application runs at: http://127.0.0.1:5000

## Verification Status

* `make factorize` returns `doctrine verify: OK`.
* `dotnet build Circus.sln -c Release --no-restore` exits with 0 errors. Three `MSB3277` FSharp.Core version warnings remain because Giraffe 8.2.0 transitively pulls FSharp.Core 6.0.0 and the `Circus.Api.Tests` test project pulls FSharp.Core 7.0.200; future resolution requires dependency-graph investigation.
* `dotnet run --project tests/Circus.Domain.Tests` runs **4/4 Expecto tests**.
* `dotnet run --project tests/Circus.Contracts.Tests` runs the contract suite.
* `dotnet run --project tests/Circus.Api.Tests` runs **10/10 Expecto tests**.
* `cd web && ./node_modules/.bin/elm-test --compiler ./node_modules/.bin/elm` runs **17/17 tests** in ~140 ms.
* `bash scripts/smoke.sh` exercises the four documented endpoints and the `/styles.css` static asset.
* `make gate` is **read-only**: `git status --porcelain` is identical before and after.

## Current Capability

This ACT establishes the foundational vertical slice:

* **F# Backend**: Giraffe HTTP API with liveness, product, and static-asset endpoints.
* **Elm Frontend**: Pure Elm 0.19.2 application loading product identity from the API. Loading, success, failure, and retry states are visible.
* **F# Domain**: Pure domain module exposing a single canonical `ProductIdentity` value, a `NonEmptyList` validation primitive, opaque domain identifiers (`EventId`, `EventSource`, `EventType`, `InstanceId`, `EpochId`, `EventSequence`, `RunId`, `RepositoryRef`, `ActId`, `LeamasVersion`), the four-state `ExecutionOutcome`, the `CheckCounts` check-record, `ExecutionStarted`/`ExecutionFinished` payload records, and the `ExecutionEvent` union for `started`/`finished`/`unrecognized` events.
* **F# Contracts** (`Circus.Contracts`): pure, deterministic decoder that turns CloudEvents-structured JSON envelopes into `ValidatedEvent`s, validates Circus-specific extension attributes, and dispatches onto known or unknown event payload types.
* **F# Tests**: Expecto suites for domain invariants, the Leamas-events contract (envelope, started, finished, unknown, safety), and HTTP contracts.
* **Elm Tests**: `elm-explorations/test` suites for the JSON decoder and the application view and update transitions.
* **Smoke**: Single-shell `set -euo pipefail` transaction with trap-based cleanup, bounded readiness polling, exact JSON assertions, and a non-empty `app.js` assertion.

### Implemented Features

* `GET /health/live` — liveness probe.
* `GET /api/v1/about` — product information.
* `GET /styles.css` — Elm stylesheet, served as `text/css`.
* `GET /` — compiled Elm application, served from `web/dist/index.html` with a bounded 503 fallback when the artefact has not been built.
* `GET /app.js` — compiled Elm runtime, served from `web/dist/app.js`.
* Static asset serving from `web/dist/` when present.

### Not Implemented (Deferred)

The following are explicitly out of scope for this ACT and remain
deferred:

* PostgreSQL persistence
* CloudEvents ingestion
* Leamas authentication
* Leamas reporters
* Run storage and projections
* Teams and organizations
* User authentication
* Docker / Compose / Kubernetes
* Metrics and tracing
* CI workflows

## Factory Doctrine

Factory doctrine is compiled from Leamas using the `fsharp-elm-service-v1`
profile from the `factory-core-v1` pack.

Generated Factory files must not be edited manually:

* `.factory/doctrine.lock.json`
* `.factory/generated/*`
* `docs/factory/README.md`

## Documentation

* [docs/product-thesis.md](docs/product-thesis.md) — product thesis and design intent.
* [.factory/generated/doctrine-inventory.md](.factory/generated/doctrine-inventory.md) — enabled Factory doctrines.
* [docs/architecture.md](docs/architecture.md) — system architecture.
* [docs/experiment-baseline.md](docs/experiment-baseline.md) — initial FP experiment measurements.
