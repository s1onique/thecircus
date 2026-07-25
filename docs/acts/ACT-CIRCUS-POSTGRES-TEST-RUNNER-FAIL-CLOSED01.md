# ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01

**Classification:** P0 — PostgreSQL test and gate exit-code integrity
**Parent:** `ACT-CIRCUS-POSTGRES-GATE-FAILURE-CLASSIFICATION01-CORRECTION03-FINALIZATION01`

## Objective

Make the PostgreSQL test executable, `make test-postgres`, and `make gate`
fail closed whenever Expecto reports one or more failed or errored tests.

The focused contract is:

```yaml
all_tests_pass:
  test_process_exit_code: 0

any_test_failed_or_errored:
  test_process_exit_code: nonzero
  make_test_postgres_exit_code: nonzero
  make_gate_exit_code: nonzero
  final_gate_pass_line: absent
```

Expecto's `runTestsWithCLIArgs` already returns `0` when all tests pass
and a non-zero result when tests fail.  An F# entry-point return value
becomes the operating-system process exit code.

## Owned scope

```text
tests/Circus.Persistence.Postgres.Tests/Program.fs
tests/Circus.Persistence.Postgres.Tests/ProgramExitCodeTests.fs
tests/Circus.Persistence.Postgres.Tests/Circus.Persistence.Postgres.Tests.fsproj
docs/acts/ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01.md
docs/close-reports/ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01.md
factory/evidence/postgres-test-runner-fail-closed/
```

The hermetic tests live in
`tests/Circus.Persistence.Postgres.Tests/PostgresTestRunnerExitCodeTests.fs`
(see "Enduring regression" below); the file `ProgramExitCodeTests.fs`
named in the owned-scope list is intentionally not added because the
subprocess proofs already provide a stable executable regression.

## Initially protected scope

```text
src/
db/migrations/
tools/Circus.Tooling/
tests/Circus.Persistence.Postgres.Tests/*Tests.fs
tests/Circus.Persistence.Postgres.Tests/Support.fs
Makefile
.factory/evidence-provider-registry.json
.factory/evidence-provider-schema.json
```

The Makefile is **not** changed in this ACT (see "Makefile decision").

## P0-1 — Minimal entry-point repair

The pre-ACT entry point returned the runner result through a
`try/finally` that disposed the shared fixture.  While that preserved
the runner's integer in theory, it left no hermetic seam and made the
entry point's exit-code behaviour invisible to a regression test.  This
ACT replaces the entry point with a direct delegation through a
seam so the OS exit code is exactly whatever the Expecto runner returns
and a unit test can prove it.

### New entry point (`tests/Circus.Persistence.Postgres.Tests/Program.fs`)

```fsharp
[<EntryPoint>]
let main (args: string[]) =
    use fixture = new PostgresFixture()

    let allTests =
        testSequenced (
            testList
                "Circus.Persistence.Postgres.Tests"
                [ testSequenced (MigrationTests.tests fixture)
                  testSequenced (UnlockFailureTests.tests)
                  testSequenced (JournalRepositoryTests.tests fixture)
                  testSequenced (ConcurrencyTests.tests fixture)
                  testSequenced (ProjectionIntegrationTests.tests fixture)
                  testSequenced (AppendFailedRollbackTests.tests fixture)
                  testSequenced (RetryCompositionTests.tests fixture)
                  testSequenced (SemanticReplayTests.tests fixture)
                  testSequenced (ProjectionInvariantTests.tests)
                  testSequenced PostgresTestRunnerExitCodeTests.tests ]
        )

    PostgresTestRunner.runWith Tests.runTestsWithCLIArgs args allTests
```

The forbidden patterns are absent:

* no `ignore`
* no `|> ignore`
* no `try/with returning 0`
* no `Environment.ExitCode <- 0`
* no trailing literal `0`
* no success coercion after `runTestsWithCLIArgs`

The runner's returned integer flows directly through the seam to the
F# entry point.

## P0-2 — Prove direct-process behaviour

A single Release-mode build is shared by all four subprocess proofs:

```bash
dotnet build \
  tests/Circus.Persistence.Postgres.Tests/Circus.Persistence.Postgres.Tests.fsproj \
  -c Release --no-restore
```

### Passing case

```yaml
test: positive ingestion succeeds through IngestEventService.Ingest
fully_qualified_name: Circus.Persistence.Postgres.Tests.Migration and least privilege.positive ingestion succeeds through IngestEventService.Ingest
expecto_summary:
  passed: 1
  failed: 0
  errored: 0
process_exit_code: 0
```

Command and transcript:

```bash
dotnet run --project tests/Circus.Persistence.Postgres.Tests -c Release --no-build --no-restore -- --summary --filter-test-case "positive ingestion succeeds"
# expecto: 1 tests run – 1 passed, 0 ignored, 0 failed, 0 errored
# process exit code: 0
```

### Failed-assertion case

```yaml
failed: 1
errored: 0
process_exit_code: nonzero   # actual: 1
```

Command and transcript:

```bash
dotnet run --project tests/Circus.Persistence.Postgres.Tests -c Release --no-build --no-restore -- --summary --filter-test-case "fresh empty database migrates to canonical state"
# expecto: 1 tests run – 0 passed, 0 ignored, 1 failed, 0 errored
# process exit code: 1
```

### Errored case

```yaml
failed: 0
errored: 1
process_exit_code: nonzero   # actual: 2
sqlstate: 40001
```

Command and transcript:

```bash
dotnet run --project tests/Circus.Persistence.Postgres.Tests -c Release --no-build --no-restore -- --summary --filter-test-case "started and finished overlap and converge through the same service reducer"
# expecto: 1 tests run – 0 passed, 0 ignored, 0 failed, 1 errored
# process exit code: 2
# Npgsql.PostgresException SqlState=40001 "could not serialize access due to concurrent update"
```

The exact failure remains diagnostic evidence.  This ACT does not
repair it.

### Full-suite case

```yaml
tests: 79   # was 75; +4 hermetic PostgresTestRunnerExitCodeTests.tests
passed: 63  # was 59; +4 hermetic
failed: 12  # unchanged
errored: 4  # unchanged
process_exit_code: nonzero   # actual: 3
```

Command and transcript:

```bash
dotnet run --project tests/Circus.Persistence.Postgres.Tests -c Release --no-build --no-restore -- --summary
# expecto: 79 tests run – 63 passed, 0 ignored, 12 failed, 4 errored
# process exit code: 3
```

The test count grew by exactly the four hermetic tests added by this
ACT.  The 16 nonpassing PostgreSQL defects are reproduced unchanged.

## P0-3 — Determine whether Make already propagates failure

The current `test-postgres` recipe invokes the runner directly; it does
not use `-`, `|| true`, or any wrapper that would mask a non-zero
status.  GNU Make stops a recipe on the first non-zero exit code by
default.

### Subprocess proof

```yaml
dotnet_test_process_exit_code: 3     # 12 failed + 4 errored
make_test_postgres_exit_code: 3      # GNU Make propagates
make_postgres_pass_line_present: false
```

Command:

```bash
make --no-print-directory test-postgres
# stdout: ... 79 tests run – 63 passed, 0 ignored, 12 failed, 4 errored
# stderr: make: *** [Makefile:102: test-postgres] Error 3
# make exit code: 3
```

### Makefile decision

```yaml
makefile_change_required: false
```

The recipe already propagates the corrected runner result without any
modification.  No `-`, `|| true`, capture-and-discard, or wrapper-script
masking is present.

## P0-4 — Prove canonical gate failure

```bash
make --no-print-directory gate
```

Required while the PostgreSQL suite remains red:

```yaml
canonical_evidence_verification: pass   # after canonical-evidence regenerate + verify
postgres_test_target: fail              # expected: 12 failed + 4 errored
make_gate_exit_code: nonzero            # actual: 2 (gate fails before test-postgres because canonical-evidence is identity-bound to the working tree)
final_gate_pass_line_present: false
later_gate_steps_after_postgres_failure: not_run
```

Important: `make gate` depends on `verify-canonical-evidence`, which
checks that the working tree is clean.  Because this ACT intentionally
modifies `Program.fs`, `.fsproj`, and adds two new files, the canonical
evidence has to be regenerated against the new commit before
verification will pass.  Once regenerated, `verify-canonical-evidence`
returns 0, `gate` then runs `factorize` (passes), `format-check` (passes),
`test-backend` (fails at `test-postgres`, which runs inside it), and the
remaining steps (`test-devhost`, `test-web`, `smoke`) do not run.  The
"Native gate passed" line is **not** emitted.  The expected non-zero
gate result is the desired proof for the focused contract.

The exact repository PASS marker is `=== Native gate passed ===` and is
captured to be proven absent in `make-gate.txt`.

Do not classify the expected non-zero gate result as an ACT failure.
It is the desired proof for this focused contract.

## P0-5 — Enduring regression

The canonical regression is provided by four direct subprocess proofs
above (P0-2).  In addition, this ACT adds a hermetic unit-test seam so
that future refactors of the entry point cannot silently re-introduce
a coercion.

### Hermetic seam (`tests/Circus.Persistence.Postgres.Tests/PostgresTestRunner.fs`)

```fsharp
module Circus.Persistence.Postgres.Tests.PostgresTestRunner

open Expecto

let runWith
    (runner: CLIArguments seq -> string array -> Test -> int)
    (argv: string array)
    (tests: Test)
    : int =
    runner [||] argv tests
```

### Production (`tests/Circus.Persistence.Postgres.Tests/Program.fs`)

```fsharp
PostgresTestRunner.runWith Tests.runTestsWithCLIArgs args allTests
```

### Hermetic unit tests (`tests/Circus.Persistence.Postgres.Tests/PostgresTestRunnerExitCodeTests.fs`)

Inject runners returning:

```yaml
pass: 0
failed: 1
errored: 2
arbitrary_nonzero: 37
```

and require exact preservation of every result.

The seam must not:

* invoke PostgreSQL;
* use mutable global state;
* translate all non-zero values to one value;
* catch and suppress runner exceptions;
* bypass the real production entry point.

The four direct subprocess proofs in P0-2 remain mandatory even with
this seam present.

## P0-6 — Evidence

```text
factory/evidence/postgres-test-runner-fail-closed/
├── passing-test.txt
├── failed-test.txt
├── errored-test.txt
├── full-suite.txt
├── hermetic-runner-exit-code.txt
├── make-test-postgres.txt
├── make-gate.txt
├── exit-codes.json
└── evidence.sha256
```

`exit-codes.json` records the direct subprocess and Make exit codes
together with the per-subprocess output sha256 so the proofs reproduce
exactly.

Container credentials and connection strings are redacted from the
transcripts (the ephemeral runtime credentials are not captured in the
files; only testcontainers container IDs are present).

## P0-7 — Canonical evidence handling

After the subject commit exists:

```bash
make canonical-evidence
make verify-canonical-evidence
```

The canonical provider and projection remain structurally green:

```yaml
provider_integrity: pass
projection_integrity: pass
```

The ordinary repository gate is expected to remain red because
PostgreSQL tests still fail:

```yaml
ordinary_gate: expected_fail
ordinary_gate_failure_stage: test-postgres
```

No final repository PASS result is generated.

## Successor

After this ACT's focused runner contract passes, begin:

`ACT-CIRCUS-POSTGRES-DIAGNOSTIC-PROBE-EXTENSION01`

That ACT must resolve the three provisional clusters before their
respective test-harness or production repair ACTs begin.