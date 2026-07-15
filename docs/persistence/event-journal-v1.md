# Event Journal Persistence Specification v1

This document describes the PostgreSQL event journal implementation for The Circus,
as delivered by ACT-CIRCUS-INGESTION-JOURNAL01.

## Overview

The event journal is an append-only store for Leamas execution events. It provides:

- **Durability**: Events are persisted to PostgreSQL and survive restarts
- **Idempotency**: Duplicate events are detected and handled gracefully
- **Determinism**: Concurrent ingestion produces deterministic results
- **Auditability**: Raw bytes and normalized JSON are both retained
- **Immutability**: Journal rows cannot be updated or deleted

## Architecture

```
HTTP bytes
  → authorization seam
  → Circus.Contracts
  → IngestEvent application service
  → serializable PostgreSQL transaction
       ├── append-only journal
       └── run projection
```

## Identity Semantics

CloudEvents identity is the pair `(source, event_id)`. This pair uniquely
identifies an event in the Circus system. The database enforces uniqueness:

```sql
CONSTRAINT circus_event_journal_source_event_id_uq UNIQUE (source, event_id)
```

## Stream Sequence Semantics

Within a specific Leamas instance and epoch, events are ordered by sequence number.
The combination `(instance_id, epoch_id, sequence)` is unique:

```sql
CONSTRAINT circus_event_journal_stream_sequence_uq UNIQUE (instance_id, epoch_id, sequence)
```

## Replay Classification

An event is considered an **idempotent replay** when:

1. The event identity `(source, event_id)` matches an existing row
2. The stream position `(instance, epoch, sequence)` matches the same row
3. The envelope JSON is semantically equal (PostgreSQL jsonb comparison)

If any of these conditions fail, the outcome is a typed conflict:

| Outcome | Meaning |
| ------- | ------- |
| `Inserted` | Neither identity nor sequence existed; one row appended |
| `IdempotentReplay` | Same row exists with matching envelope; no mutation |
| `EventIdentityConflict` | Identity exists but stream position differs |
| `SequenceConflict` | Stream position exists under different identity |
| `CrossIdentityConflict` | Identity and sequence resolve to different rows |

## Append-Only Enforcement

The journal is protected by database triggers:

```sql
CREATE TRIGGER circus_event_journal_prevent_update
    BEFORE UPDATE ON circus_event_journal
    FOR EACH ROW
    EXECUTE FUNCTION circus.prevent_journal_modification();

CREATE TRIGGER circus_event_journal_prevent_delete
    BEFORE DELETE ON circus_event_journal
    FOR EACH ROW
    EXECUTE FUNCTION circus.prevent_journal_modification();
```

Application role permissions restrict access:

```sql
GRANT SELECT, INSERT ON circus_event_journal TO circus_app;
-- UPDATE, DELETE, TRUNCATE are not granted
```

## Transaction Isolation

Journal insertion and projection updates occur in a single SERIALIZABLE transaction:

```sql
BEGIN TRANSACTION ISOLATION LEVEL SERIALIZABLE;
-- INSERT INTO circus_event_journal ...
-- UPDATE circus_run_projection ...
COMMIT;
```

Serialization failures (SQLSTATE 40001) are retried up to 3 times.

## Run Projection Rules

The run projection is derived state computed from journal events:

1. **Unknown events do not create or mutate projections**
2. **First started event becomes authoritative started evidence**
3. **First finished event becomes authoritative finished evidence**
4. **Finished-before-start produces `FinishedWithoutStart`**
5. **A later started event completes a finished-first projection**
6. **A second distinct started or finished event marks conflict without overwriting authority**
7. **Replays do not call `applyEvent`**
8. **Fold order is ascending `journal_position`**

### Projection States

| State | Meaning |
| ----- | ------- |
| `StartedOnly` | Started event received, no finish yet |
| `FinishedWithoutStart` | Finished event received, no start yet |
| `Completed` | Both started and finished received |
| `Conflicted` | Multiple started or finished events detected |

## Failure Mapping

| Condition | HTTP Status |
| --------- | ----------- |
| missing/invalid credentials | 401 |
| producer not allowed for instance | 403 |
| unsupported content type | 415 |
| malformed JSON | 400 |
| body too large | 413 |
| other contract violations | 422 |
| event identity conflict | 409 |
| sequence conflict | 409 |
| cross-identity conflict | 500 |
| database unavailable | 503 |
| retries exhausted | 503 |

## Authorization Seam

Production authorization defaults to deny-all. The `IngestionAuthorizationPort`
extracts the producer principal from the HTTP context. A later ACT will provide
real bearer-token implementation.

## Rebuild Authority

The projection can be rebuilt from journal order:

```fsharp
rebuild : (JournalPosition * ValidatedEvent) list -> Map<RunId, RunProjection>
```

Integration tests prove:
```text
incremental projection rows == projection rebuilt from journal_position order
```

## Operational Limitations

This implementation does not include:

- Production bearer-token issuance (deferred to ACT-CIRCUS-AUTH-LEAMAS01)
- Event batches
- Journal partitioning
- Retention or deletion
- Background projection workers
- Multi-region replication
- Projection rebuild CLI
- PostgreSQL logical replication

## Schema

See `db/migrations/000001_event_journal.sql` for the complete DDL.
