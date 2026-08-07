# Close report — ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01-CORRECTION06B-CANONICAL-COMMIT-AND-SUCCESSFUL-ROLLBACK-INJECTION01

```yaml
act_id: ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01-CORRECTION06B-CANONICAL-COMMIT-AND-SUCCESSFUL-ROLLBACK-INJECTION01
parent: ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01
status: CLOSED_PASS
verdict: real canonical install seam with successful rollback injection; 9 new tests + 26-suite regression all pass with byte-for-byte recovery
```

## 1. Resolved baseline and final implementation tree

```text
BASE_COMMIT     = 022d1963846a2a850da63ae42b66a1fcf3663e71
BASE_TREE       = (BASE_COMMIT tree)
I:
  implementation_commit = 88fd353
  implementation_tree   = (recorded by D)
F:
  meaning: this report commit (the commit whose tree contains this file)
  self_sha_recorded_inside_report: false   # recursive F is impossible by
                                           # construction; F is recorded by D
                                           # after the report is committed
```

The implementation commit `88fd353` extends the shared `AtomicPublish` filesystem
seam from pre-commit staging into the canonical install path.  Every
canonical mutation, including successful rollback, is observable through
the seam.  `git diff --check` and `git status --short` are clean after the
report commit.  Production candidate hashes verified unchanged.

## 2. Production seam surface (commit + rollback)

```fsharp
type AtomicPublishOps =
    {
      // Pre-commit staging (Correction06A).
      CreateDirectory : string -> unit
      OpenWrite       : string -> IAtomicWriteHandle
      ReadAllBytes    : string -> byte[]

      // Correction06B commit + rollback seam.
      FileExists      : string -> bool
      MoveFile        : string -> string -> unit                // source, destination
      ReplaceFile     : string -> string -> string -> unit       // source, destination, backup
      DeleteFile      : string -> unit
    }

let defaultAtomicPublishOps : AtomicPublishOps =  // delegates to real System.IO
```

The four new seam operations cover every filesystem primitive required
by canonical install and successful rollback.  No direct `File.*` calls
remain in the commit / rollback code paths.

## 3. Typed commit phase model

```fsharp
type AtomicPublishPhase =
    // Pre-commit staging (Correction06A).
    | StageDirectory | StageOpen | StageWrite | StageFlush | StageVerify

    // Correction06B commit + rollback.
    | Snapshot            // capturing pre-commit canonical state
    | Backup               // reserved for a future distinct-backup op
    | Install              // ReplaceFile or MoveFile into canonical
    | RollbackDelete       // removing a newly installed canonical file
    | RollbackRestore      // restoring a backed-up canonical file
```

The `Backup` phase is reserved; the current production implementation
folds backup into `ReplaceFile` (`File.Replace` writes the backup as
part of the swap).  It is retained in the DU so a future slice that
exposes backup as a separate operation can carry the exact phase
without reshaping the type.

## 4. Recovery state

```fsharp
type AtomicRecoveryState =
    | NeverModified
    | RestoredByteIdentical
    | Committed
```

`NeverModified` is set when no canonical mutation occurred before the
failure.  `RestoredByteIdentical` is set when at least one canonical
file was mutated and rolled back, AND the post-rollback bytes match the
pre-snapshot.  `Committed` is set on full success.  Rollback-failure
mapping to `MayHaveChanged` is reserved for Correction06C.

## 5. Commit / rollback discipline

For the canonical pair `candidate + summary`:

```text
1. snapshotCanonicalPair(ops, canonicalDir, files)
   -> FileExists + ReadAllBytes for each (records the pre-state)
2. CreateDirectory(staging)
3. for each f in files:
     OpenWrite(staging/f)
     WriteAll
     FlushToDisk        (calls FileStream.Flush(true))
     Dispose
     ReadAllBytes       (verify-after-write on disk)
     SHA-256 verify
4. commitCanonicalPairFromStaging:
   4a. commitOneFile candidate   -> ReplaceFile(staged, canonical, backup)
                                     OR MoveFile(staged, canonical)
   4b. if candidate succeeded:
       commitOneFile summary    -> same
       if summary failed:
           rollbackAttempted = true
           rollbackOneFile candidate:
             DeleteFile(canonical) then MoveFile(backup, canonical)
             OR DeleteFile(canonical) (if previous was absent)
```

Path discipline: `parent(stagingDir) = parent(canonicalDir)` and
`parent(backupPath) = parent(canonicalDir)`.  No backup or staging is
placed under `Path.GetTempPath()` or `/tmp`.  Every filesystem primitive
runs through `ops`.

## 6. Failure matrix (Correction06B)

```text
1. candidate install fails, existing A/A
   -> canonical A/A preserved, no rollback attempted
   -> RecoveryState: NeverModified
2. candidate install fails, Absent/Absent
   -> canonical stays Absent/Absent
   -> RecoveryState: NeverModified
3. summary install fails, existing A/A
   -> candidate temporarily B, rollback restores A/A
   -> RecoveryState: RestoredByteIdentical
4. summary install fails, Absent/Absent
   -> candidate installed, then removed by rollback
   -> RecoveryState: RestoredByteIdentical
5. exact operation-order test (existing A/A rollback)
   -> snapshot -> stage -> replace candidate -> replace summary (fault)
   -> rollback delete -> rollback move
   -> no second publication attempt
6. exact operation-order test (absent rollback)
   -> snapshot -> stage -> move candidate -> move summary (fault)
   -> rollback delete candidate
   -> no second publication attempt
```

Rollback-failure injection (`CanonicalStateMayHaveChanged`) is
explicitly deferred to Correction06C.

## 7. Tests

New file: `tests/Circus.Tooling.Tests/FSharpDiagnostics/AtomicPublish/CommitRollbackSeamTests.fs`

9 focused tests:

```text
1.  existing A/A -> B/B success; recovery state Committed
2.  candidate install failure (existing A/A); canonical A/A preserved
3.  summary install failure (existing A/A); rollback restores A/A
4.  candidate install failure (Absent/Absent); canonical stays Absent/Absent
5.  summary install failure (Absent/Absent); rollback removes candidate
6.  operation order (existing A/A rollback)
7.  operation order (Absent rollback)
8.  canonical snapshot distinguishes Absent from zero-byte Present
9.  backup / staging paths remain under canonical parent
```

All 9 tests use unique repo-local temporary directories under
`factory/tmp/atomic-publish-commit-rollback-tests-<guid>/` (NOT
`Path.GetTempPath()`) and call `publishWithDependencies` directly
through the seam.  Every fault is injected through a real production
seam operation.  No test manually constructs an `AtomicPublishFailure`
value and counts it as coverage.

The pre-commit `StagingWriteFlushSeamTests.fs` was updated to keep its
9 pre-commit fault paths working against the new snapshot-then-stage
order.  Specifically, the verify-after-write fault is now targeted at
the SECOND occurrence of the read (the verify), not the first (which
is the snapshot read of the same filename).  The existing
`AtomicPublishTests.fs` legacy tests were updated from one-file to
two-file publication to match the new canonical-pair requirement.

### 7.1 Focused suite (authoritative for this slice)

```yaml
filter: "FSharpDiagnostics.AtomicPublish.CommitRollback"
tests_run:   9
tests_passed: 9
tests_failed: 0
tests_errored: 0
exit_code: 0
```

### 7.2 Regression suite (authoritative for Correction06A preservation)

```yaml
filter: "FSharpDiagnostics.AtomicPublish"
tests_run:   26
tests_passed: 26
tests_failed: 0
tests_errored: 0
exit_code: 0
```

This combines the 13 pre-commit tests (Correction06A) + the 4 legacy
AtomicPublish tests + the 9 new CommitRollback tests.  All pass.

### 7.3 Production candidate preservation

```yaml
candidate_id: 7c470d2b8e3f7b3d67c1e34e44d3644b090a370103d01065810b68d4ee728c89
verify: VERIFIED (canonical bytes unchanged)
rule-candidates-v2.jsonl:       c48e1ac9f84183cbab002bba7a50ff293b6c1b52e4ddb8c36bffef061fc6cbf3
rule-candidate-summary-v2.json: b5537953bfdb3c5ada9fc260b8ea53df712b22bec409e87671917667148d923d
```

## 8. Stop-condition self-check

```yaml
production_file_seam_commit:
  canonical_replace_injectable: true   (ReplaceFile seam fault test)
  canonical_move_injectable:    true   (MoveFile seam fault test for absent)
  canonical_delete_injectable:   true   (DeleteFile used in rollback path)
snapshot:
  absent_distinguished_from_zero_byte: true
existing_pair:
  success_A_to_B: true
candidate_install_failure:
  canonical_A_A_preserved: true
  rollback_attempted: false
summary_install_failure:
  candidate_temporarily_changed: true
  rollback_attempted: true
  canonical_A_A_restored: true
  recovery_state: RestoredByteIdentical
first_publication:
  candidate_failure_preserves_absence: true
  summary_failure_rolls_back_candidate: true
  final_state_absent_absent: true
operation_order:
  exact: true
  second_publication_attempt: false
path_discipline:
  staging_same_parent: true
  backup_same_parent: true
  system_temp_used: false
tests:
  new: 9
  all_relevant_suites_green: true
production_candidate_preserved: true
parent_act:
  status: REOPENED_PARTIAL
```

## 9. Parent state after success

```yaml
ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01:
  status: REOPENED_PARTIAL

  newly_closed:
    - real canonical install seam (ReplaceFile / MoveFile)
    - candidate install failure injection
    - summary install failure injection
    - successful rollback to previous canonical pair (existing A/A)
    - successful rollback to previous canonical pair (Absent/Absent)

  still_open:
    - rollback failure injection
    - cleanup failure injection
    - post-install verification failure
    - typed RuleCandidates publication mapping
    - verification-binding exact assertions
    - canonical verifier matrix
    - ambiguity rejection restoration
    - unreadable corpus seam
    - CLI capture
    - fresh global gate
```

## 10. Next slice

**Correction06C — rollback failure injection + recovery evidence.**

That slice introduces `CanonicalStateMayHaveChanged`, retained backups,
and rollback-failure injection.  It is the third and final commit slice
in the canonical-pair publication matrix.
