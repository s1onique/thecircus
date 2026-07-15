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
  → authorization seam
  → Circus.Contracts (validates envelope + payload)
  → IngestEvent application service
  → serializable PostgreSQL transaction
       ├── append-only journal
       └── run projection
  → typed HTTP outcome
```

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
