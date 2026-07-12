# Architecture

## Overview

The Circus is a functional-programming application built with F# on the backend
and Elm on the frontend. This document describes the system architecture and
design decisions.

## System Layers

```
┌─────────────────────────────────────────────────────────────┐
│                        Browser                               │
│                    (Elm Application)                          │
└────────────────────────┬────────────────────────────────────┘
                         │ HTTP/JSON
┌────────────────────────▼────────────────────────────────────┐
│                     F# HTTP API                              │
│                   (Giraffe + Kestrel)                        │
└────────────────────────┬────────────────────────────────────┘
                         │
┌────────────────────────▼────────────────────────────────────┐
│                 F# Domain Core                               │
│              (Circus.Domain)                                │
└─────────────────────────────────────────────────────────────┘
                         ▲
┌────────────────────────┴────────────────────────────────────┐
│             F# Contracts (Leamas events)                     │
│           (Circus.Contracts — future ingestion)             │
└─────────────────────────────────────────────────────────────┘
```

### Decoding pipeline (untrusted bytes → typed domain)

The first versioned Leamas → Circus contract sits in front of the
domain layer. Its job is to convert untrusted bytes into a typed
`ValidatedEvent` without any persistence, networking, or exceptions.

```
bytes
  → Circus.Contracts (pure F# decoder, validates envelope + payload)
  → Circus.Domain validated event (ExecutionStarted | Finished | Unrecognized)
```

This boundary owns everything through `validated domain event`.
PostgreSQL, authentication, idempotency, and HTTP response behaviour
are deferred to `ACT-CIRCUS-INGESTION-JOURNAL01` and beyond. Until then
the decoder is pure: it produces `ValidatedEvent`s without side effects.

## F# Domain Core (Circus.Domain)

The domain layer is a pure F# library with no external dependencies on
web frameworks, databases, or infrastructure.

### Design Principles

1. **No HTTP dependencies**: The domain module cannot reference `System.Net.Http`
   or any web framework.

2. **Type safety**: Uses opaque types for domain primitives to prevent
   invalid value construction at the boundaries.

3. **Canonical values**: Product identity is defined once in the domain
   and projected to DTOs at the HTTP boundary.

### ProductIdentity Module

```fsharp
type ProductName = private ProductName of string
type ProductTagline = private ProductTagline of string
type ProductDescription = private ProductDescription of string

type ProductIdentity =
    {
        Name: ProductName
        Tagline: ProductTagline
        Description: ProductDescription
    }
```

## Giraffe HTTP Boundary (Circus.Api)

The API layer uses Giraffe on ASP.NET Core to expose HTTP endpoints.

### Design Principles

1. **Composability**: The web application is built as a composable `HttpHandler`
   that can be constructed by both production startup and integration tests.

2. **No TCP port requirement for tests**: Tests use `WebHostBuilder` with
   `UseTestServer()` to run in-process without binding to a real port.

3. **Projection from domain**: API responses are projected from domain types
   at the boundary, not duplicated inline.

### Endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/health/live` | GET | Liveness probe |
| `/api/v1/about` | GET | Product information |
| `/` | GET | Static HTML (Elm app) |
| `/*` | GET | Static assets |

### JSON Serialization

Uses `System.Text.Json` with camelCase naming policy. Domain types are
projected to DTOs before serialization to avoid exposing internal types.

## Elm Frontend Boundary

The frontend is a pure Elm 0.19.2 application following The Elm Architecture (TEA).

### Design Principles

1. **No JavaScript application code**: The only JavaScript is the minimal
   bootstrap in `index.html` that initializes the Elm app.

2. **No ports or flags**: All data is loaded via HTTP requests from the
   Elm application itself.

3. **Closed remote state**: Uses a closed `RemoteData` type with exactly
   four states: `NotAsked`, `Loading`, `Failure`, `Success`.

### Required UI States

| State | Display |
|-------|---------|
| Loading | "Loading product information..." |
| Success | Product name, tagline, description |
| Failure | Error message with retry button |

### Accessibility

- Single `<main>` landmark
- Single level-one heading for product name
- Keyboard-accessible retry button
- Meaningful document title
- Error state communicated via text, not color alone

## Generated Static Asset Boundary

The compiled Elm application is served from `web/dist/` as static files:

```
web/dist/
├── index.html   (copied from web/)
├── styles.css   (copied from web/)
└── app.js       (compiled by elm make)
```

The backend serves these files at the root path (`GET /`) and the same
directory (`GET /app.js`, `GET /styles.css`).

## Dependency Direction

```
Circus.Domain (pure library)
      ↑
Circus.Api (web layer depends on domain)
```

The domain layer has no knowledge of the API. This is enforced by the
project reference: `Circus.Api` references `Circus.Domain`, but not vice versa.

## Why PostgreSQL Is Deferred

This ACT establishes the foundational vertical slice. Persistence is deferred
because:

1. **Incremental verification**: Proving the F#/Elm stack works first allows
   gradual addition of persistence without risking the entire stack.

2. **No speculative infrastructure**: We prefer to add database integration
   after the API contract is stable.

3. **Eventual CloudEvents**: Event ingestion will follow a different pattern
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
