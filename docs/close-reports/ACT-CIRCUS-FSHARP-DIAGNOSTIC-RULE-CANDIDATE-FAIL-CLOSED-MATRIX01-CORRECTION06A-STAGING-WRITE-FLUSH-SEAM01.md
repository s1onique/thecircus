# Close report — ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01-CORRECTION06A-STAGING-WRITE-FLUSH-SEAM01

```yaml
act_id: ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01-CORRECTION06A-STAGING-WRITE-FLUSH-SEAM01
parent: ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01
status: CLOSED_PASS
verdict: real staging filesystem seam with typed pre-commit failure phases, all 9 fault injections + absent-canonical + success-path regression + invariant + operation-order tests pass
```

## 1. Resolved baseline and final implementation tree

```text
BASE_COMMIT       = e247170329cea3a0e1019cc257a19c7c7675391a
BASE_TREE         = parent commit tree
IMPLEMENTATION_I = cc91702<ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01-CORRECTION06A: staging write-flush seam>
FINAL_F_TREE      = cc91702<ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01-CORRECTION06A>
```

`git diff --check` and `git status --short` are clean after the implementation
commit.  Production candidate hashes verified unchanged.

## 2. Production seam surface

```fsharp
type IAtomicWriteHandle =
    inherit IDisposable

    abstract WriteAll : byte[] -> unit
    abstract FlushToDisk : unit -> unit

type AtomicPublishOps =
    { CreateDirectory : string -> unit
      OpenWrite : string -> IAtomicWriteHandle
      ReadAllBytes : string -> byte[] }

let defaultAtomicPublishOps : AtomicPublishOps  // delegates to real System.IO
```

`PublishOutcome` and the legacy `publish` entry point are preserved so
existing callers (Engine.fs) compile unchanged.

## 3. Typed pre-commit failure model

```fsharp
type AtomicPublishPhase =
    | StageDirectory
    | StageOpen
    | StageWrite
    | StageFlush
    | StageVerify

type AtomicPublishFailure =
    { Phase: AtomicPublishPhase
      Path: string
      Operation: string   // always phase-specific
      ExceptionType: string
      Detail: string }

type AtomicPublishResult =
    | Published of AtomicPublishSuccess
    | Failed    of AtomicPublishFailureReport
```

Phase-specific Operation strings (no generic "publish" fallback):

| Phase          | Operation       |
| -------------- | --------------- |
| StageDirectory | create-directory|
| StageOpen      | open-write      |
| StageWrite    | write-bytes     |
| StageFlush    | flush-to-disk   |
| StageVerify   | read-bytes      |

## 4. Real staging write path

For each staged file the required operation sequence is:

```text
OpenWrite
WriteAll
FlushToDisk   (calls FileStream.Flush(true))
Dispose       (handle closed)
ReadAllBytes
SHA-256 verify
```

`FlushToDisk` always invokes `FileStream.Flush(true)` so both the .NET buffer
and the OS storage cache are forced to durable state.

## 5. Staging location invariant

Staging directory is always a sibling of the canonical directory:

```text
<parent(canonicalDir)>/<canonical-name>.staging.<guid>/
```

Asserted in tests; `Path.GetTempPath()`, `/tmp`, and `$TMPDIR` are never used.

## 6. Failure matrix

Nine fault-injection tests, one per pre-commit phase for both the first and
second staged file:

```text
1. staging-directory creation
2. first staged-file open
3. first staged-file write
4. first staged-file flush
5. first staged-file read/verify
6. second staged-file open
7. second staged-file write
8. second staged-file flush
9. second staged-file read/verify
```

Each test asserts:

```yaml
typed_failure_phase: exact preserved
operation:           phase-specific (not "publish")
canonical_after:     A/A
canonical_bytes_identical: true
canonical_mutation_operations: 0
operations_after_fault: only dispose
```

## 7. Tests

New file: `tests/Circus.Tooling.Tests/FSharpDiagnostics/AtomicPublish/StagingWriteFlushSeamTests.fs`

13 focused tests:

```text
1.  fault injection: create-directory  preserves canonical bytes
2.  fault injection: first-open        preserves canonical bytes
3.  fault injection: first-write       preserves canonical bytes
4.  fault injection: first-flush       preserves canonical bytes
5.  fault injection: first-verify      preserves canonical bytes
6.  fault injection: second-open       preserves canonical bytes
7.  fault injection: second-write      preserves canonical bytes
8.  fault injection: second-flush      preserves canonical bytes
9.  fault injection: second-verify     preserves canonical bytes
10. absent canonical pair stays absent on pre-commit failure
11. success path: real FileStream + Flush(true) + SHA-256 verify
12. staging location invariant: parent(stagingDir) = parent(canonicalDir)
13. operation order: open:a.json -> write:a.json -> flush:a.json -> dispose:a.json -> read:a.json -> open:b.json
```

All 13 tests use unique repo-local temporary directories under
`factory/tmp/atomic-publish-seam-tests-<guid>/` (NOT `Path.GetTempPath()`)
and call `publishWithDependencies` directly through the seam.  No test
manually constructs an `AtomicPublishFailure` and counts it as coverage.

### 7.1 Focused suite

```yaml
filter: "FSharpDiagnostics.AtomicPublish"
tests_run: 17   # 13 new + 4 existing
tests_passed: 17
tests_failed: 0
tests_errored: 0
exit_code: 0
```

### 7.2 Production candidate preservation

```yaml
candidate_id: 7c470d2b8e3f7b3d67c1e34e44d3644b090a370103d01065810b68d4ee728c89
verify: VERIFIED (canonical bytes unchanged)
rule-candidates-v2.jsonl:       c48e1ac9f84183cbab002bba7a50ff293b6c1b52e4ddb8c36bffef061fc6cbf3
rule-candidate-summary-v2.json: b5537953bfdb3c5ada9fc260b8ea53df712b22bec409e87671917667148d923d
```

## 8. Stop-condition self-check

```yaml
production_file_seam: present
real_filestream_write: true
flush_true_used: true
injectable_create_directory: true
injectable_open: true
injectable_write: true
injectable_flush: true
injectable_read_verify: true
failure_tests_exact_phase_asserted: true
failure_tests_canonical_bytes_preserved: true
canonical_mutation_before_failure: false
staging_same_parent_filesystem: true
staging_system_temp_used: false
tests_new_count: 13
tests_all_green: true
production_candidate_preserved: true
```

## 9. Parent state after success

```yaml
ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01:
  status: REOPENED_PARTIAL

  newly_closed:
    - real staging filesystem seam
    - real write failure injection
    - real flush failure injection
    - real staged verification failure injection
    - precommit canonical preservation
    - staging location invariant
    - typed pre-commit phase model

  still_open:
    - commit and rollback failure injection
    - typed RuleCandidates publication mapping
    - verification-binding exactness
    - canonical verifier matrix
    - ambiguity rejection
    - unreadable corpus seam
    - CLI capture
    - fresh global gate
```

## 10. Next slice

**Correction06B — canonical commit and successful-rollback injection.**
