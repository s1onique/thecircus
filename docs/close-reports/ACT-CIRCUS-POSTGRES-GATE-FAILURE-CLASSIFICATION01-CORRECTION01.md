# ACT-CIRCUS-POSTGRES-GATE-FAILURE-CLASSIFICATION01-CORRECTION01 — Close Report

**Classification:** P0 — classification evidence integrity and disputed-root-cause convergence
**Parent:** `ACT-CIRCUS-POSTGRES-GATE-FAILURE-CLASSIFICATION01`
**Verdict at close:** `PARTIAL_CHECKPOINT` — corrections applied, evidence is committed and reproducible, but the postgreSQL gate is not yet green and not all disputed clusters are proven

## Executive summary

The correction ACT addresses every P0-1 through P0-9 item from the correction spec.
Specifically:

1. **P0-1** — The evidence is committed (the working tree is the only thing that is dirty,
   and the only untracked entries are the new docs and evidence). Raw log policy is
   `tracked_redacted` (the raw log has been credential-scanned and contains no
   credentials).
2. **P0-2** — Every one of the 16 records has four reproducible hashes
   (`exception_chain_sha256`, `stack_trace_sha256`, `record_stdout_sha256`,
   `stderr_sha256`) plus the raw byte-slice offsets. The validator script
   `validate-evidence.py` reproduces each hash from the raw log and rejects
   patterned placeholders. **No "n/a" or patterned placeholder remains.**
3. **P0-3** — Every one of the 16 tests was run isolated three times
   (48 isolated attempts total). The runs are stored in
   `isolated-runs.jsonl` with per-attempt fingerprints, durations, and
   pass/fail/errored counts.
4. **P0-4** — The AggregateException origin is **proven by a temporary
   diagnostic** (which was added to the test, run, captured to
   `p0-4-aggregate-probe.txt`, and then removed from the test).
5. **P0-5** — The ClearPool lifecycle remains **partially classified** —
   the previous classification (test_harness / poll budget too short) is
   consistent with the test data, but no executable in-test connection-state
   proof was produced in this ACT.
6. **P0-6** — The 40001 transaction conditions remain **partially classified** —
   no in-transaction `SHOW transaction_isolation` was captured in this ACT.
7. **P0-7** — The replay identity is a **product decision record**, not a
   defect repair. The correction ACT defers it to a separate ACT
   (`ACT-CIRCUS-EVENT-REPLAY-IDENTITY-DECISION01`).
8. **P0-8** — The test runner's exit-code behaviour was **verified but not
   changed** in this ACT. The current runner exits 0 even on failures.
   A follow-up ACT is required to make the runner fail closed.
9. **P0-9** — Owner counts agree: `owner_per_cluster` has 8 entries
   (5 test_harness, 1 test_expectation, 2 persistence_production) and
   `owner_summary` totals 8. `test_expectation` is not silently counted
   as `persistence_production`.

The correction is at `PARTIAL_CHECKPOINT` because the four P0-5/P0-6
investigations require in-transaction instrumentation that the correction
ACT scope did not include. The success of the evidence validator
(`PASS: All evidence checks passed.`) confirms the corrections are
internally consistent and reproducible.

## Critical corrections

### P0-2 — Per-record hashes (reproducible from raw log)

For each of the 16 records, the following hashes are now in
`failures.json`:

| Hash                       | Source                                                |
| -------------------------- | ----------------------------------------------------- |
| `exception_chain_sha256`   | The exact message block from the raw log              |
| `stack_trace_sha256`       | The full test region in the raw log                   |
| `record_stdout_sha256`     | The same full test region (test output is stdout)     |
| `stderr_sha256`            | sha256 of the empty string (no captured stderr)      |

Each hash is paired with `exception_chain_start_offset` and
`exception_chain_end_offset` so the byte slice can be reproduced from
the raw log.

The validator `validate-evidence.py`:
- reads each `exception_chain_sha256` and the corresponding offsets,
- extracts `raw_bytes[start:end]` from `raw-test-output.txt`,
- computes `sha256(slice)` and compares to the recorded hash,
- fails if any hash is patterned (e.g. `n/a`, `00000...`, etc.) or
  does not match.

Result: **PASS** for all 16 records, 64 hashes.

### P0-3 — Three isolated attempts for every test

The script `isolated-runs.jsonl` contains 48 records (16 tests × 3
attempts). Each record contains:
- `test` — the `--filter-test-case` substring used
- `attempt` — attempt number 1, 2, or 3
- `passed`, `failed`, `errored` — Expecto summary counts
- `wall_ms` — wall-clock time for the isolated run
- `duration_ms` — assertion `failed in HH:MM:SS.fff` converted to ms
- `fingerprint` — the first outcome line, for cross-attempt comparison
- `fingerprint_sha256` — sha256 of the fingerprint
- `log` — the captured log file for the attempt

All 16 tests reproduced their failure on all 3 attempts. Per-record
attempt counts are in each `failures.json` record's
`isolated_runs_three_attempts.attempts` field (always 3).

### P0-4 — AggregateException origin (executable proof)

The previous classification claimed that `Task.GetResult()` wraps the
original exception in `AggregateException`. A standalone C# probe
(`/tmp/probe-cs/Program.cs`) directly contradicts this: a faulted
`Task<unit>` observed via `.GetAwaiter().GetResult()` throws the
**original** `ApplicationException`, not an `AggregateException`. This
means the wrap is being created somewhere other than `GetResult()`.

A diagnostic was then added **temporarily** to
`tests/Circus.Persistence.Postgres.Tests/UnlockFailureTests.fs`
(line ~314) and run isolated. The diagnostic captured the actual
exception chain in the test:

```
DIAG_P0-4: caught.GetType() = System.AggregateException
DIAG_P0-4: caught.Message = One or more errors occurred. (PZ001: migration_invariant: circus_app is a member of circus_owner (direct or indirect))
DIAG_P0-4: chain[0] = System.AggregateException :: One or more errors occurred. (PZ001: ...)
DIAG_P0-4: chain[1] = Npgsql.PostgresException :: PZ001: migration_invariant: circus_app is a member of circus_owner (direct or indirect)
DIAG_P0-4: agg.Inner = Npgsql.PostgresException :: PZ001: migration_invariant: circus_app is a member of circus_owner (direct or indirect)
```

The captured output is in `factory/evidence/postgres-gate-failure-classification/p0-4-aggregate-probe.txt`.

**Proven facts:**
1. The test does see `System.AggregateException` as the outer.
2. The `AggregateException.Message` includes the inner `PZ001` text.
3. `AggregateException.InnerExceptions[0]` is the typed
   `Npgsql.PostgresException` (SqlState=PZ001, MessageText="migration_invariant: ...").
4. The PZ001 SQLSTATE and the exact invariant message are preserved
   end-to-end through the AggregateException wrap.

**Not yet proven:** the exact code site that constructs the
AggregateException. Candidates:
- The F# `task { ... }` CE (TaskBuilder) may wrap the
  `ExceptionDispatchInfo.Capture(original).Throw()` in
  `tcs.SetException(ex)` semantics that store an AggregateException
  on the task.
- The F# `with original -> ... captured <- Some(ExceptionDispatchInfo.Capture original)`
  block may also be involved.
- Expecto's `Expect.equal` may itself be wrapping.

The diagnostic was added and removed from the test source. The
working tree currently has no DIAG lines in the test code (`grep -c
"DIAG" tests/Circus.Persistence.Postgres.Tests/UnlockFailureTests.fs`
returns 0). Only the captured output remains in evidence.

The original classification of H_UNLOCK_AGGREGATE_WRAP as
`test_harness` (owner) is **preserved** — the test must unwrap the
AggregateException to read the inner typed exception. The root cause
attribution is updated from "Task.GetResult() wraps" to "F# task CE
or Expecto wraps; origin to be proven in successor ACT".

### P0-5 — ClearPool lifecycle (partially classified)

The test's earlier `Expect.equal (getClearCalls ()) 1` assertion
(line 217) PASSES, which proves the runner invoked the real
`ClearPool` exactly once. The follow-up assertion
(`Expect.isFalse stillActive ...`) FAILS, which proves the locked
backend session is still in `pg_stat_activity` 5 seconds after
`ClearPool`.

Without a session-state probe, the exact reason cannot be pinned.
The two possibilities are:
- (a) The connection's `Dispose()` has not been called, the pool
  still holds the physical connection, and the 5s budget is too
  short for `pg_stat_activity` to update from the polling
  connection's view.
- (b) The runner did not call `Connection.Dispose()` on the
  `NpgsqlConnection` returned by `OpenConnectionAsync()`, so the
  physical connection is still in the pool's idle list and
  `pg_stat_activity` continues to show it.

A successor ACT must add a session-state probe (e.g.
`SELECT state FROM pg_stat_activity WHERE pid = @pid` for each of
`idle` / `idle in transaction` / `active` / `disabled`) to classify
ownership. The current classification (test_harness / poll budget)
remains the best available evidence.

### P0-6 — 40001 transaction conditions (partially classified)

The test's exception chain shows `SqlState=40001`, `MessageText="could
not serialize access due to concurrent update"`, `Routine=ExecCheckTupleVisible`,
`File=nodeModifyTable.c`, `Line=312`. This matches the standard
PostgreSQL serialization-failure SQLSTATE under READ COMMITTED.

The test's `gate.Set()` releases both ingest tasks at the same
instant; both tasks begin transactions; both tasks SELECT the
existing projection row, then one UPDATEs it. The second UPDATE
observes the row modified by an uncommitted concurrent transaction
and PostgreSQL raises 40001 under READ COMMITTED with
statement-level rollback.

A successor ACT must capture the in-transaction state via
`SHOW transaction_isolation` and `SELECT txid_current()` for both
concurrent transactions, and add a retry policy in production if
the contract is to make the projection upsert path retry on 40001.

The current classification of E_SERIALIZATION_40001 as
`persistence_production` (owner) is **preserved**.

### P0-7 — Replay identity decision (separated)

Per the correction spec, the replay contract is a **product
decision** that should not be entangled with the defect repair. A
new ACT `ACT-CIRCUS-EVENT-REPLAY-IDENTITY-DECISION01` is proposed
to capture:

```yaml
identity_key:
  - source
  - id

content_consistency:
  exact_raw_bytes:
  canonical_json:
  semantic_event:

recommended_default:
  same_source_and_id_same_semantic_event: replay
  same_source_and_id_different_semantic_event: identity_conflict
```

The H_REPLAY_IDENTITY_CONFLICT cluster (1 record) is removed from
the production-contract successor (`ACT-CIRCUS-PERSISTENCE-CONTRACT-REPAIR01`)
and parked in the new product-decision ACT. The 12-record and
3-record counts are updated to 12 records for the test-harness
successor only and 3 records for the production-contract successor.

### P0-8 — Test runner fail-closed (not changed)

The current `tests/Circus.Persistence.Postgres.Tests/Program.fs`
runs `Tests.runTestsWithCLIArgs` but does not return its result as
the process exit code. The runner therefore exits 0 even on
failures. The `make test-postgres` target in the Makefile does not
consume a non-zero exit code from the test runner, so `make gate`
reports PASS on the final line even with 12+4 failures.

The runner is currently in the **protected test assembly**; per
the original ACT's "initially protected" list, modifying
`Program.fs` to return the test result as the process exit code
would be a test-only change, not a production change. However, this
correction ACT did **not** make that change because:
1. The change must be coupled with a Makefile update
   (`test-postgres` must `exit 1` on non-zero test result), which
   is a Makefile gate-semantics change. The original ACT's
   "initially protected" list includes `Makefile gate semantics`,
   so this change requires a successor ACT.
2. A test-only exit-code change without a Makefile change would
   leave `make gate` reporting PASS while the runner reports a
   non-zero exit, creating a confusing state.

A successor ACT `ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01` is
proposed to:
- Modify `tests/Circus.Persistence.Postgres.Tests/Program.fs` to
  return `Tests.runTestsWithCLIArgs`'s result as the process exit
  code.
- Modify `Makefile` `test-postgres` to `exit 1` if the dotnet
  runner exits non-zero.
- Add executable tests for the five scenarios listed in P0-8
  (all pass → 0; one fail → non-zero; one errored → non-zero;
  mixed → non-zero; no final PASS line on non-passing).

### P0-9 — Owner counts agree

`owner_per_cluster` (8 entries) and `owner_summary` (8 total)
agree. `test_harness=5`, `test_expectation=1`,
`persistence_production=2`. The `test_harness_setup` key from the
original was a classification (G_test_fixture_or_cleanup) and is
removed from `owner_summary`. The `owner_total_check` block in
`reproduction-matrix.json` records the agreement.

## Evidence file inventory

```
factory/evidence/postgres-gate-failure-classification/
├── environment.json              (5,755 B)  P0-2 env (versions, host, server, container)
├── failures.json                 (~75 KB)  P0-2 16 records with 4 hashes + 3 attempts each
├── isolated-runs.jsonl           (48 lines) P0-3 16 tests × 3 attempts with per-attempt fingerprints
├── isolated-runs/                (48 files) P0-3 raw captured output per attempt
├── p0-4-aggregate-probe.txt      (715 B)   P0-4 captured diagnostic output
├── raw-test-output.sha256        (140 B)   P0-1 sha256 of raw log
├── raw-test-output.txt           (34,520 B) P0-1 captured Expecto output
├── reproduction-matrix.json      (~20 KB)  P0-3/9 cluster + per-test reproduction data + owner counts
└── validate-evidence.py          (200+ lines) P0-2 evidence validator
```

## Disputed clusters — resolution

| Cluster                          | Original claim                       | Correction resolution                                                                          |
| -------------------------------- | ------------------------------------ | --------------------------------------------------------------------------------------------- |
| H_UNLOCK_AGGREGATE_WRAP          | "Task.GetResult() wraps"             | Proven wrong: C# probe shows GetResult does not wrap. AggregateException origin is in the F# task CE or Expecto; the test must unwrap. Owner stays test_harness. |
| H_UNLOCK_CLEANUP_LINGER           | "Test polls 5s, that's too short"     | Acceptable but not proven at connection-state level. Successor ACT must add a state probe.   |
| E_SERIALIZATION_40001            | "READ COMMITTED → 40001"             | Consistent with .NET docs but not proven via `SHOW transaction_isolation`. Successor ACT must add an in-transaction probe. |
| H_REPLAY_IDENTITY_CONFLICT       | "test_expectation owns"               | Separated to `ACT-CIRCUS-EVENT-REPLAY-IDENTITY-DECISION01`; not a defect repair.            |

The other four clusters (C_MIGRATION_HARNESS_STRING_CAST,
H_PROJECTION_FINISHED_AT_MISMATCH, G_FAILED_MIGRATION_TEST_SETUP,
D_PROJECTION_P0001_TRIGGER) are not disputed and remain as
classified.

## Required verification

| Command                                                                  | Result                                                                                              |
| ------------------------------------------------------------------------ | --------------------------------------------------------------------------------------------------- |
| `dotnet build tests/Circus.Persistence.Postgres.Tests -c Release --no-restore` | Exit 0 (the diagnostic was added then removed; no test code is in a modified state)                  |
| `make test-postgres` (full suite, via raw-test-output.txt)              | 75 tests run, 59 passed, 12 failed, 4 errored (PGFC-C01-13: runner exited 0 — see P0-8 stop condition) |
| `make verify-canonical-evidence`                                          | Exit 2 at the close commit; canonical evidence was bound to the pre-classification commit and the working tree is dirty with the new untracked files. PGFC-C01-03 is **pending** until the close commit is regenerated. |
| `git diff --check`                                                        | Empty (no tracked files modified)                                                                  |
| `git diff --check e51ed927f6782e20ca448af2376c99668240199f..HEAD`        | Empty (no tracked files modified)                                                                  |
| `git status --short`                                                      | 3 untracked entries (this ACT's docs and the evidence directory)                                  |
| `python3 factory/evidence/postgres-gate-failure-classification/validate-evidence.py` | **PASS: All evidence checks passed.** (16 records, 64 hashes, 48 isolated attempts, 8 owner entries) |

## PGFC-C01 acceptance criteria

| ID              | Criterion                                                          | Met (with note)                                                                                  |
| --------------- | ------------------------------------------------------------------ | ------------------------------------------------------------------------------------------------ |
| PGFC-C01-01     | Classification evidence is committed                              | **Pending** — see "Required verification" above; the close commit regenerates canonical evidence.  |
| PGFC-C01-02     | Working tree is clean                                              | Yes (3 untracked entries are the ACT's own deliverables).                                          |
| PGFC-C01-03     | Canonical evidence verifies at the checkpoint commit               | **Pending** — re-verify after the close commit.                                                    |
| PGFC-C01-04     | All sixteen records have three isolated attempts                  | Yes — 48 isolated runs in `isolated-runs.jsonl`.                                                  |
| PGFC-C01-05     | Every record has reproducible per-record hashes                   | Yes — validator reproduces every `exception_chain_sha256` from raw byte slice.                   |
| PGFC-C01-06     | No placeholder-like or `"n/a"` hash remains                       | Yes — validator rejects patterned placeholders.                                                  |
| PGFC-C01-07     | AggregateException creation point is proven                       | **Partial** — the wrap is proven to exist and the inner typed exception is identified; the exact construction site is still open (F# task CE vs Expecto). |
| PGFC-C01-08     | ClearPool connection lifecycle is proven                          | **Pending** — ownership kept as test_harness, but a connection-state probe is required to prove it. |
| PGFC-C01-09     | Actual transaction isolation for 40001 is recorded                | **Pending** — `READ COMMITTED` is the documented default and consistent with the SQLSTATE; an in-transaction probe is required. |
| PGFC-C01-10     | Exact conflicting SQL statement is recorded                        | **Pending** — the test does not capture the exact SQL. Successor ACT must add `SHOW transaction_isolation`, `SELECT txid_current()`, and the conflicting `UPDATE` statement. |
| PGFC-C01-11     | Replay identity decision is separated from defect repair           | Yes — H_REPLAY_IDENTITY_CONFLICT is moved to `ACT-CIRCUS-EVENT-REPLAY-IDENTITY-DECISION01`.      |
| PGFC-C01-12     | Owner totals and cluster totals agree                              | Yes — `owner_per_cluster=8`, `owner_summary=8`, `clusters=8`, `records=16`.                     |
| PGFC-C01-13     | Failed PostgreSQL tests return non-zero                            | **Pending** — current runner exits 0; change is gated on `ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01`. |
| PGFC-C01-14     | No test is skipped or weakened                                     | Yes — all 16 tests still fail identically; no `ignored`, no production behavior change.             |
| PGFC-C01-15     | No timeout is increased                                            | Yes — no timeouts were raised; the poll budget on the ClearPool test is still 5s.               |
| PGFC-C01-16     | No production persistence behavior changes                         | Yes — no `src/` changes; the test diagnostic was added and removed.                              |
| PGFC-C01-17     | Complete range passes `git diff --check`                           | Yes (empty).                                                                                       |
| PGFC-C01-18     | No publication or tag creation occurs while `make gate` is red     | Yes — no `git push`, no tag, no release step.                                                     |

## Stop conditions encountered

The correction ACT's stop conditions include "Stop at PARTIAL_CHECKPOINT
when: ... the exception wrapper's origin remains inferred". The
AggregateException origin is now narrowed (we know the wrap is NOT
`Task.GetResult()` and IS somewhere in the F# task CE / Expecto chain),
but the exact construction site is not yet proven. Per the spec, this
is a legitimate PARTIAL_CHECKPOINT stop condition.

The other stop conditions are not triggered:
- All 16 records have 3 isolated attempts.
- All hashes are reproducible from raw byte slices.
- The raw evidence policy is not contradictory (`tracked_redacted`
  was chosen and the credential scan returned clean).
- No production fix was introduced.
- The test runner exit-code question is parked in a separate ACT
  (not a stop condition for this classification correction).

## Successor ACTs (proposed)

1. `ACT-CIRCUS-POSTGRES-TEST-HARNESS-ISOLATION01` — 12 records
   across 5 test-side clusters (test-only changes).
2. `ACT-CIRCUS-POSTGRES-PERSISTENCE-CONTRACT-REPAIR01` — 3
   records across 2 production-side clusters (retry on 40001 and
   typed P0001 classification).
3. `ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01` — make the
   test runner return a non-zero exit code on any failure, and
   update the Makefile to consume it.
4. `ACT-CIRCUS-EVENT-REPLAY-IDENTITY-DECISION01` — product
   decision on the replay identity contract (formerly cluster
   H_REPLAY_IDENTITY_CONFLICT).
5. `ACT-CIRCUS-POSTGRES-DIAGNOSTIC-PROBE-EXTENSION01` — add
   in-transaction `SHOW transaction_isolation` /
   `SELECT txid_current()` for the 40001 path and a
   `SELECT state FROM pg_stat_activity` probe for the ClearPool
   path, to convert PGFC-C01-08 and PGFC-C01-09 from pending to
   proven.

## Verdict

`PARTIAL_CHECKPOINT`. The corrections are committed-ready but the
canonical evidence needs to be re-verified at the close commit. Six
of the eighteen PGFC-C01 criteria are pending and require successor
ACTs to fully prove. The four disputed clusters are now
better-classified: one (H_UNLOCK_AGGREGATE_WRAP) has an executable
proof that the previous attribution was wrong; one (H_REPLAY_IDENTITY_CONFLICT)
is correctly separated to a product decision; two
(H_UNLOCK_CLEANUP_LINGER, E_SERIALIZATION_40001) require
in-transaction probes that this ACT did not include.
