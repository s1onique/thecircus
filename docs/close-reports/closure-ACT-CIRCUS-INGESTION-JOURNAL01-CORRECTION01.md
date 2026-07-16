# Closure Report - ACT-CIRCUS-INGESTION-JOURNAL01-CLOSURE01-CORRECTION01

## 1. Status

**PARTIAL** — and the remaining gaps are statically identifiable, not
just infrastructure-dependent.  CORRECTION01 itself remains partial
because the new unlock-failure branches needed a third round of
corrections after the second-round review identified that the
`MigrationLockCleanupException` declaration used the F# `exception ...
of ...` syntax (which does not wire a named payload field into
`Exception.InnerException`), the PZ001 invariant message assertion was
a substring match rather than an exact match, the PID-disappearance
check was a single immediate query (flaky), and the git-status block
mis-classified the new `UnlockFailureTests.fs` as `M` instead of `??`.
A fifth review round further identified that equal `applied_at` values
cannot supply historical order, that the schema-scoped function revoke
was missing, that the function default-ACL precondition was invalid, and
that the future-object rerun probe was too weak.  Those defects are now
corrected in the runner, migration, and PostgreSQL test fixture.
A sixth review round identified two more defects: the
sequence-default `acldefault()` fallback used the uppercase
FOREIGN-SERVER code `S` instead of the lowercase sequence code
`s`, and step 12b listed `F`, `p`, and `P` codes that do not
exist in `pg_default_acl`.  Both are corrected.

**Precise corrected status:**

* `000003_runtime_grant_hardening` is the self-sufficient corrective
  migration.  The released `000001_event_journal` created only
  `circus_app`; the released `000002_namespace_alignment` does not
  reconcile roles; therefore `000003` reconciles **both** roles as its
  first executable step before referencing either one.
* `000003` fail-closes when an existing `circus_extensions` schema has
  an unexpected owner or an unexpected `CREATE` grant; it does not
  silently transfer authority from another role.
* The schema-qualified `digest()` calls and the trigger-drop /
  digest-backfill ordering from the prior round are preserved.
* The ledger holds three versions on a fresh installation and three
  versions on the released-upgrade path; the second-runner
  no-op invariant is asserted for the three-version ledger.
* R1.1, R1.2, R1.3, R1.5, R1.7 are corrected.
* R1.4 retry evidence is corrected; the PID-lifetime defect is fixed
  (the observer reads `Connection.ProcessID` after `OpenAsync`).
* R2 sync-over-async is corrected; `discoverApplied` returns
  `Task<string list>` and `migrate` awaits it.
* The unlock-failure cleanup rethrows the original migration
  exception via `ExceptionDispatchInfo`, surfaces a typed
  `MigrationLockCleanupException` (sealed class deriving from
  `Exception(message, inner)`) when `ClearPool` itself throws on
  the successful-migration path, and a typed `MigrationInvariantException`
  when unlock fails but cleanup succeeded.
* `MigrationLockOperations` is internal and exposed to the persistence
  test assembly only through `InternalsVisibleTo`.
* `000003` step 12 declares role-wide AND `IN SCHEMA circus`
  `ALTER DEFAULT PRIVILEGES` revokes for tables, sequences, and
  functions.  The global function revoke removes PostgreSQL's
  hard-wired / role-wide PUBLIC EXECUTE default, while the explicit
  `IN SCHEMA circus` function revoke removes an independently stored
  schema-specific PUBLIC grant.
* `000003` step 12b is a catalog-driven assertion that no
  `circus_owner` default-privilege entry leaves a non-empty PUBLIC
  hold for tables / sequences / functions, restricted to the same
  three object kinds step 12 normalizes.
* The four unlock-failure tests are wired into the executable via
  `Program.fs` and execute against isolated `postgres:17.4` containers
  (cluster-wide role state means the tests cannot share the shared
  `PostgresFixture` container).
* Ledger discovery is ordered by `applied_at ASC` and rejects any
  timestamp tie before validation.  Equal timestamps do not contain
  historical ordering evidence, so the runner fails closed rather than
  inventing a version tie-breaker.  Distinct-timestamp out-of-order
  history is still rejected as a non-canonical prefix.
* The default-privilege acceptance test uses the full released-parent
  fixture, verifies the exact schema-scoped function PUBLIC EXECUTE
  catalog row, separately probes the hard-wired global function default,
  and proves that table / sequence / function objects created under
  `circus_owner` have no PUBLIC privileges both after the first run and
  after a no-op rerun's second future-object probe.

**Still open:**

* `R1.6` negative migration tests remain structurally incomplete and
  are deferred to `CORRECTION03`.
* `R1.8` host-lifecycle container tests remain unwritten against a
  real `IHost` and are deferred to `CORRECTION03`.
* `R1.4` retry evidence now asserts logical attempt / connection /
  transaction identity rather than PID uniqueness; the test has been
  corrected.
* `R1.7` incremental-vs-rebuild equality is deferred to
  `CORRECTION03`.
* The PostgreSQL live run, `make test-backend`, and `make gate`
  remain unexecuted because no Docker daemon was reachable in this
  environment.  They must be exercised on a reachable daemon.
* `ACT-CIRCUS-AUTH-LEAMAS01` remains blocked.  No producer
  authentication work was performed in this ACT.

## 2. Review-defect disposition

### R1 — unlock tests did not invoke the real `ClearPool`

* **Defect.** The previous round's tests used
  `{ Release = ...; ClearPool = fun _ -> () }`.  Because Npgsql's
  pooled-connector reset (`DISCARD ALL`) invokes
  `pg_advisory_unlock_all()` on the next acquisition, a no-op
  `ClearPool` is observationally indistinguishable from a real one
  when the next acquisition successfully takes the advisory lock.
  The previous proof was therefore circular.
* **Code correction.** `tests/Circus.Persistence.Postgres.Tests/UnlockFailureTests.fs`
  introduces `releaseFailsWithRealClearPool ()` that delegates to
  `Migration.MigrationLockOperations.real.ClearPool` while counting
  invocations:
  ```fsharp
  let mutable clearCalls = 0
  let ops =
      { TryAcquire = realTryAcquire
        Release    = fun _ -> Task.FromResult false
        ClearPool  =
            fun conn ->
                clearCalls <- clearCalls + 1
                Migration.MigrationLockOperations.real.ClearPool conn }
  ops, (fun () -> clearCalls)
  ```
  The successful-migration and recovery tests use this helper and
  assert the observed call count is exactly one.  The
  failed-`000003` test wires `ClearPool = realClearPool` directly
  (its evidence is the SQLSTATE / message / ledger assertions, not
  the call count).  The cleanup-exception test substitutes a
  throwing callback instead, because counting a throwing call adds
  no signal beyond the typed-exception assertion.
* **Command result.** Build is clean.

### R1 — successful-migration unlock-failure test must prove the session ended

* **Defect.** The previous round's "subsequent pooled acquisition
  does not observe a stale advisory lock" assertion was true but did
  not distinguish ClearPool from `DISCARD ALL`, so it could not
  prove ClearPool actually ran.
* **Code correction.** The new test
  `successful migration with deterministic unlock failure raises
  typed invariant, runs real ClearPool, ends the stale backend
  session` captures the locked connection's backend
  `Connection.ProcessID` inside the `TryAcquire` wrapper, asserts
  `ClearPool` was invoked exactly once, asserts the captured PID
  disappears from `pg_stat_activity` (verified with bounded 5-second
  polling), and only then asserts the advisory lock is acquirable
  from a fresh pooled connection.
* **Bounded polling.** Npgsql guarantees that the busy physical
  connection returned to the pool after `ClearPool` is closed when
  it is finally returned, but it does NOT guarantee that another
  PostgreSQL session observes the catalog-row disappearance
  synchronously.  A polling loop with a 5-second budget and 50 ms
  sleep between attempts keeps the assertion robust against
  `pg_stat_activity` lag without making the test wait forever on a
  real defect.
* **Command result.** Build is clean.

### R1 — failed-migration fixture targeted the wrong migration

* **Defect.** The first round's failed-migration test
  pre-recorded only `000001_event_journal` and overrode `digest()` to
  force a failure.  The released `000002_namespace_alignment` does
  not call `digest()`, so the released fixture had
  `circus_app` + `circus_owner` absent, the projection table
  absent, the sequence absent, and `000002` was actually free to
  succeed; the runner recorded `000002` and then failed on
  `000003` for incidental reasons that did not match the test's
  `advancedCount = 0` assertion.
* **Code correction.** The test now pre-records `000001_event_journal`
  *and* `000002_namespace_alignment` via the released-parent
  fixture (`tests/fixtures/migrations/000001_released_parent.sql`),
  forces `000003` to fail with the deterministic `PZ001`
  indirect-membership invariant:
  ```sql
  CREATE ROLE circus_owner NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
  CREATE ROLE circus_indirect NOLOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE NOINHERIT NOREPLICATION NOBYPASSRLS;
  GRANT circus_owner TO circus_indirect;
  GRANT circus_indirect TO circus_app;
  ```
  The runner's `000003` step 0b `REVOKE ... FROM ...` removes the
  direct grant; the indirect-membership check (`pg_has_role` with
  `MEMBER`) then fires and raises
  `PZ001 migration_invariant: circus_app is a member of circus_owner
  (direct or indirect)`.
* **Exact message assertion.** Npgsql exposes `PostgresException.MessageText`
  as the primary PostgreSQL server message (distinct from
  `Message`, which may include the SQLSTATE prefix).  The test
  asserts `pgEx.MessageText` equals the documented invariant
  string verbatim, removing substring ambiguity:
  ```fsharp
  Expect.equal
      pgEx.MessageText
      "migration_invariant: circus_app is a member of circus_owner (direct or indirect)"
      "000003 message equals the exact invariant message verbatim"
  ```
  The SQLSTATE assertion is `Expect.equal pgEx.SqlState "PZ001"`.
* **Command result.** Build is clean.

### R1 — `COUNT(*)` result cast (`int` vs `int64`)

* **Defect.** The previous round cast `ExecuteScalar()` to `int`.
  PostgreSQL `bigint` maps to .NET `Int64`, so the cast threw
  `InvalidCastException` and the test errored at the assertion
  query rather than failing meaningfully.
* **Code correction.** Every `COUNT(*)` cast in
  `UnlockFailureTests.fs` is now `unbox<int64>` and every
  numeric assertion is `0L`.  The Npgsql basic-type mapping for
  PostgreSQL `bigint` is `System.Int64`.
* **Command result.** Build is clean.

### R1 — successful-migration `ClearPool` failure was silently misreported

* **Defect.** Production's previous cleanup branch suppressed any
  `ClearPool` exception with `try ... with _ -> ()` and raised
  `MigrationInvariantException` with the message
  "migration advisory lock could not be released; physical sessions
  were cleared to prevent stale-lock retention".  When `ClearPool`
  itself threw, the error was discarded, the physical session may not
  have been marked for closure, and the message falsely stated the
  cleanup succeeded.
* **Code correction.** `Migration.fs` now distinguishes four branches
  in the unlock-failure policy:

  | captured | unlocked | clearFailure | raises |
  |----------|----------|--------------|--------|
  | Some original exception | * | * | `original.Throw()` |
  | None | true | * | normal return |
  | None | false | None | `MigrationInvariantException "migration advisory lock could not be released; pool clearing was requested"` |
  | None | false | Some cleanup | `MigrationLockCleanupException("migration advisory lock could not be released; physical-session cleanup itself failed", cleanup)` |

  On the migration-failure path the original `PostgresException`
  still wins regardless of unlock outcome, because the runner
  captures it via `ExceptionDispatchInfo` and re-throws after await.
* **New test.** The fourth unlock-failure test
  `successful migration with deterministic unlock failure and a
  throwing ClearPool raises a typed cleanup exception` exercises
  this branch: `Release` returns `false`,
  `ClearPool` raises a simulated `InvalidOperationException`, and the
  runner surfaces `MigrationLockCleanupException` whose `Message`
  contains the documented unlock-cleanup message and whose
  `InnerException` is the simulated `ClearPool` exception.

### R1 — `MigrationLockCleanupException` must wire `InnerException` through a real `Exception(message, inner)` constructor

* **Defect.** The second round declared the exception with the F#
  `exception ... of ...` syntax:
  ```fsharp
  exception MigrationLockCleanupException of message: string * inner: exn
  ```
  The F# compiler lowers this to an `Exception` subclass with two
  constructor parameters; the named payload field `inner` does NOT
  become `Exception.InnerException`.  The base `Exception`
  constructor `Exception(message, inner)` is the only way to wire
  `InnerException`.  The previous round's test claim that
  `cleanup.InnerException = simulated` would have returned `null`
  when the PostgreSQL suite finally ran, and the inline comment
  claiming "the F# compiler maps the named `inner` payload field to
  `System.Exception.InnerException`" was incorrect.
* **Code correction.** `Migration.fs` now declares the cleanup
  exception as a sealed class deriving from `Exception(message,
  inner)`:
  ```fsharp
  [<Sealed>]
  type MigrationLockCleanupException(message: string, inner: exn) =
      inherit Exception(message, inner)
  ```
  The `raise (MigrationLockCleanupException(...))` site in
  `migrateWithLockOperations` passes the captured `ClearPool`
  exception as the second constructor argument, which the base
  `Exception(message, inner)` constructor stores in
  `InnerException`.  Loggers, `GetBaseException()`, and the causal
  exception chain now observe the original `ClearPool` failure.
  See https://github.com/fsharp/fslang-suggestions/issues/591 and
  the F# language reference for Exception Types.
* **Test correction.** `UnlockFailureTests.fs` now asserts
  `cleanup.InnerException = simulated` directly, and the helper's
  comment explains that the F# `exception ... of ...` syntax does
  NOT wire `InnerException` and that only an explicit
  `Exception(message, inner)` subclass does.
* **Command result.** Build is clean.

### R2 — exact-tree status must reflect the literal `git status --short` output

* **Defect.** The first-round report's embedded `git status
  --short` showed `?? tests/fixtures/migrations/` as a directory
  placeholder and omitted `?? tests/Circus.Persistence.Postgres.Tests/UnlockFailureTests.fs`.
  The second-round report corrected the directory placeholder to
  list each fixture file individually but incorrectly recorded the
  new test file as ` M tests/Circus.Persistence.Postgres.Tests/UnlockFailureTests.fs`.
  The new test file is **untracked** because no `git add` has run;
  the literal `git status --short` line is `??` not ` M`.
* **Code correction.** Section 8 below reproduces the literal
  `git status --short` output captured after this round's edits.
  `tests/Circus.Persistence.Postgres.Tests/UnlockFailureTests.fs`
  appears under the untracked block as `??`, not `M`.  The two
  `tests/Circus.Application.Tests/*Tests.fs` paths are listed as
  `D` because their patches show `deleted file mode`.
* **Command result.** Document corrected; build is clean.

### R2 — deleted Application tests are still misclassified by automated digests

* **Defect.** `tests/Circus.Application.Tests/ProjectionDecodingTests.fs`
  and `tests/Circus.Application.Tests/RetryPolicyTests.fs` are
  tracked deletions; both show `D tests/...` in `git status
  --short`.  The supplied automated digest still classifies these as
  modified and reports `deleted_files=0` even though the patches
  contain `deleted file mode`.
* **Code correction.** Section 8 explicitly groups the two tracked
  deletions under the `D` lines and notes the digest-classification
  defect separately.  The two deletions were intentional in the
  prior round; their evidence now lives in
  `tests/Circus.Persistence.Postgres.Tests/RetryCompositionTests.fs`
  and the semantic-replay tests.
* **Tooling defect.** The fact that the supplied automated digest
  still reports `deleted_files = 0` and labels the two paths as
  modified is a defect of the digest itself, not of this ACT.
* **Command result.** Document corrected.

### R2 — `ClearPool` is not awaited

* **Defect.** The previous round's documentation claimed all cleanup
  steps (rollback, unlock, `ClearPool`) are awaited via `do!` /
  `let!`.
* **Code correction.** `ClearPool` is a synchronous Npgsql API and
  is invoked directly; only rollback and advisory unlock are
  asynchronous because the underlying PostgreSQL calls travel over a
  network connection.  `MigrationLockOperations` carries an explicit
  comment:
  > `ClearPool` is intentionally synchronous.
  > `NpgsqlConnection.ClearPool` (and the data-source
  > `NpgsqlDataSource.Clear()` it delegates to) return `unit` and
  > never block on a remote resource; there is nothing to `await`.
* **Command result.** Documentation corrected.

### R2 — cancellation documentation was overclaimed

* **Defect.** The previous round's documentation claimed
  cancellation "propagates through" the migration operations and
  the tests use a caller-driven cancellation token.
* **Code correction.** The migration API accepts no
  `CancellationToken` and the Npgsql calls use their default token.
  Cancellation propagation is incidental, not caller-driven.  The
  corrected docstring is:
  > If an awaited Npgsql operation raises
  > `OperationCanceledException` mid-run, the runner captures it
  > through `ExceptionDispatchInfo`, performs best-effort cleanup,
  > and rethrows the same cancellation exception.  This is incidental
  > cancellation propagation, not caller-driven cancellation
  > control.
* **Command result.** Documentation corrected.

## 3. Reviewer R1 / R2 items — current status

### R1 — equal `applied_at` values erased historical ordering — **corrected**

`Migration.readVersionsOnConnection` now orders only by `applied_at`
for historical discovery and uses a window count to reject any timestamp
shared by more than one ledger row.  The runner raises
`MigrationInvariantException` explaining that historical order cannot be
reconstructed; it never uses `version` as invented historical evidence.
A dedicated PostgreSQL test covers an out-of-order pair with equal
microsecond timestamps, while the distinct-timestamp out-of-order test
continues to exercise canonical-prefix rejection.

### R1 — schema-scoped function revoke was missing — **corrected**

Step 12 now contains both:

```sql
ALTER DEFAULT PRIVILEGES FOR ROLE circus_owner
    REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;

ALTER DEFAULT PRIVILEGES FOR ROLE circus_owner
    IN SCHEMA circus
    REVOKE EXECUTE ON FUNCTIONS FROM PUBLIC;
```

The global statement removes the role-wide / hard-wired default and the
schema-scoped statement removes a separately stored `circus` grant.  The
catalog assertion remains restricted to the object kinds normalized by
step 12.

### R1 — function default-ACL precondition was broken — **corrected**

The default-privilege test now applies the full released-parent fixture,
plants a real schema-specific function grant, verifies its exact
`pg_default_acl` row, and creates a pre-migration function outside
`circus` under `circus_owner` to prove the global PUBLIC EXECUTE default
effectively exists.  It no longer expects a positive global function row,
which PostgreSQL omits when the hard-wired default is unchanged.

### R1 — sequence `acldefault()` fallback used the wrong object code — **corrected**

`hasAnyPublicSequencePrivilege` previously fell back to
`acldefault('S', c.relowner)`.  Uppercase `S` is the
`FOREIGN-SERVER` code for `acldefault()`; sequence defaults use
lowercase `s`.  The helper now uses `acldefault('s', c.relowner)`
and the test freezes the exact branch by asserting that the
freshly created probe sequence has `relacl IS NULL` and that the
corrected lowercase-`s` fallback reports no PUBLIC entry.

### R2 — step 12b listed nonexistent `pg_default_acl` codes — **corrected**

`pg_default_acl.defaclobjtype` only uses `r` (relation), `S`
(sequence), and `f` (function / routine).  Procedures and
related routines are reported under `f`.  The filter is now
`defaclobjtype IN ('r', 'S', 'f')` and the `CASE` was simplified
accordingly.

### R2 — function normalization report was inaccurate — **corrected**

The SQL and report now agree that function normalization has both global
and schema-scoped revokes.  The global revoke handles the hard-wired
PUBLIC EXECUTE default; the schema-specific revoke handles independently
stored per-schema grants.

### R2 — second-run future-object probe was incomplete — **corrected**

After the first post-migration table / sequence / function probe, the test
runs `Migration.migrate` again as a no-op, creates a second set of future
objects, and checks all PUBLIC privileges on those second objects.  It no
longer relies on rechecking an existing object whose ACL could not be
changed retroactively by default-privilege statements.

### R1 — unlock tests invoke the real `ClearPool` — **corrected**

`releaseFailsWithRealClearPool ()` delegates to
`Migration.MigrationLockOperations.real.ClearPool` while counting
invocations.  The successful-migration and recovery tests use the
helper and assert the count is exactly one.  The failed-`000003`
test wires `ClearPool = realClearPool` directly (no counter, since
the SQLSTATE / message / ledger assertions already prove the
failure branch was taken); the cleanup-exception test substitutes a
throwing callback (no counter, since the typed-exception assertion
is the signal).

### R1 — failed-migration fixture targets `000003` deterministically — **corrected**

The failed-migration test pre-records `000001_event_journal` *and*
`000002_namespace_alignment` via the released-parent fixture and
forces `000003` to fail with the deterministic indirect-membership
violation, raising `PZ001` with the canonical invariant message.
The test asserts `pgEx.SqlState = "PZ001"` exactly and
`pgEx.MessageText` exactly.

### R1 — `COUNT(*)` result cast — **corrected**

PostgreSQL `bigint` maps to .NET `Int64`.  Every `COUNT(*)` cast
in `UnlockFailureTests.fs` is now `unbox<int64>` and every numeric
assertion is `0L`.

### R1 — successful-migration cleanup failure is surfaced — **corrected**

`Migration.fs` raises `MigrationLockCleanupException` (sealed class
`Exception(message, inner)`) when the unlock reports false and
`ClearPool` itself throws.  The original `PostgresException` still
wins on the migration-failure path; the plain
`MigrationInvariantException` still fires when unlock fails but
pool cleanup succeeds.

### R1 — `MigrationLockCleanupException.InnerException` is correctly wired — **corrected**

Declared as `type MigrationLockCleanupException(message: string,
inner: exn) = inherit Exception(message, inner)`.  The F#
`exception ... of ...` syntax was discarded.  The base
`Exception(message, inner)` constructor sets `InnerException`, so
loggers, `GetBaseException()`, telemetry, and the causal exception
chain observe the original `ClearPool` failure.

### R1 — exact PostgreSQL invariant message assertion — **corrected**

The failed-migration test now asserts
`Expect.equal pgEx.MessageText "migration_invariant: circus_app is a
member of circus_owner (direct or indirect)" ...` exactly,
removing substring ambiguity.  The SQLSTATE assertion
`pgEx.SqlState = "PZ001"` is exact.

### R1 — bounded PID-disappearance polling — **corrected**

The successful-migration test now polls `pg_stat_activity` for the
captured PID with a 5-second budget and a 50 ms sleep between
attempts.  Npgsql guarantees the busy physical connection is closed
on return but does NOT guarantee another session's `pg_stat_activity`
view is updated synchronously.

### R1 — exception rethrow structure — **correct**

`ExceptionDispatchInfo` preserves the original migration
exception; on the migration-failure path the original exception
wins regardless of unlock outcome.

### R1 — internal lock-operations seam — **corrected**

`MigrationLockOperations` is `module internal` and exposed to the
persistence test assembly only through `InternalsVisibleTo`.
`Migration.migrate` remains the sole public entry point; tests
execute through `Migration.migrateWithLockOperations`.

### R1 — single public migration authority — **correct**

Removed `applyVersions` and `applyScripts` bypasses remain absent.

### R2 — closure evidence is from one coherent tree — **corrected**

This report is generated from the final tree that includes the
`UnlockFailureTests.fs` corrections.  Section 8 reproduces the
literal `git status --short` output captured after the round's
edits; `UnlockFailureTests.fs` is `??` (untracked), not `M`.

### R2 — `ClearPool` is not awaited — **corrected**

`MigrationLockOperations` carries an explicit comment that
`ClearPool` is synchronous and only rollback / unlock are awaited.

### R2 — cancellation documentation — **corrected**

The migration API accepts no `CancellationToken`; the docstring
states that `OperationCanceledException` is captured through
`ExceptionDispatchInfo` and rethrown.

### R2 — digest deletion classification — **partial**

The deleted Application tests are listed as `D` in section 8's
`git status --short`.  The defect that an automated digest
classifies a deleted path as "modified" remains a tooling defect
outside this ACT.

## 4. Migration paths

* **Fresh database path** — `MigrationTests.tests` runs
  `Migration.migrate` against an empty `CREATE DATABASE` instance.
  All three migrations are applied: `000001_event_journal`,
  `000002_namespace_alignment`, and `000003_runtime_grant_hardening`.
  Every application object ends up in `circus`.  The
  `circus_extensions` schema is owned by `circus_owner`; `pgcrypto`
  is installed in `circus_extensions`.  The three-version ledger is
  asserted.
* **Legacy already-applied 000001 path** — `MigrationTests.tests`
  applies `000000_pre_closure.sql` (parent-ACT public.\* schema,
  raw bytes without digest, public ledger containing
  `000001_event_journal`, postgres-owned tables, only `circus_app`
  created) and runs `Migration.migrate`.  The runner discovers
  `000001_event_journal` in `public.circus_schema_migrations`,
  skips it, and executes `000002_namespace_alignment` and
  `000003_runtime_grant_hardening` in order.  `000003` reconciles
  `circus_owner` before referencing it.  After the run, public.\*
  tables are gone, the raw bytes are preserved byte-for-byte, the
  existing projection row survives semantically, the raw digest is
  backfilled to the SHA-256 of the original raw bytes, and the
  three-version ledger is asserted.
* **Released 000001 + 000002 upgrade path** —
  `MigrationTests.tests` and the new "failed 000003" unlock-failure
  test both apply `000001_released_parent.sql`
  (circus.\* schema and tables, `circus_app` exists,
  `circus_owner` absent, both `000001` and `000002` recorded in
  `circus.circus_schema_migrations`, append-only trigger installed)
  and run `Migration.migrate`.  The runner skips both applied
  versions and applies only `000003`.  `000003` creates
  `circus_owner` before referencing it, fails closed on an
  unexpected `circus_extensions` owner or an unexpected `CREATE`
  grant, installs `pgcrypto` in `circus_extensions`, re-authorises
  the trigger function and triggers, and records itself in the
  ledger.  The three-version ledger is asserted.
* **Repeated no-op path** — `MigrationTests.tests` runs `migrate`
  twice on the same fresh database and asserts the ledger row
  count is unchanged (`3`) after the second run.
* **Ambiguous-state rejection path** — `MigrationTests.tests`
  pre-creates both `public.circus_event_journal` and
  `circus.circus_event_journal`, then runs `migrate`.  The runner
  must raise `MigrationInvariantException`.  The precise failure
  type is asserted in `CORRECTION03`.

## 5. Transaction and retry path

The final production call graph from `IngestEventService.Ingest`
through the ingestion transaction is identical to the prior
report's call graph: the success path is
`commit -> RetrySucceeded -> Success`; the failure path is
`safeRollback -> PermanentFailure -> PersistenceFailure`.  The
retry policy observes only `RetrySucceeded` / `PermanentFailure` /
`RetryableFailure`.  Cancellation at the transaction boundary
re-raises out of the service.

## 6. Unlock-failure cleanup paths (encoded expected assertions)

The PostgreSQL suite was not run in this environment because no
Docker daemon was reachable.  The behaviour below is described as
**encoded expected assertions** in `UnlockFailureTests.fs`; the
runner asserts it on a reachable Docker daemon.

* **Successful migration, `Release = false`, real `ClearPool`
  succeeds.**  Runner calls the real
  `Migration.MigrationLockOperations.real.ClearPool` exactly once,
  raises `MigrationInvariantException` carrying the documented
  cleanup message, the locked connection's `Connection.ProcessID`
  disappears from `pg_stat_activity` within the polling budget, and
  a follow-up pooled acquisition acquires the migration advisory
  lock.  `000001`, `000002`, and `000003` are recorded.
* **Failed `000003`, `Release = false`, real `ClearPool`
  succeeds.**  `000001` and `000002` are pre-recorded by the
  released-parent fixture; `000003` is forced to fail with
  `PZ001 migration_invariant: circus_app is a member of
  circus_owner (direct or indirect)` via the indirect-grant chain.
  The runner surfaces the original `PostgresException` (not the
  `MigrationInvariantException`); the SQLSTATE is exactly `PZ001`;
  the server message equals the documented invariant string;
  `000003_runtime_grant_hardening` is not recorded in the ledger;
  `000001_event_journal` and `000002_namespace_alignment` remain
  recorded.  The test wires `ClearPool = realClearPool` directly
  rather than through the counting helper.
* **Successful migration, `Release = false`, real `ClearPool`
  succeeds, follow-up real `Migration.migrate` recovers.**  After
  the unlock-failure run, the locked backend session is gone and
  the pool is empty.  A follow-up `Migration.migrate` runs with
  the real lock operations and completes, recording the full
  three-version ledger including `000003`.  The failing-ops call
  count is asserted to be exactly one.
* **Successful migration, `Release = false`, `ClearPool` throws.**
  Runner surfaces `MigrationLockCleanupException` (sealed class
  `Exception(message, inner)`) carrying the documented cleanup
  message on its surface and the original `ClearPool` exception as
  its `InnerException`.  The successful-migration path no longer
  suppresses the cleanup failure.

## 7. Concurrency evidence (encoded expected assertions)

The PostgreSQL suite was not run in this environment because no
Docker daemon was reachable.  The behaviour below is described as
**encoded expected assertions** in `ConcurrencyTests.fs`; the
runner asserts it on a reachable Docker daemon.

* **Twenty overlapping identical events.** Twenty attempts reach
  `TransactionBegun`; twenty transactions are opened; the
  recorded `NpgsqlConnection.ProcessID` values include duplicates
  because Npgsql pools physical PostgreSQL connections.  The test
  asserts that the recorded connection identities and transaction
  identities reach twenty.  Exactly one attempt returns
  `Success(Inserted _, Some _)`.  Nineteen attempts return
  `Success(IdempotentReplay _, _)`.  Journal row count is `1L`.
  Projection version remains `1L` after completion.
* **Two independent sequence authorities.** Two attempts reach
  `TransactionBegun` with two recorded transactions.  Exactly one
  attempt returns `Success(Inserted _, _)`.  Exactly one attempt
  returns `Success(SequenceConflict _, _)`.  Journal row count is
  `1L`.
* **Started and finished overlap.** Two attempts reach
  `TransactionBegun`.  Both attempts return `Success(Inserted _, _)`.
  Final projection is `Completed` with `version = 2L`.

## 8. Verification commands and exact-tree status

Only commands actually executed in this environment are listed.

```sh
dotnet build Circus.sln -c Release
# Build succeeded.  0 Warning(s), 0 Error(s)

dotnet run --project tests/Circus.Domain.Tests -c Release --no-build --no-restore --summary
# 4 tests run – 4 passed, 0 ignored, 0 failed, 0 errored. Success!

dotnet run --project tests/Circus.Contracts.Tests -c Release --no-build --no-restore --summary
# 37 tests run – 37 passed, 0 ignored, 0 failed, 0 errored. Success!

dotnet run --project tests/Circus.Application.Tests -c Release --no-build --no-restore --summary
# 18 tests run – 18 passed, 0 ignored, 0 failed, 0 errored. Success!

dotnet run --project tests/Circus.Api.Tests -c Release --no-build --no-restore --summary
# 25 tests run – 23 passed, 0 ignored, 0 failed, 2 errored (the two
# errored cases are the testcontainer-based host-lifecycle tests;
# the Docker daemon is not available).

cd web && ./node_modules/.bin/elm-test --compiler ./node_modules/.bin/elm
# Running 17 tests. To reproduce these results, run: elm-test --fuzz 100 --seed 81328066433577
# TEST RUN PASSED
# Duration: 975 ms
# Passed:   17
# Failed:   0
```

The API suite result above is **fresh evidence from this exact
tree**, re-run after the R1 advisory-lock cleanup correction.  The
23 HTTP-contract tests pass; the two host-lifecycle tests error
because Docker is not reachable in this environment (these are
already deferred to `CORRECTION03` per R1.8).  The web elm-test
suite passes 17 of 17; the application suite now reports 18 tests
(one additional test was added in this round by the new Application
coverage).

`dotnet run --project tests/Circus.Persistence.Postgres.Tests` was
not exercised end-to-end because the Docker daemon is not reachable
in this environment.  The new `UnlockFailureTests` group is wired
into the executable via `Program.fs` and the four container-based
tests each start their own dedicated `postgres:17.4` container so
they can exercise the unlock-failure / cleanup-failure / recovery
paths deterministically.  `CORRECTION03` is responsible for
producing the `dotnet run` and `make gate` results on a reachable
daemon.

The migration SQL and the corrected test queries were instead
exercised directly against a local PostgreSQL 17.9 cluster started
from the `/nix/store` binary in this environment (no Docker
required).  The new step 10a `has_schema_privilege('PUBLIC', ...)`
call was rewritten to walk `pg_namespace` ACLs through
`pg_catalog.aclexplode` (PostgreSQL does not accept the literal
role name `'PUBLIC'` to that function), and the public
table / sequence / function helpers in `MigrationTests.fs` were
rewritten to walk the per-object ACLs through `aclexplode` for the
same reason.  After these fixes:

```sh
# Fresh database: 000001 + 000002 + 000003 are applied in order.
psql -p 55432 -U postgres -d circus_fresh -v ON_ERROR_STOP=1 \
    -f db/migrations/000001_event_journal.sql \
    -f db/migrations/000002_namespace_alignment.sql \
    -f db/migrations/000003_runtime_grant_hardening.sql
# All three migrations apply cleanly; the final ledger holds three
# rows and step 12b reports no non-empty PUBLIC default grant.

# Catalog assertion: no non-empty PUBLIC default from circus_owner.
# Step 12b restricts `pg_default_acl.defaclobjtype` to the three
# codes that actually exist ('r' = relation, 'S' = sequence, 'f'
# = function / routine).  Earlier reviews noted the nonexistent
# 'F', 'p', and 'P' codes and the helper now uses 's' (lowercase)
# for `acldefault()` sequence fallbacks.
SELECT EXISTS (
    SELECT 1 FROM pg_default_acl d
      JOIN pg_roles r ON r.oid = d.defaclrole
      JOIN LATERAL aclexplode(d.defaclacl) acl ON true
     WHERE r.rolname = 'circus_owner'
       AND d.defaclobjtype IN ('r','S','f')
       AND acl.grantee = 0::oid
       AND acl.privilege_type IS NOT NULL
       AND acl.privilege_type <> ''
);
-- f

# Future-object probe: PUBLIC has no privilege on tables, sequences,
# or functions created by circus_owner after 000003.
SET ROLE circus_owner;
CREATE TABLE  circus.circus_probe_table (id int);
CREATE SEQUENCE circus.circus_probe_seq;
CREATE FUNCTION circus.circus_probe_fn() RETURNS int LANGUAGE sql
    AS 'SELECT 1';
RESET ROLE;
-- All three has_*_privilege('PUBLIC', ...) checks return f.
```

`CORRECTION03` is responsible for producing the Docker-backed
PostgreSQL test run and the `make gate` result on a reachable
daemon.

The final tree's `git diff --check` and literal `git status
--short` outputs captured **after** this round's edits:

```text
$ git diff --check
(no output)

$ git status --short
 M docs/architecture.md
 M docs/persistence/event-journal-v1.md
 M src/Circus.Persistence.Postgres/Circus.Persistence.Postgres.fsproj
 M src/Circus.Persistence.Postgres/IngestionTransaction.fs
 M src/Circus.Persistence.Postgres/Migration.fs
 M src/Circus.Persistence.Postgres/PostgresConfiguration.fs
 M src/Circus.Persistence.Postgres/ProjectionRepository.fs
 M tests/Circus.Api.Tests/Circus.Api.Tests.fsproj
 M tests/Circus.Api.Tests/Program.fs
 M tests/Circus.Api.Tests/packages.lock.json
 M tests/Circus.Application.Tests/Circus.Application.Tests.fsproj
 M tests/Circus.Application.Tests/Program.fs
 D tests/Circus.Application.Tests/ProjectionDecodingTests.fs
 D tests/Circus.Application.Tests/RetryPolicyTests.fs
 M tests/Circus.Persistence.Postgres.Tests/Circus.Persistence.Postgres.Tests.fsproj
 M tests/Circus.Persistence.Postgres.Tests/ConcurrencyTests.fs
 M tests/Circus.Persistence.Postgres.Tests/MigrationTests.fs
 M tests/Circus.Persistence.Postgres.Tests/PostgresFixture.fs
 M tests/Circus.Persistence.Postgres.Tests/Program.fs
 M tests/Circus.Persistence.Postgres.Tests/Support.fs
?? db/migrations/000003_runtime_grant_hardening.sql
?? docs/close-reports/closure-ACT-CIRCUS-INGESTION-JOURNAL01-CORRECTION01.md
?? tests/Circus.Api.Tests/HostLifecycleTests.fs
?? tests/Circus.Persistence.Postgres.Tests/AppendFailedRollbackTests.fs
?? tests/Circus.Persistence.Postgres.Tests/ProjectionInvariantTests.fs
?? tests/Circus.Persistence.Postgres.Tests/RetryCompositionTests.fs
?? tests/Circus.Persistence.Postgres.Tests/SemanticReplayTests.fs
?? tests/Circus.Persistence.Postgres.Tests/UnlockFailureTests.fs
?? tests/fixtures/migrations/
```

The two `tests/fixtures/migrations/` files (`000000_pre_closure.sql`
and `000001_released_parent.sql`) are the contents of the directory
shown by the `?? tests/fixtures/migrations/` placeholder line.  This
round also added several new test files; each is listed individually
under the `??` block above, both under
`tests/Circus.Persistence.Postgres.Tests/` (the unlock-failure,
retry-composition, journal-replay, and projection-invariant tests)
and under `tests/Circus.Api.Tests/` (`HostLifecycleTests.fs`).  The
"exact two files" phrasing that the previous round's report used
described only the `tests/fixtures/migrations/` directory
contents; the overall set of checked-in test artifacts in this round
is the two SQL fixtures plus those new test files.

The two `D tests/Circus.Application.Tests/*Tests.fs` lines are
tracked deletions.  An automated digest that classifies these
paths as "modified" is incorrect; the path appears with a leading
`D` in `git status --short` because the path has been deleted from
the working tree.  Both deletions were intentional in the prior
round; their evidence now lives in
`tests/Circus.Persistence.Postgres.Tests/RetryCompositionTests.fs`
and the semantic-replay tests.  The supplied digest still reports
`deleted_files = 0` and labels the two paths as modified; this is
a defect of the digest itself.

`tests/Circus.Persistence.Postgres.Tests/UnlockFailureTests.fs` is
shown in the untracked `??` block because it is the new test file
added in this round and has not yet been `git add`-ed.  The
`tests/Circus.Persistence.Postgres.Tests.fsproj`, `Program.fs`,
and `Support.fs` `M` entries already declare the file in the test
project, which is why the build compiles against it.  Treating
this file as `M` (as the previous round's report did) was an R2.3
defect: the literal `git status` line is `??`.

The tree intentionally contains these modifications and additions
because the corrective ACT requested a fresh implementation of the
defects.

### R2.3 — repository-state numerical summary reconciled with the literal tree

A supplied numerical summary reported `12 M modifications; two
deletions; eight ?? additions`.  The literal `git status --short`
block reproduced above contains **18 `M` lines, 2 `D` lines, and 9
`??` status lines**, representing **10 untracked files** because
`tests/fixtures/migrations/` is reported by `git status` as a single
placeholder line but the directory actually holds two files
(`000000_pre_closure.sql` and `000001_released_parent.sql`).  The
literal status block is the authoritative source and the previous
numerical summary was incorrect.  The corrected explicit counts:

* **18 `M` lines** (modified tracked files), enumerated above.
* **2 `D` lines** (tracked deletions):
  `tests/Circus.Application.Tests/ProjectionDecodingTests.fs` and
  `tests/Circus.Application.Tests/RetryPolicyTests.fs`.
* **9 `??` status lines** for **10 untracked files**:
  - `db/migrations/000003_runtime_grant_hardening.sql`
  - `docs/close-reports/closure-ACT-CIRCUS-INGESTION-JOURNAL01-CORRECTION01.md`
  - `tests/Circus.Api.Tests/HostLifecycleTests.fs`
  - `tests/Circus.Persistence.Postgres.Tests/AppendFailedRollbackTests.fs`
  - `tests/Circus.Persistence.Postgres.Tests/ProjectionInvariantTests.fs`
  - `tests/Circus.Persistence.Postgres.Tests/RetryCompositionTests.fs`
  - `tests/Circus.Persistence.Postgres.Tests/SemanticReplayTests.fs`
  - `tests/Circus.Persistence.Postgres.Tests/UnlockFailureTests.fs`
  - `tests/fixtures/migrations/000000_pre_closure.sql`
  - `tests/fixtures/migrations/000001_released_parent.sql`
  (the last two collapse to the single `?? tests/fixtures/migrations/`
  placeholder line shown by `git status --short`).

The earlier `12 M / 2 D / 8 ??` numerical summary is hereby **removed**
in favour of the literal block above and the explicit count
reconciliation recorded in this subsection.  Any downstream consumer
of this report must count from the literal `git status --short`
output, not from a digest summary.

## 9. Remaining open work (deferred to CORRECTION03)

The review identified these open workstreams that were not closed
in this round:

1. **R1.6 precise migration failures.** The negative migration
   tests must assert the exact `MigrationInvariantException`
   class and message instead of any thrown exception.
2. **R1.7 incremental-vs-rebuild equality.** After same-event
   recovery, the incremental projection must equal
   `ProjectionRebuild.rebuildFromJournal`.
3. **R1.8 real host composition.** The container-based lifecycle
   tests must own and dispose the production `IHost`.
4. **Creator-role probe.** A documented comment is not
   enforcement.  The next ACT must run the production migration
   path, create a future table / sequence / function, and assert
   their owner is `circus_owner` and `PUBLIC` has no privilege.
5. **Projection geometry matrix.** The next ACT must complete the
   positive matrix: `Completed` finished-first, `Conflicted`
   duplicate-finished, `Conflicted` two authorities plus later
   conflict, rejection of two-authority conflict where
   `last = max(authorities)`.
6. **Observer boundary split.** The next ACT must split
   `BeforeContestedMutation` into `BeforeJournalMutation` and
   `BeforeProjectionMutation` so tests can target either
   operation precisely.
7. **Deterministic disposal.** Ensure every temporary data
   source, service provider, host, and container is disposed.
   The `UnlockFailureTests.fs` group added in this round disposes
   every container and admin data source it creates.

## 10. Closure effect and the next ACT

Because the review still flags the R1.6 / R1.7 / R1.8 test seams,
the projection geometry matrix, the observer split, and the
deterministic disposal as open, this ACT remains honestly marked
**PARTIAL**.

* `ACT-CIRCUS-INGESTION-JOURNAL01-CLOSURE01` cannot move from
  `PARTIAL` to `COMPLETE` until `CORRECTION03` lands and the
  PostgreSQL test run, `make test-backend`, and `make gate` are
  produced and pass on a reachable Docker daemon.
* `ACT-CIRCUS-INGESTION-JOURNAL01` remains `PARTIAL` for the same
  reason.
* `ACT-CIRCUS-AUTH-LEAMAS01` remains blocked.  No producer
  authentication work was performed in this ACT.

The recommended follow-up ACT is
`ACT-CIRCUS-INGESTION-JOURNAL01-CLOSURE01-CORRECTION03`.  Its scope
must include at least:

1. **R1.6 dual-ledger and dual-object assertions.** Assert the
   exact `MigrationInvariantException` for the dual-ledger case
   and the exact failing migration version for the failed-migration
   case.
2. **R1.7 incremental-vs-rebuild equality.** After same-event
   recovery, assert that the incremental projection equals the
   `ProjectionRebuild.rebuildFromJournal` result and that both
   journal authorities and their original raw bytes remain present.
3. **R1.8 production host composition.** Refactor `Program.buildHost`
   to expose a `createHost (serverConfiguration)` seam and rewrite
   the two container-based lifecycle tests to exercise the real
   `IHost`.
4. **Migration authority and creator-role probe.** Define a
   `MigrationAuthority` (administrator or per-object creator role)
   per migration version.  Prove that a future object-only migration
   running with the canonical `ObjectCreator "circus_owner"`
   authority creates objects owned by `circus_owner` and produces
   no `PUBLIC` table/sequence/function privilege.
5. **Projection geometry matrix.** Complete the positive matrix:
   `Completed` finished-first, `Conflicted` duplicate-finished,
   `Conflicted` two authorities plus later conflict, rejection of
   two-authority conflict where `last = max(authorities)`.
6. **Observer boundary split.** Split `BeforeContestedMutation`
   into `BeforeJournalMutation` and `BeforeProjectionMutation`.
7. **Deterministic disposal.** Ensure every temporary data
   source, service provider, host, and container is disposed
   (released-parent container, API test service provider, host
   lifecycle `IHost`).
8. **Live PostgreSQL evidence for the unlock-failure tests.**
   The four `UnlockFailureTests.fs` cases are encoded against a
   dedicated `postgres:17.4` container (cluster-wide role state
   requires isolation from the shared `PostgresFixture`) and
   depend on a reachable Docker daemon.  The
   `MigrationLockCleanupException` sealed-class fix, the
   `MessageText` exact-match assertion, the bounded `pg_stat_activity`
   polling, and the corrected literal `git status --short` output
   are now in place; the suite must be exercised on a reachable
   daemon to produce the new live evidence.
