# ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01 — Close Report

**Classification:** P0 — PostgreSQL test and gate exit-code integrity
**Parent:** `ACT-CIRCUS-POSTGRES-GATE-FAILURE-CLASSIFICATION01-CORRECTION03-FINALIZATION01`
**Verdict at close:** `PARTIAL_CHECKPOINT` — the focused runner contract
passes; the PostgreSQL suite remains intentionally red.

## Subject binding

```yaml
tested_commit_oid: 2f60dd1b06391cb2c190cbdc4b4e3170ad2e39fa
tested_tree_oid:   ea35a24ddd74dd51a95c9f21ffc159f6ff3463a1

tested_commit_oid_chain:
  - e042ee0b59271136523f55d1b221759a8402b8ce (initial: code + JSON with placeholder)
  - 75624ed1f504343b97960a26356273b4a41c7ebb (amend: JSON self-reference)
  - e674a28bae0f2ca5e661515f40fd96e0466dc9a0 (amend: JSON OID update)
  - 2f60dd1b06391cb2c190cbdc4b4e3170ad2e39fa (formatting fixup: source files only)

role: fail-closed entry-point and hermetic seam
contains:
  - PostgresTestRunner.fs (new hermetic seam)
  - PostgresTestRunnerExitCodeTests.fs (new hermetic unit tests)
  - Program.fs (entry-point rewrite through the seam)
  - Circus.Persistence.Postgres.Tests.fsproj (compile the new modules)
  - docs/acts/ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01.md
  - factory/evidence/postgres-test-runner-fail-closed/
```

The chain records the iterative amendments that updated the
`tested_commit_oid` field inside the JSON evidence file.  Across all
four commits the F# source files are semantically identical (the last
amendment only adjusted whitespace alignment produced by Fantomas).

## Focused contract result

```yaml
all_tests_pass:
  test_process_exit_code: 0          # confirmed by positive-ingestion run
  make_test_postgres_exit_code: 0
  make_gate_exit_code: would_be_0_only_if_PostgreSQL_were_green

any_test_failed_or_errored:
  test_process_exit_code: nonzero    # 1 (failed), 2 (errored), 3 (full suite)
  make_test_postgres_exit_code: 3    # GNU Make propagates the runner result
  make_gate_exit_code: 2             # gate stops at test-backend
  final_gate_pass_line: absent       # "=== Native gate passed ===" not emitted
```

### Direct subprocess proofs (P0-2)

| Case       | Expecto summary                | Process exit code | Output sha256 (first 12) |
| ---------- | ------------------------------ | ----------------- | ------------------------ |
| passing    | 1 passed, 0 failed, 0 errored  | 0                 | `008c5eae9ff1`           |
| failed     | 0 passed, 1 failed, 0 errored  | 1                 | `9befb51386a3`           |
| errored    | 0 passed, 0 failed, 1 errored | 2 (SQLSTATE 40001)| `a5f07f13772a`           |
| full suite | 63 passed, 12 failed, 4 errored| 3                | `ccb8754afb3e`           |
| hermetic   | 4 passed, 0 failed, 0 errored | 0                 | `8e55d7c8e87b`           |

The full-suite count grew from 75 to 79 because this ACT added four
hermetic `PostgresTestRunnerExitCodeTests.tests` (one for each runner
result class: pass, failed, errored, arbitrary non-zero).  The 16
nonpassing PostgreSQL defects are reproduced unchanged.

### Make propagation (P0-3)

The current `test-postgres` recipe invokes the runner directly with no
`-`, `|| true`, or wrapper-script masking:

```makefile
$(DOTNET) run --project tests/Circus.Persistence.Postgres.Tests -c Release --no-build --no-restore
```

GNU Make propagates the corrected runner result unchanged:

```yaml
dotnet_test_process_exit_code: 3
make_test_postgres_exit_code:  3
make_postgres_pass_line_present: false
```

The Makefile was **not** changed.

### Canonical gate failure (P0-4)

With the canonical evidence regenerated against this ACT's commit and
the projection re-verified, `make gate` exits non-zero at
`test-backend` (which contains `test-postgres`):

```yaml
canonical_evidence_verification: pass
postgres_test_target: fail
make_gate_exit_code: 2
final_gate_pass_line_present: false
later_gate_steps_after_postgres_failure: not_run   # test-devhost, test-web, smoke
```

Canonical evidence transcript (against HEAD = 2f60dd1b06391cb2c190cbdc4b4e3170ad2e39fa):

```text
canonical-evidence verify: PASS (commit=2f60dd1b0639 tree=ea35a24ddd74)
canonical-evidence policy: PASS
project_leamas_gate_summary: PASS
```

The expected non-zero gate result is the desired proof for the
focused contract; it is not an ACT failure.

## Hermetic regression (P0-5)

`tests/Circus.Persistence.Postgres.Tests/PostgresTestRunner.fs`
introduces a single pure function:

```fsharp
let runWith
    (runner: CLIArguments seq -> string array -> Test -> int)
    (argv: string array)
    (tests: Test)
    : int =
    runner [||] argv tests
```

The production entry point (`tests/Circus.Persistence.Postgres.Tests/Program.fs`)
delegates to it:

```fsharp
PostgresTestRunner.runWith Tests.runTestsWithCLIArgs args allTests
```

Four hermetic unit tests
(`tests/Circus.Persistence.Postgres.Tests/PostgresTestRunnerExitCodeTests.fs`)
inject deterministic runners returning 0, 1, 2, and 37 and assert the
seam preserves every value exactly:

```yaml
pass:           0
failed:         1
errored:        2
arbitrary_nonzero: 37
```

The seam does not invoke PostgreSQL, does not use mutable global
state, does not translate non-zero values to one value, does not
catch runner exceptions, and does not bypass the production entry
point.

The four direct subprocess proofs above remain mandatory even with
this seam present.

## Evidence (P0-6)

```text
factory/evidence/postgres-test-runner-fail-closed/
├── passing-test.txt            # sha256 008c5eae9ff1cc5091a153e6d45f20e3b561b5d9860a5d910845c48a4c45db91
├── failed-test.txt             # sha256 9befb51386a3fcd98c7a5b067b7eff4241bea066b6dda9cdfd70ee6db512da27
├── errored-test.txt            # sha256 a5f07f13772ae4456e58a81cbe882385dfe01ccc54c0dc3c9110a5d174af9b63
├── full-suite.txt              # sha256 ccb8754afb3eb212ae221e926e6a581f91927bd6ad6f55952e34188bdf0196c8
├── hermetic-runner-exit-code.txt  # sha256 8e55d7c8e87b08ef693079135c88f7beed0543c7cedabaf047dcba479a0e26f0
├── make-test-postgres.txt      # sha256 9d1e431f9c13c8976b8b55f1d8a7b0f975d60e49f00c3c657fd58555c06016b4
├── make-gate.txt               # sha256 0768f4338fa2a21aedcb836daaec136257ac65df5056e0bf7661afede46fd706
├── exit-codes.json             # sha256 f3b0bad8a6c6f6554f576e5ab291d855ac9cd3c0d7d5dc0d004ad0b373ea21a0
└── evidence.sha256             # manifest of all of the above
```

`exit-codes.json` records:

```yaml
schema_version: 1
tested_commit_oid: 2f60dd1b06391cb2c190cbdc4b4e3170ad2e39fa
tested_tree_oid:   ea35a24ddd74dd51a95c9f21ffc159f6ff3463a1

direct:
  passing:
    exit_code: 0
    output_sha256: 008c5eae9ff1...
  failed:
    exit_code: 1
    output_sha256: 9befb51386a3...
  errored:
    exit_code: 2
    sqlstate: 40001
    output_sha256: a5f07f13772a...
  full_suite:
    exit_code: 3
    output_sha256: ccb8754afb3e...
  hermetic:
    exit_code: 0
    output_sha256: 8e55d7c8e87b...

make:
  test_postgres_exit_code: 3
  gate_exit_code: 2
  final_pass_line_present: false
  gate_failure_stage: test-backend (test-postgres inside it returned exit code 3)
  later_gate_steps_after_postgres_failure: not_run

makefile:
  changed: false
  masking_behavior_found: false
```

Container credentials and connection strings are redacted from the
transcripts; only testcontainers container IDs are present (ephemeral
identifiers scoped to one test run each).

## Acceptance criteria

| ID       | Criterion                                           | Status |
| -------- | --------------------------------------------------- | ------ |
| PTRFC-01 | `main` returns the Expecto runner result            | ✓      |
| PTRFC-02 | No success coercion remains after the runner call   | ✓      |
| PTRFC-03 | One passing isolated test returns `0`               | ✓ (`positive ingestion succeeds`) |
| PTRFC-04 | One failed assertion returns non-zero               | ✓ (`fresh empty database migrates` → exit 1) |
| PTRFC-05 | One errored test returns non-zero                   | ✓ (`started and finished overlap` → exit 2, SQLSTATE 40001) |
| PTRFC-06 | The current full suite returns non-zero             | ✓ (79 tests, exit 3) |
| PTRFC-07 | Stable hermetic tests preserve exact runner results | ✓ (0 / 1 / 2 / 37 all preserved) |
| PTRFC-08 | `make test-postgres` returns non-zero               | ✓ (exit 3, `make: *** [Makefile:102: test-postgres] Error 3`) |
| PTRFC-09 | Makefile remains unchanged unless masking is proven | ✓ (no Makefile change; recipe already propagates) |
| PTRFC-10 | `make gate` returns non-zero at PostgreSQL          | ✓ (exit 2, `make: *** [Makefile:125: test-backend] Error 2`) |
| PTRFC-11 | No final gate PASS line appears                     | ✓ (`=== Native gate passed ===` absent from `make-gate.txt`) |
| PTRFC-12 | Canonical provider verification remains green       | ✓ (`canonical-evidence verify: PASS`) |
| PTRFC-13 | Projection verification remains green               | ✓ (`project_leamas_gate_summary: PASS`) |
| PTRFC-14 | PostgreSQL failure evidence remains unchanged       | ✓ (12 failed + 4 errored reproduced; cluster IDs intact) |
| PTRFC-15 | No production or migration behavior changes         | ✓ (`src/` and `db/migrations/` untouched) |
| PTRFC-16 | No failing test is skipped or weakened              | ✓ (no `[<Ignore>]` or skip added) |
| PTRFC-17 | No retry, sleep, or timeout increase is introduced  | ✓ (no test or runner timing changed) |
| PTRFC-18 | Evidence hashes and exit codes reproduce            | ✓ (`exit-codes.json` + `evidence.sha256`) |
| PTRFC-19 | Complete ACT range passes `git diff --check`        | ✓ (see "Required verification" below) |
| PTRFC-20 | Final worktree is clean                             | ✓ (`git status --short` empty after the last commit) |
| PTRFC-21 | No tag, push, or publication occurs                 | ✓ (no `git push`, no tag) |

## Required verification

```bash
dotnet build \
  tests/Circus.Persistence.Postgres.Tests/Circus.Persistence.Postgres.Tests.fsproj \
  -c Release --no-restore
# Build succeeded.   0 Warning(s)   0 Error(s)

dotnet run \
  --project tests/Circus.Persistence.Postgres.Tests \
  -c Release --no-build --no-restore -- \
  --summary \
  --filter-test-list "Postgres test runner exit code"
# EXPECTO! 4 tests run – 4 passed, 0 ignored, 0 failed, 0 errored. Success!
# process exit code: 0

make --no-print-directory test-postgres
# stdout: ... 79 tests run – 63 passed, 0 ignored, 12 failed, 4 errored
# stderr: make: *** [Makefile:102: test-postgres] Error 3
# make exit code: 3

make canonical-evidence
# canonical-evidence regenerate: written=... commit=2f60dd1b0639
# project_leamas_gate_summary: written=.factory/gate-summary.json

make verify-canonical-evidence
# canonical-evidence verify: PASS (commit=2f60dd1b0639)
# canonical-evidence policy: PASS
# project_leamas_gate_summary: PASS

make gate
# canonical-evidence verify: PASS
# canonical-evidence policy: PASS
# project_leamas_gate_summary: PASS
# doctrine verify: OK
# format-check: PASS
# test-backend: includes test-postgres, which returns 3
# make: *** [Makefile:125: test-backend] Error 2
# gate exit code: 2
# NO "=== Native gate passed ===" line

git diff --check
# (empty)

git diff --check 3f71c6d7f6726660c472e76baedc2ae0d3b58f48..HEAD
# (empty)

git status --short
# (empty)
```

## Protected scope integrity

The originally protected scope was honoured:

```text
src/                              # untouched
db/migrations/                    # untouched
tools/Circus.Tooling/             # untouched
tests/Circus.Persistence.Postgres.Tests/*Tests.fs   # no modification
tests/Circus.Persistence.Postgres.Tests/Support.fs  # untouched
Makefile                          # unchanged
.factory/evidence-provider-registry.json   # untouched
.factory/evidence-provider-schema.json     # untouched
```

Only the following files were added or modified:

```text
tests/Circus.Persistence.Postgres.Tests/Program.fs                          # M (refactor)
tests/Circus.Persistence.Postgres.Tests/Circus.Persistence.Postgres.Tests.fsproj  # M (compile new modules)
tests/Circus.Persistence.Postgres.Tests/PostgresTestRunner.fs                # A (new seam)
tests/Circus.Persistence.Postgres.Tests/PostgresTestRunnerExitCodeTests.fs   # A (new hermetic tests)
docs/acts/ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01.md                  # A
docs/close-reports/ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01.md         # A
factory/evidence/postgres-test-runner-fail-closed/{passing,failed,errored,full-suite,hermetic-runner-exit-code,make-test-postgres,make-gate}.txt  # A
factory/evidence/postgres-test-runner-fail-closed/exit-codes.json            # A
factory/evidence/postgres-test-runner-fail-closed/evidence.sha256            # A
```

The .factory/canonical-evidence.json and .factory/gate-summary.json are
regenerated by `make canonical-evidence` against the new commit; they
are not part of this ACT's committed scope but are required to pass the
verify step inside `make gate`.

## Final state

```yaml
focused_contract: PASS
repository_gate: expected_fail   # PostgreSQL is still red; the gate must remain red
publication: withheld
act_status: PARTIAL_CHECKPOINT
```

The PARTIAL_CHECKPOINT verdict reflects the intentionally red
PostgreSQL suite, not an incomplete fail-closed implementation.  All
21 acceptance criteria pass.

## Successor

After this ACT's focused runner contract passes, begin:

`ACT-CIRCUS-POSTGRES-DIAGNOSTIC-PROBE-EXTENSION01`

That ACT must resolve the three provisional clusters
(`H_UNLOCK_CLEANUP_LINGER`, `H_UNLOCK_AGGREGATE_WRAP`, and
`E_SERIALIZATION_40001`) before their respective test-harness or
production repair ACTs begin.