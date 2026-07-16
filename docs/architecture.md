# Architecture

## Overview

The Circus is a functional-programming application built with F# on the backend
and Elm on the frontend. This document describes the system architecture and
design decisions.

## System Layers

```
┌─────────────────────────────────────────────────────────────┐
│                        Browser                               │
│                    (Elm Application)                         │
└────────────────────────┬────────────────────────────────────┘
                         │ HTTP/JSON
┌────────────────────────▼────────────────────────────────────┐
│                     F# HTTP API                             │
│                   (Giraffe + Kestrel)                      │
└────────────────────────┬────────────────────────────────────┘
                         │
┌────────────────────────▼────────────────────────────────────┐
│                  Application Layer                           │
│              (Circus.Application)                           │
│  • Ingestion orchestration                                  │
│  • Journal decision logic                                   │
│  • Run projection                                          │
│  • Authorization seam                                       │
└────────────────────────┬────────────────────────────────────┘
                         │
┌────────────────────────▼────────────────────────────────────┐
│                Persistence Layer                            │
│           (Circus.Persistence.Postgres)                    │
│  • Journal repository                                       │
│  • Projection repository                                    │
│  • Transaction management                                   │
└────────────────────────┬────────────────────────────────────┘
                         │
┌────────────────────────▼────────────────────────────────────┐
│              F# Domain Core                                 │
│               (Circus.Domain)                              │
└────────────────────────┬────────────────────────────────────┘
                         ▲
┌────────────────────────┴────────────────────────────────────┐
│              F# Contracts (Leamas events)                    │
│                (Circus.Contracts)                           │
└─────────────────────────────────────────────────────────────┘
```

### Ingestion pipeline

```
HTTP bytes
  → authorization seam (production: deny-all)
  → Circus.Contracts (bounded byte size, duplicate-key detection, valid JSON,
     contract validation)
  → IngestEvent application service
  → serializable PostgreSQL transaction (one fresh connection per attempt)
       ├── insert into circus.circus_event_journal (raw + digest, ON CONFLICT DO NOTHING)
       └── upsert circus.circus_run_projection through RunProjection.applyEvent
  → typed HTTP outcome (201 / 200 / 409 / 422 / 503 / 500)
```

The HTTP boundary receives the authorization port and the ingestion port
through dependency injection.  Production composes the route, the real
authorization adapter, the real `IngestEventService`, the PostgreSQL
persistence adapter, and the single host-owned `NpgsqlDataSource`.

### Persistence invariants

* All PostgreSQL objects (tables, sequences, indexes, triggers, functions)
  live in the `circus` schema and are qualified as `circus.*` in every
  statement, migration, test, grant, and revoke.
* The migration/owner role `circus_owner` owns the journal, the
  projection, the migration tracker, and the trigger function.  The
  runtime role `circus_app` owns nothing.
* The runtime role is `NOSUPERUSER`, has no `BYPASSRLS`, does not
  inherit, and has `SELECT, INSERT` on the journal only.  `UPDATE`,
  `DELETE`, and `TRUNCATE` all fail at execution time through the real
  service credentials.
* The serializable transaction commits only when both the journal insert
  and the projection upsert succeed.  A typed `AppendFailed` failure
  rolls back without committing; a trigger-driven projection exception
  rolls back without committing.  Both paths are covered by
  `tests/Circus.Persistence.Postgres.Tests/AppendFailedRollbackTests.fs`
  and `tests/Circus.Persistence.Postgres.Tests/ConcurrencyTests.fs`.
* The migration runner is ledger-aware: it discovers already-applied
  versions, skips them, records each version only after the migration
  transaction commits, and fails closed when both the legacy
  `public.circus_schema_migrations` and the canonical
  `circus.circus_schema_migrations` ledgers exist.
* `000001_event_journal` is the initial journal and projection
  schema; `000002_namespace_alignment` is the immutable released
  namespace alignment; `000003_runtime_grant_hardening` is the
  post-release corrective migration that carries every role,
  ownership, digest, trigger, privilege, and index invariant the
  released tree did not author on its own.  See
  `docs/persistence/event-journal-v1.md` for the durable spec and
  `closure-ACT-CIRCUS-INGESTION-JOURNAL01-CORRECTION01.md` for the
  exact migration paths.

### Bounded retry

`RetryPolicy.execute` runs the complete transaction once per attempt,
obtains a fresh connection for each attempt, and never reuses a failed
`NpgsqlTransaction`.  The policy retries on SQLSTATE `40001` and `40P01`
only.  All other database errors are converted to a typed
`PersistenceFailure` that the API layer maps to a generic 5xx response
without leaking internals.  `RetryPolicy.execute` is the sole retry
authority in production; the recursive loop that previously lived inside
`IngestionTransaction.fs` is removed.

The transaction has exactly two terminal paths.  The service commits
only on `AppendSucceeded(outcome, projection)`; every `AppendFailed failure`
rolls back before the service surfaces `PermanentFailure failure` to the
retry policy.  The success path is
`commit -> RetrySucceeded -> Success`; the failure path is
`safeRollback -> PermanentFailure -> PersistenceFailure`.

## F# Domain Core (Circus.Domain)

The domain layer is a pure F# library with no external dependencies on
web frameworks, databases, or infrastructure.

### Design Principles

1. **No HTTP dependencies**: The domain module cannot reference `System.Net.Http`
   or any web framework.

2. **No persistence dependencies**: The domain module cannot reference Npgsql,
   Entity Framework, or any database infrastructure.

3. **Type safety**: Uses opaque types for domain primitives to prevent
   invalid value construction at the boundaries.

4. **Canonical values**: Product identity is defined once in the domain
   and projected to DTOs at the HTTP boundary.

## Application Layer (Circus.Application)

The application layer orchestrates ingestion without knowing about HTTP or
database specifics.

### Components

- **JournalModel**: Core types for journal identity, stream position, and append outcomes
- **JournalDecision**: Pure classification logic for collision outcomes
- **RunProjection**: Deterministic fold over journal events to derive run state
- **IngestionAuthorization**: Authorization seam (production: deny-all; tests: explicit)
- **IngestEvent**: Application service for event ingestion

### Dependency Direction

```
Circus.Domain
      ↑
Circus.Contracts
      ↑
Circus.Application
```

## Persistence Layer (Circus.Persistence.Postgres)

Raw Npgsql commands are used directly. No EF Core, Dapper, or generic repository.

### Components

- **PostgresConfiguration**: Connection string and retry configuration
- **JournalSql**: Parameterized SQL statements
- **JournalRepository**: Append-only journal operations
- **ProjectionRepository**: Run projection upsert and query
- **IngestionTransaction**: SERIALIZABLE transaction with retry logic

### Dependency Direction

```
Circus.Application
      ↑
Circus.Persistence.Postgres
```

## HTTP Boundary (Circus.Api)

The API layer uses Giraffe on ASP.NET Core to expose HTTP endpoints.

### Design Principles

1. **Composability**: The web application is built as a composable `HttpHandler`
   that can be constructed by both production startup and integration tests.

2. **No TCP port requirement for tests**: Tests use `WebHostBuilder` with
   `UseTestServer()` to run in-process without binding to a real port.

3. **Projection from domain**: API responses are projected from domain types
   at the boundary, not duplicated inline.

4. **Authorization before body**: Credentials are validated before reading the request body.

5. **Diagnostic safety**: Raw payloads, SQL, and exception details are never echoed.

### Endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/health/live` | GET | Liveness probe |
| `/api/v1/about` | GET | Product information |
| `POST /api/v1/events` | POST | Event ingestion |
| `/` | GET | Static HTML (Elm app) |
| `/*` | GET | Static assets |

### Event Ingestion

`POST /api/v1/events` accepts CloudEvents JSON and:

1. Authorizes the request (production: deny-all)
2. Validates Content-Type is `application/cloudevents+json`
3. Reads bounded body (max 256 KiB)
4. Decodes via Circus.Contracts
5. Normalizes JSON for storage
6. Executes SERIALIZABLE transaction
7. Returns typed HTTP outcome

## Elm Frontend Boundary

The frontend is a pure Elm 0.19.2 application following The Elm Architecture (TEA).

### Design Principles

1. **No JavaScript application code**: The only JavaScript is the minimal
   bootstrap in `index.html` that initializes the Elm app.

2. **No ports or flags**: All data is loaded via HTTP requests from the
   Elm application itself.

3. **Closed remote state**: Uses a closed `RemoteData` type with exactly
   four states: `NotAsked`, `Loading`, `Failure`, `Success`.

## Dependency Direction Summary

```
Circus.Domain
      ↑
Circus.Contracts
      ↑
Circus.Application
      ↑
Circus.Persistence.Postgres
      ↑
Circus.Api
```

Forbidden dependencies:
- `Circus.Domain` → PostgreSQL, Npgsql, Giraffe, JSON infrastructure
- `Circus.Persistence.Postgres` → HTTP types
- `Circus.Api` → Business logic in handlers

## PostgreSQL Integration

The journal is append-only with database-enforced constraints:

- `(source, event_id)` uniqueness
- `(instance_id, epoch_id, sequence)` uniqueness
- Triggers prevent UPDATE and DELETE
- Application role has SELECT and INSERT only

Serialization failures are retried up to 3 times.

## Why PostgreSQL Is Deferred

This ACT establishes the foundational vertical slice. Persistence is deferred
because:

1. **Incremental verification**: Proving the F#/Elm stack works first allows
   gradual addition of persistence without risking the entire stack.

2. **No speculative infrastructure**: We prefer to add database integration
   after the API contract is stable.

3. **Eventual CloudEvents**: Event ingestion follows a different pattern
   than traditional CRUD, so we defer schema design until the event model
   is clearer.

## Why JavaScript Application Code Is Prohibited

The Elm architecture provides strong guarantees through:

- **No runtime exceptions**: The type system ensures all states are handled.
- **Reproducible rendering**: Same input always produces same output.
- **No undefined states**: The compiler enforces exhaustive pattern matching.

Introducing handwritten JavaScript would bypass these guarantees and create
an untestable integration point.

## How The Make Gate Composes With Factory

```
make gate
    ├── factorize (Factory verification)
    ├── restore (toolchain setup)
    ├── format-check (F# + Elm formatting)
    ├── build-backend (Release configuration)
    ├── test-backend (Expecto tests)
    ├── test-web (Elm tests)
    ├── build-web (optimized Elm build)
    └── smoke (HTTP contract verification)
```

Factory (`factorize`) runs first and independently. If it fails, the native
gate does not proceed. This ensures:

1. Factory doctrine is always verified before native checks.
2. Native changes don't break Factory assumptions.
3. The gate can be extended without modifying Factory behavior.
