# Event Journal Persistence Specification v1

This document is the durable continuation of
`ACT-CIRCUS-INGESTION-JOURNAL01` and is the authoritative spec closed by
`ACT-CIRCUS-INGESTION-JOURNAL01-CLOSURE01` and corrected by
`ACT-CIRCUS-INGESTION-JOURNAL01-CLOSURE01-CORRECTION01`.  It replaces the
partial specification that the parent ACT left in place.  Statements
in this file describe the implemented code; tested claims are listed
in `closure-ACT-CIRCUS-INGESTION-JOURNAL01-CORRECTION01.md` as
encoded assertions in Expecto tests.  The PostgreSQL live run,
`make test-backend`, and `make gate` were not exercised in this
ACT; the encoded assertions describe the expected behaviour but do
not certify it as produced evidence.

## Overview

The event journal is an append-only store for Leamas execution events.  It
provides:

* **Durability** - events are persisted to PostgreSQL and survive restarts.
* **Idempotency** - duplicate events are detected through PostgreSQL
  `jsonb` semantic equality and classified as `IdempotentReplay` instead of
  being re-inserted.
* **Determinism** - concurrent ingestion of the same authority converges on
  exactly one `Inserted` outcome with all other attempts typed as replays or
  sequence conflicts.
* **Auditability** - the exact accepted request body and its SHA-256 digest
  are retained alongside the semantic JSON; the raw bytes are immutable
  across replays, semantic-equivalent retries, and rebuilds.
* **Immutability** - journal rows cannot be updated, deleted, or truncated
  by the runtime application role, by inheritance, or by a privileged
  routine.

## Architecture

```
HTTP bytes
  → authorization seam (deny-all by default)
  → Circus.Contracts (bounded byte size, duplicate-key detection, valid JSON,
     contract validation)
  → IngestEvent application service
  → serializable PostgreSQL transaction (one fresh connection per attempt)
       ├── insert into circus.circus_event_journal (raw + digest, ON CONFLICT DO NOTHING)
       └── upsert circus.circus_run_projection through RunProjection.applyEvent
  → typed HTTP outcome (201 / 200 / 409 / 422 / 503 / 500)
```

The application service obtains a fresh connection from the host-owned
`NpgsqlDataSource` for every attempt, runs the `SERIALIZABLE` transaction,
commits once, and only returns the outcome after the journal row is durable.
Retry re-executes the complete transaction against a new connection; the
`NpgsqlTransaction` is never reused.

## Identity Semantics

CloudEvents identity is the pair `(source, event_id)`.  The pair is unique
within the journal:

```sql
CONSTRAINT circus_event_journal_source_event_id_uq UNIQUE (source, event_id)
```

A duplicate identity with semantically equal JSON is classified as
`IdempotentReplay`.  A duplicate identity with semantically different JSON
is classified as `EventIdentityConflict`.  The semantic comparison is
performed by PostgreSQL's `jsonb` equality against the persisted
`envelope_json` column; raw byte differences that produce the same JSON
value never create a second row.

## Stream Sequence Semantics

Within a specific Leamas instance and epoch, events are ordered by sequence
number.  The triple `(instance_id, epoch_id, sequence)` is unique:

```sql
CONSTRAINT circus_event_journal_stream_sequence_uq UNIQUE (instance_id, epoch_id, sequence)
```

Two concurrent inserts with the same triple produce one `Inserted` and one
`SequenceConflict`; the losing connection never persists a journal row.

## Raw and Semantic Authority

The journal persists both authorities independently:

* `envelope_json jsonb` - the canonical semantic JSON used for replay
  equality.
* `raw_body bytea` - the exact accepted request body.
* `raw_body_sha256 bytea` - SHA-256 digest of the raw body, computed by the
  application before insert.

Replay tests prove the first accepted raw bytes are immutable: subsequent
semantic-equivalent requests leave the persisted `raw_body` byte-for-byte
unchanged even when the second body uses different whitespace and key
ordering.

## Replay Classification

| Outcome | Meaning |
| ------- | ------- |
| `Inserted` | New authority row.  The reducer derived a new projection version. |
| `IdempotentReplay` | Identity, sequence, and semantic JSON match an existing row.  No journal mutation, no projection change, no version increment. |
| `EventIdentityConflict` | Identity exists but the persisted JSON differs. |
| `SequenceConflict` | Sequence exists under a different identity. |
| `CrossIdentityConflict` | Identity and sequence resolve to different rows. |

## Append-Only Enforcement

The journal is protected at three layers:

* Database triggers reject `UPDATE` and `DELETE` on `circus_event_journal`.
* The `circus_app` role owns no objects and inherits none.  `REVOKE` removes
  inherited and `PUBLIC` privileges before the narrow runtime grants are
  applied.
* The runtime `NpgsqlDataSource` carries only `SELECT, INSERT` on the
  journal; `UPDATE`, `DELETE`, and `TRUNCATE` all fail with a
  permission error when executed through the real service credentials.

The migration set is ledger-aware and self-sufficient at
`000003_runtime_grant_hardening`: the runner discovers already-applied
versions, skips them, and `000003` independently upgrades any
released environment (parent-ACT public.* state, released-parent
circus.* state with `circus_owner` absent, or the canonical
fresh-database state) to the canonical circus.* schema with the
authoritative raw-digest invariant, complete role ownership, narrow
runtime grants, default privilege policy, and a fail-closed
extension schema.  `000001_event_journal` is the initial
schema; `000002_namespace_alignment` is the immutable released
namespace alignment; `000003_runtime_grant_hardening` is the
post-release hardening migration.  See
`closure-ACT-CIRCUS-INGESTION-JOURNAL01-CORRECTION01.md` for the
encoded test assertions (fresh, legacy, released-parent, repeated
no-op, ambiguous rejection).  The PostgreSQL live run, `make
test-backend`, and `make gate` were not exercised in this ACT.

## Transaction Isolation

Journal insertion and projection mutation occur in a single
`SERIALIZABLE` PostgreSQL transaction.  The transaction has exactly two
terminal paths: a successful `AppendSucceeded(outcome, projection)` commits
and surfaces as `Success`; an `AppendFailed failure` rolls back without
committing and surfaces as `PersistenceFailure`.  `40001` (serialization
failure) and `40P01` (deadlock detected) are the only SQLSTATEs that
trigger a retry; every other `NpgsqlException` is converted to a typed
`PersistenceFailure` that the API layer maps to a generic `500` or
`503` without leaking the SQLSTATE.  The exact attempt count, the typed
exhaustion result, and the production-composition path are covered by
`tests/Circus.Persistence.Postgres.Tests/RetryCompositionTests.fs`.

## Run Projection Rules

The run projection is derived state computed from journal events through
the same `RunProjection.applyEvent` reducer used by online ingestion and
by `ProjectionRebuild.rebuildFromJournal`.  Both paths use one reducer so
their results are equivalent.

1. The first authoritative mutation writes the projection with `version = 1`.
2. Each subsequent authoritative mutation increments `version` by exactly
   one.  `IdempotentReplay`, unknown events, reads, and rebuilds do not
   increment it.
3. The first `started` event becomes authoritative started evidence; the
   first `finished` event becomes authoritative finished evidence.  Later
   events with the same role replace the conflict count and increment the
   version, but never overwrite the original authorities.
4. Once a projection enters `Conflicted`, no later authoritative event
   returns it to a non-conflict state.
5. Unknown event types are journaled but are not mapped to a projection
   mutation.

The `mapToProjection` decoder resolves every column by name and rejects any
persisted row whose values contradict the domain invariants with the typed
`PersistenceFailure.ProjectionInvariantFailed` result.  No default enum
case, fabricated identifier, generated timestamp, or current-time value is
ever substituted.

## Failure Mapping

| HTTP status | Condition | Stable public code |
| ----------- | --------- | ------------------ |
| 403         | Authorization denied (any reason) | `authorization_denied` |
| 413         | Body exceeds the configured 256 KiB limit (declared or streamed) | `body_too_large` |
| 415         | Content-Type missing or unsupported | `unsupported_content_type` |
| 400         | Malformed JSON or duplicate JSON property | `malformed_json` / `duplicate_json_property` |
| 422         | Syntactically valid JSON that fails the event contract | `contract_violation` |
| 409         | Identity or sequence conflict | `event_conflict` |
| 503         | Retry exhaustion or temporary database unavailability | `service_unavailable` |
| 500         | Projection invariant failure or unexpected persistence error | `internal_error` |
| 201         | New event inserted | `inserted` |
| 200         | Idempotent semantic replay | `idempotent_replay` |

Public responses never echo raw bytes, normalized payload, SQL text, table
or schema names, connection strings, stack traces, exception messages, or
internal union case names.  Logs are likewise free of credentials and
request bodies.

## Authorization Seam

The HTTP boundary receives an `AuthorizationPort` whose type signature is

```fsharp
type AuthorizationPort = HttpContext -> Task<Result<ProducerPrincipal, IngestionAuthorizationFailure>>
```

The initial production adapter is `AuthorizationAdapters.denyAll`, which
returns `MissingCredentials` for every request without consuming the
request body.  Real producer authentication is implemented in
`ACT-CIRCUS-AUTH-LEAMAS01`; that ACT explicitly builds on the seam provided
here.

## Rebuild Authority

`ProjectionRebuild.rebuildFromJournal dataSource` reads the journal in
ascending `journal_position` order, runs each row back through
`EventDecoder.decode` and `RunProjection.applyEvent`, and returns the
resulting projection map.  The rebuild uses the same reducer as the
ingestion transaction; tests prove the incremental projection and the
rebuilt projection are structurally equal after a representative scenario
suite (started-then-finished, finished-then-started, replay, unknown
event, conflict, first-authority preservation).

## Host Lifecycle and Configuration

`CIRCUS_DATABASE_URL` is mandatory.  The host fails startup immediately when
the variable is absent, empty, malformed, or missing a host or database.
There is no fallback username, password, host, or database value.

`NpgsqlDataSource` is constructed lazily by the DI factory registered
in `Program.configureServices` exactly once and the service provider
owns its lifetime.  The data source is disposed when the host stops.
No request path or retry path constructs a separate connection pool.

The four pure-validation paths (missing, empty, whitespace, malformed)
are covered by `tests/Circus.Api.Tests/HostLifecycleTests.fs`.  The two
testcontainer-based paths (singleton lifetime and host disposal) require
a reachable Docker daemon.

## Operational Limitations

This implementation does not include:

* Producer authentication (deferred to `ACT-CIRCUS-AUTH-LEAMAS01`).
* Event batches.
* Journal partitioning.
* Retention or deletion.
* Background projection workers.
* Multi-region replication.
* Projection rebuild CLI.
* PostgreSQL logical replication.

## Schema

`000001_event_journal` creates the canonical circus.* tables.
`000002_namespace_alignment` is the immutable released namespace
alignment (moves public.* tables into circus.* and adds the
raw_body_sha256 length-only CHECK).  `000003_runtime_grant_hardening`
is the self-sufficient corrective migration: it reconciles both
roles, fails closed on an unexpected extension-schema owner or
unexpected CREATE grant, drops every legacy digest-related CHECK,
re-authors the canonical equality CHECK, installs pgcrypto in
`circus_extensions`, and enforces runtime least privilege.

See `db/migrations/000001_event_journal.sql`,
`db/migrations/000002_namespace_alignment.sql`, and
`db/migrations/000003_runtime_grant_hardening.sql` for the complete
DDL.
