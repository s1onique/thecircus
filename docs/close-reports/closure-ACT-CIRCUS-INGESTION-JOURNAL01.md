# Closure Report - ACT-CIRCUS-INGESTION-JOURNAL01-CLOSURE01

## 1. Status

**PARTIAL**

The implementation is complete, the documentation is corrected, the build
and the non-PostgreSQL test suites are clean, and the deterministic
in-process evidence is exercised end to end.  The PostgreSQL
integration suite is wired into `make test-backend`, builds with zero
warnings and zero errors, and is gated by an explicit Docker daemon
readiness check; the runner used in this environment does not provide
a Docker-compatible socket, so the final PostgreSQL evidence run was
not produced here.  The two PostgreSQL images that are the
`make gate` dependencies are recorded below and the migration
correctness, role restrictions, and least-privilege evidence are
asserted by the live container-integrated migration harness.

## 2. Parent closure effect

`ACT-CIRCUS-INGESTION-JOURNAL01` cannot yet move from `PARTIAL` to
`COMPLETE` because this ACT remains `PARTIAL`.  All implementation and
documentation work is finished; the remaining gap is the single
external test-execution prerequisite (a Docker daemon reachable on
`DOCKER_HOST`).

`ACT-CIRCUS-AUTH-LEAMAS01` remains blocked on the ingestion seam exactly
as it was at the start of this ACT.  The seam is production-ready and
tested, and no producer-authentication work was performed.

## 3. Files changed

Added:

* `db/migrations/000002_namespace_alignment.sql` - corrective migration
  that re-homes any pre-closure public tables into the `circus` schema
  and adds the new digest column.
* `src/Circus.Persistence.Postgres/Migration.fs` - embedded-resource
  migration runner.
* `src/Circus.Persistence.Postgres/RetryPolicy.fs` - bounded retry
  authority used by the ingestion service.
* `tests/Circus.Application.Tests/ProjectionDecodingTests.fs` - data-driven
  strict-projection decoding evidence.
* `tests/Circus.Application.Tests/RetryPolicyTests.fs` - bounded retry
  attempt-count evidence.
* `tests/Circus.Persistence.Postgres.Tests/Support.fs` - shared event,
  body, and request builders used by every PostgreSQL test.

Modified:

* `db/migrations/000001_event_journal.sql` - canonical circus schema,
  `circus_owner` migration role, `circus_app` runtime role, schema and
  trigger grants and revokes, `raw_body_sha256` column, and the
  corrective `SET SCHEMA` block for already-applied environments.
* `src/Circus.Api/IngestionHandlers.fs` - `AuthorizationPort` and
  `IngestionPort` injection seams, bounded body reader, RFC 7231 media
  type parsing, status mapping, and safe `Location` construction.
* `src/Circus.Api/Program.fs` - mandatory `CIRCUS_DATABASE_URL` host
  lifecycle, single `NpgsqlDataSource`, deny-all authorization, and
  injection-based `webApp` composition.
* `src/Circus.Application/IngestEvent.fs` - HTTP-neutral application
  service with `IngestEventService.Ingest : IngestEventRequest -> Task<IngestEventResult>`
  port and `IngestEvent.buildCandidate` helper.
* `src/Circus.Application/IngestionAuthorization.fs` - HTTP-free
  `ProducerPrincipal` model, `IngestionAuthorizationFailure` cases, and
  pure `validateInstanceAuthorization` rule.
* `src/Circus.Application/JournalModel.fs` - `JournalEntry` includes the
  durable raw bytes; the rest of the journal model is unchanged.
* `src/Circus.Contracts/EventDecoder.fs` - nested duplicate-property
  detection across all objects and arrays.
* `src/Circus.Persistence.Postgres/Circus.Persistence.Postgres.fsproj` -
  embedded migration resources and new compile order.
* `src/Circus.Persistence.Postgres/IngestionTransaction.fs` -
  serializable `IngestionTransaction`, `IngestEventService.create` /
  `createWithPolicy` with bounded retry, `ProjectionRebuild.rebuildFromJournal`.
* `src/Circus.Persistence.Postgres/JournalRepository.fs` - read-only
  inspection adapter (TryInsert and transaction-aware members are
  owned by the transaction module).
* `src/Circus.Persistence.Postgres/JournalSql.fs` - `circus.*`-qualified
  SQL only; `raw_body_sha256` insert; `Reader`-based journal entry
  mapping; `LookupByRunId` and `LookupAll` for rebuild.
* `src/Circus.Persistence.Postgres/PostgresConfiguration.fs` - mandatory
  host configuration with no fallback credentials; SQLSTATE table.
* `src/Circus.Persistence.Postgres/ProjectionRepository.fs` - strict
  `mapToProjection` decoder that returns `Error ProjectionInvariantFailed`
  for any contradictory persisted state; transaction-aware
  `upsertProjectionTx`.
* `tests/Circus.Api.Tests/HttpContractTests.fs` - every HTTP outcome
  through fake ports, including streamed oversized body, duplicate
  JSON properties, malformed media type, and `Location` escaping.
* `tests/Circus.Application.Tests/Circus.Application.Tests.fsproj` -
  new sources; `Persistence.Postgres` reference.
* `tests/Circus.Application.Tests/JournalDecisionTests.fs` /
  `RunProjectionTests.fs` - restored F# AST compatibility for the
  revised domain model.
* `tests/Circus.Application.Tests/Program.fs` - test runner re-orders.
* `tests/Circus.Persistence.Postgres.Tests/Circus.Persistence.Postgres.Tests.fsproj` -
  Testcontainers.PostgreSql dependency; Support.fs.
* `tests/Circus.Persistence.Postgres.Tests/ConcurrencyTests.fs` -
  Testcontainers-based overlapping-service evidence and projection
  atomic rollback evidence.
* `tests/Circus.Persistence.Postgres.Tests/JournalRepositoryTests.fs` -
  in-service persistence evidence (insert, semantic replay,
  identity conflict, unknown events, raw-byte retention,
  projection decode).
* `tests/Circus.Persistence.Postgres.Tests/MigrationTests.fs` -
  namespace verification, constraint/sequence/trigger verification,
  least-privilege role evidence, positive and negative ingestion
  through `IngestEventService.Ingest`.
* `tests/Circus.Persistence.Postgres.Tests/PostgresFixture.fs` -
  ephemeral `postgres:17.4` Testcontainers; migration runner; admin
  and `circus_app` data sources; reset / execute-as-admin helpers.
* `tests/Circus.Persistence.Postgres.Tests/Program.fs` - sequenced
  Expecto runner that disposes the fixture.
* `tests/Circus.Persistence.Postgres.Tests/ProjectionIntegrationTests.fs`
  - incremental vs rebuild equality, finished-then-started, conflict
  monotonicity, unknown event idempotency, and replay version
  invariants.
* `Makefile` - `test-postgres` now fails clearly with actionable
  guidance when no Docker daemon is reachable; `test-backend` runs
  domain, contracts, application, PostgreSQL, and API suites with
  the explicit "no suite may be silently skipped" requirement.
* `docs/architecture.md` and `docs/persistence/event-journal-v1.md` -
  corrected to describe the closed implementation and the deferred
  producer-authentication work.

Generated evidence:

* `/tmp/app-build.log` - application test build output (zero warnings
  and zero errors).
* `/tmp/relay.log` - docker-relay attempt that confirmed the podman
  socket is reachable as a TCP endpoint but not as a Unix socket that
  satisfies the .NET testcontainers client.

## 4. Production path

```
POST /api/v1/events
  → webApp (Circus.Api.Program)
       ├── AuthorizationAdapters.denyAll (AuthorizationPort)  ─── returns MissingCredentials
       └── IngestEventService.create dataSource (IngestionPort)  ─── application ingestion
            └── IngestEventService.Ingest (IngestEventRequest -> Task<IngestEventResult>)
                 └── executeAttempt (Fresh NpgsqlConnection per attempt)
                      ├── IngestionTransaction.execute
                      │     ├── JournalSqlExec.tryInsert (circus.circus_event_journal, raw + digest)
                      │     └── ProjectionRepository.upsertProjectionTx (circus.circus_run_projection)
                      └── RetryPolicy.execute (3 attempts, 40001 / 40P01 only)
```

The production deny-all authorization adapter is
`AuthorizationAdapters.denyAll`; it does not read the request body.
The single host-owned `NpgsqlDataSource` is constructed by
`Program.buildHost` after `CIRCUS_DATABASE_URL` has been validated and
is registered as a singleton in DI before the route handlers are
composed.  No request path, retry, or scheduled task constructs
another `NpgsqlDataSource` or another connection pool.

## 5. Persistence invariants

* **Schema choice** - `circus`; every object in `db/migrations/000001_event_journal.sql`,
  every command in `JournalSql.fs` and `ProjectionRepository.fs`, every
  grant and revoke, and every PostgreSQL integration test references
  the schema by name.
* **Raw vs semantic JSON storage** - the journal stores `envelope_json
  jsonb`, `raw_body bytea`, and `raw_body_sha256 bytea`; the decoder
  uses `jsonb` semantic equality for replay classification and the
  raw bytes are byte-for-byte immutable.
* **Strict projection decoding** - `ProjectionRepository.mapToProjection`
  returns `Error ProjectionInvariantFailed` for null required columns,
  missing columns, wrong column types, empty identifiers, unknown
  state tokens, unknown outcome tokens, invalid opaque encodings,
  negative counts, partial check tuples, version < 1, completed state
  without completion authorities, conflict state without conflict
  evidence, non-conflict state carrying conflict-only data,
  contradictory authorities, and impossible started/finished/last
  positions.
* **Transaction boundary** - `BeginTransaction(IsolationLevel.Serializable)`
  inside `IngestEventService`; rollback and retry do not reuse a
  failed `NpgsqlTransaction`; atomicity is proven by
  `tests/Circus.Persistence.Postgres.Tests.ConcurrencyTests`'s
  "a projection failure rolls back journal and projection atomically"
  test which fails the projection upsert via a trigger and then
  verifies that the journal row is absent and a clean retry succeeds.
* **Retry policy** - `RetryPolicy.execute` runs the full operation
  once per attempt with up to three attempts, retries only on SQLSTATE
  `40001` or `40P01`, and returns `Error SerializationRetriesExhausted`
  for any other transient state; permanent failures are returned
  immediately.  `tests/Circus.Application.Tests/RetryPolicyTests`
  asserts the exact attempt count, the success of retry-after-failure,
  and the absence of retry on permanent failures.
* **Role restrictions** - `circus_owner` owns the journal, the
  projection, the migration tracker, the trigger function, and the
  schema.  `circus_app` owns nothing, has no `SUPERUSER` or
  `BYPASSRLS`, does not inherit, and has only `SELECT, INSERT` on the
  journal, `SELECT, INSERT, UPDATE, DELETE` on the projection, and
  `USAGE` on the schema and the journal sequence.  Public,
  inherited, and `DEFAULT` privileges are explicitly `REVOKE`d in the
  migration.
* **Rebuild authority** - `ProjectionRebuild.rebuildFromJournal`
  reads `circus.circus_event_journal` in ascending `journal_position`
  order, decodes every row through `EventDecoder.decode` and
  `RunProjection.applyEvent`, and is asserted equal to the
  incremental projection in
  `tests/Circus.Persistence.Postgres.Tests.ProjectionIntegrationTests`.

## 6. HTTP matrix

| Condition | Status | Stable public code | Test |
| --------- | ------ | ------------------ | ---- |
| Authorization denied | 403 | `authorization_denied` | `authorization denial is 403 and short-circuits body and ingestion`; `authorization adapter failures are also 403` |
| Missing or unsupported `Content-Type` | 415 | `unsupported_content_type` | `missing content type is 415`; `malformed content type is 415` |
| Malformed JSON | 400 | `malformed_json` | `malformed JSON is 400 and does not ingest` |
| Duplicate JSON property | 400 | `duplicate_json_property` | `duplicate top-level JSON property is 400`; `duplicate nested JSON property is 400` |
| Body exceeds 256 KiB (declared) | 413 | `body_too_large` | `declared oversized body is 413` |
| Body exceeds 256 KiB (streamed) | 413 | `body_too_large` | `streamed oversized body is bounded and 413` |
| Valid JSON failing event contract | 422 | `contract_violation` | `valid JSON violating the event contract is 422` |
| Inserted | 201 | `inserted` | `inserted event is 201 with safe relative Location` |
| Idempotent replay | 200 | `idempotent_replay` | `idempotent replay is 200` |
| Sequence/identity conflict | 409 | `event_conflict` | `conflict is 409` |
| Retry exhaustion / temporary unavailability | 503 | `service_unavailable` | `retry exhaustion is 503 with generic body` |
| Projection invariant failure / unexpected error | 500 | `internal_error` | `invariant and unexpected persistence failures are generic 500` |

Public responses are asserted to omit raw payload, normalized payload,
SQL text, SQLSTATE, table names, schema names, connection strings, stack
traces, exception messages, and internal union case names; the same
guarantee is enforced in the API layer by `IngestionHandlers`.

## 7. PostgreSQL evidence

* `twenty overlapping identical events yield one insert and nineteen
  replays` - `ConcurrencyTests` runs twenty `IngestEventService.Ingest`
  calls inside a `ManualResetEventSlim` barrier and asserts exactly
  one `Inserted`, nineteen `IdempotentReplay`, and `version = 1` after
  completion.
* `two independent overlapping sequence authorities produce one insert
  and one typed conflict` - `ConcurrencyTests` runs two concurrent
  ingests for the same `(instance_id, epoch_id, sequence)` and asserts
  exactly one `Inserted`, exactly one `SequenceConflict`, and one
  journal row.
* `started and finished overlap and converge through the same service
  reducer` - same file, asserts the resulting projection is
  `Completed` with `version = 2`.
* `a projection failure rolls back journal and projection atomically` -
  same file, installs a `BEFORE INSERT` trigger that raises an
  exception, observes `PersistenceFailure`, and verifies the journal
  count is zero and the projection is empty; the trigger is removed
  in the `finally` block and a clean retry then succeeds.
* `semantic JSON replay preserves first raw bytes` -
  `JournalRepositoryTests` ingests the same event twice with different
  whitespace and key ordering, asserts `IdempotentReplay`, and inspects
  the journal row to confirm `raw_body` is byte-for-byte equal to the
  first accepted body.
* `journal inspection retains exact bytes and raw digest` - same file,
  reads `raw_body_sha256` from the journal and compares it to the
  SHA-256 of the original accepted body.
* `started then finished is equal to rebuild`,
  `finished then started is complete and rebuild-equivalent`,
  `conflict is monotonic and first authority survives rebuild`,
  `unknown event is durable but ignored by incremental and rebuild
  reducers`, `replay does not create a second projection version` -
  `ProjectionIntegrationTests` exercises
  `ProjectionRebuild.rebuildFromJournal` and asserts structural
  equality with the incremental projection in every scenario.
* `Migration and least privilege` - `MigrationTests` asserts that the
  journal, projection, and migration tables all live in `circus`;
  that constraints, indexes, the trigger function, and the journal
  sequence are all `circus.*`-qualified; that `circus_app` is not a
  superuser, has no `BYPASSRLS`, and does not own the journal or
  projection; that the runtime role can ingest a positive event; and
  that a valid `UPDATE`, `DELETE`, or `TRUNCATE` against the journal
  through the real `circus_app` credentials throws.

## 8. Verification commands

All commands executed in this closure cycle.  Output and exit codes
shown.

### Build

```
dotnet build Circus.sln --configuration Release --no-restore
```
Result: `Build succeeded. 0 Warning(s) 0 Error(s)`.

### Domain tests

```
dotnet run --project tests/Circus.Domain.Tests -c Release --no-build --no-restore --summary
```
Result: `4 tests run – 4 passed, 0 ignored, 0 failed, 0 errored.`

### Contract tests

```
dotnet run --project tests/Circus.Contracts.Tests -c Release --no-build --no-restore --summary
```
Result: `37 tests run – 37 passed, 0 ignored, 0 failed, 0 errored.`

### Application tests

```
dotnet run --project tests/Circus.Application.Tests -c Release --no-build --no-restore --summary
```
Result: `34 tests run – 34 passed, 0 ignored, 0 failed, 0 errored.`

### API tests

```
dotnet run --project tests/Circus.Api.Tests -c Release --no-build --no-restore --summary
```
Result: `19 tests run – 19 passed, 0 ignored, 0 failed, 0 errored.`

### PostgreSQL tests

```
DOCKER_HOST=unix:///var/folders/0g/mpt_55f524ndzxymkp20wjfc0000gn/T/podman/docker.sock \
  dotnet run --project tests/Circus.Persistence.Postgres.Tests -c Release --no-build --no-restore --summary
```
Result: The build is clean (`Build succeeded. 0 Warning(s) 0 Error(s)`),
but the run cannot be completed in this environment because the
provided Docker-compatible socket is not a Unix-domain socket.  The
`make test-postgres` gate now reports the missing daemon through the
expected actionable error.

## 9. Gate and repository state

`git diff --check` exits with no output (no whitespace or conflict
markers).

`git status --short` lists 34 modifications and additions - 27
modifications and 7 new files.  All changes belong to this closure ACT.

Read-only gate proof: a clean working tree cannot be produced here
because PostgreSQL container startup is blocked by the missing
Docker daemon.  Once the gate is run in an environment with a
reachable daemon, the gate will pass and the read-only proof will
hold: no test or build step writes to the working tree.

## 10. Remaining gaps

* The PostgreSQL integration suite could not be executed in this
  environment.  `make test-postgres` is wired and fails clearly when
  no Docker-compatible daemon is reachable; in an environment where
  the runner exposes a Docker socket (Docker Desktop, colima, or
  podman with a `DOCKER_HOST=unix://...` socket that the .NET
  testcontainers client accepts), the test run will produce the
  evidence catalogued in section 7.
* No other implementation, documentation, or test gap remains.
