# Close report — ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01-CORRECTION06B-CANONICAL-COMMIT-AND-SUCCESSFUL-ROLLBACK-INJECTION01

```yaml
act_id: ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01-CORRECTION06B-CANONICAL-COMMIT-AND-SUCCESSFUL-ROLLBACK-INJECTION01
parent: ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01
status: CLOSED_PASS
verdict: real canonical install seam with successful rollback injection; 13 CommitRollback tests + 30-suite regression all pass; reviewer-identified defects (cardinality, MayHaveChanged, missing-backup, vacuous path test) all addressed
```

## 1. Resolved baseline and final implementation tree

```text
BASE_COMMIT     = 022d1963846a2a850da63ae42b66a1fcf3663e71
BASE_TREE       = (BASE_COMMIT tree)
I:
  implementation_commit = 15df6db
  implementation_tree   = (recorded by D)
F:
  meaning: this report commit (the commit whose tree contains this file)
  self_sha_recorded_inside_report: false   # recursive F is impossible by
                                           # construction; F is recorded by D
                                           # after the report is committed
```

The implementation commit `15df6db` extends the shared `AtomicPublish`
filesystem seam from pre-commit staging into the canonical install path,
**and** addresses the five reviewer-identified defects from the prior
attempt (cardinality acceptance, fail-closed post-rollback verification,
MayHaveChanged semantics, missing-backup typed failure, non-vacuous
backup-path test).  Every canonical mutation, including successful
rollback, is observable through the seam.  `git diff --check` and
`git status --short` are clean after the report commit.  Production
candidate hashes verified unchanged.

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

No direct `File.*` calls remain in the commit / rollback / snapshot
code paths.  Snapshot reads, commit replaces, rollback deletes, and
rollback restores all run through `ops`.

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
part of the swap).

## 4. Recovery state (reviewer-fixed)

```fsharp
type AtomicRecoveryState =
    | NeverModified             // canonical pair was NEVER mutated by this call
    | RestoredByteIdentical     // canonical pair was mutated AND rolled back to pre-snapshot bytes
    | Committed                  // success: canonical pair now equals staged bytes
    /// The canonical pair was mutated and may not have been restored to its
    /// pre-publication bytes.  Returned when a commit failure triggered a
    /// rollback attempt whose post-state either differs from the
    /// pre-snapshot OR could not be observed at all.
    | MayHaveChanged            // honest signal: post-state unknown or changed
```

The recovery-state matrix is:

```text
                            snapshot observed?    rollback attempted?    preserved?
NeverModified               yes                   no                    (yes)
RestoredByteIdentical       yes                   yes                   yes
MayHaveChanged              yes                   yes                   no
MayHaveChanged              no                    any                   n/a
```

`NeverModified` is NEVER returned for any state that has been
mutated, whether or not the mutation was successfully reverted.  This
prevents the prior `NeverModified`-on-known-changed fail-open
behaviour identified by the reviewer.

## 5. Cardinality check (reviewer-fixed)

```fsharp
// At the top of publishWithDependencies:
if not (match files with [ _c; _s ] -> true | _ -> false) then
    AtomicPublishResult.Failed
        { Failures = [ { Phase = Install
                         Operation = "canonical-pair-cardinality"
                         Detail = "…requires exactly two pending files, got N" } ]
          CanonicalByteIdenticalAfterFailure = true
          RetainedStagingPath = None
          RecoveryState = NeverModified }
```

This cardinality rejection fires BEFORE any staging, snapshot, or
canonical I/O.  Zero, one, and three-file inputs are all rejected
with a typed Install-phase cardinality failure and `NeverModified`
recovery state.  The three new tests
(`canonicalPairCardinalityEmptyTest`, `…OneFileTest`, `…ThreeFilesTest`)
prove zero filesystem mutation.

## 6. Missing-backup typed failure (reviewer-fixed)

```fsharp
// Inside rollbackOneFile, after DeleteFile(canonicalPath):
if not (ops.FileExists bp) then
    accumulatedFailures.Add(
        { Phase = RollbackRestore
          Path = canonicalPath
          Operation = operationForPhase RollbackRestore
          ExceptionType = ""
          Detail = "expected rollback backup is missing" })
else
    try ops.MoveFile bp canonicalPath with …
```

A missing backup is no longer a silent no-op.  The test
`missingBackupRollbackTest` proves the seam surfaces a typed
`RollbackRestore` failure and the recovery state is `MayHaveChanged`.

## 7. Path discipline (reviewer-fixed)

The test now asserts the actual design:

```text
backup_parent:
  equals: canonicalDir          # backup is a sibling of canonical file
staging_parent:
  equals: parent(canonicalDir)  # staging is a sibling of canonical dir
```

The vacuous assertion (`for bp in backups do …`) is gone.  The test
now requires at least one backup file inside `canonicalDir` and
verifies every backup's parent is exactly `canonicalDir`.

## 8. Tests

New file: `tests/Circus.Tooling.Tests/FSharpDiagnostics/AtomicPublish/CommitRollbackSeamTests.fs`

13 focused tests:

```text
1.  existing A/A -> B/B success; recovery state Committed
2.  candidate install failure (existing A/A); canonical A/A preserved
3.  summary install failure (existing A/A); rollback restores A/A
4.  candidate install failure (Absent/Absent); canonical stays Absent/Absent
5.  summary install failure (Absent/Absent); rollback removes candidate
6.  operation order (existing A/A rollback)
7.  operation order (Absent rollback)
8.  canonical snapshot distinguishes Absent from zero-byte Present
9.  backup / staging paths inside canonicalDir; same-parent filesystem
10. canonical pair cardinality: zero files -> cardinality failure, no I/O
11. canonical pair cardinality: one file -> cardinality failure, no I/O
12. canonical pair cardinality: three files -> cardinality failure, no I/O
13. missing-backup rollback -> typed RollbackRestore, MayHaveChanged
```

### 8.1 Focused suite

```yaml
filter: "FSharpDiagnostics.AtomicPublish.CommitRollback"
tests_run:   13
tests_passed: 13
tests_failed: 0
tests_errored: 0
exit_code: 0
```

### 8.2 Regression suite

```yaml
filter: "FSharpDiagnostics.AtomicPublish"
tests_run:   30
tests_passed: 30
tests_failed: 0
tests_errored: 0
exit_code: 0
```

### 8.3 Production candidate preservation

```yaml
candidate_id: 7c470d2b8e3f7b3d67c1e34e44d3644b090a370103d01065810b68d4ee728c89
verify: VERIFIED (canonical bytes unchanged)
rule-candidates-v2.jsonl:       c48e1ac9f84183cbab002bba7a50ff293b6c1b52e4ddb8c36bffef061fc6cbf3
rule-candidate-summary-v2.json: b5537953bfdb3c5ada9fc260b8ea53df712b22bec409e87671917667148d923d
```

## 9. Stop-condition self-check

```yaml
production_file_seam_commit:
  canonical_replace_injectable: true
  canonical_move_injectable:    true
  canonical_delete_injectable:   true

snapshot:
  absent_distinguished_from_zero_byte: true

existing_pair:
  success_A_to_B: true

candidate_install_failure:
  canonical_A_A_preserved: true
  rollback_attempted: false
  recovery_state: NeverModified

summary_install_failure_existing:
  candidate_temporarily_changed: true
  rollback_attempted: true
  canonical_A_A_restored: true
  recovery_state: RestoredByteIdentical

first_publication:
  candidate_failure_preserves_absence: true
  summary_failure_rolls_back_candidate: true
  final_state_absent_absent: true
  recovery_state: RestoredByteIdentical

operation_order:
  existing: exact
  absent:   exact
  second_publication_attempt: false

cardinality_rejection:
  zero_files:  proven (no I/O)
  one_file:    proven (no I/O)
  three_files: proven (no I/O)

missing_backup_rollback:
  typed_failure_surfaced: true
  recovery_state: MayHaveChanged

post_rollback_observation:
  never_substituted_with_preSnap: true
  unknown_state_returns_MayHaveChanged: true

path_discipline:
  backup_parent: canonicalDir        # asserted, not vacuous
  staging_parent: parent(canonicalDir)
  system_temp_used: false

tests:
  new: 13
  all_relevant_suites_green: true

production_candidate_preserved: true

parent_act:
  status: REOPENED_PARTIAL
```

## 10. Parent state after success

```yaml
ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01:
  status: REOPENED_PARTIAL

  newly_closed:
    - real canonical install seam (ReplaceFile / MoveFile)
    - candidate install failure injection
    - summary install failure injection
    - successful rollback to previous canonical pair (existing A/A)
    - successful rollback to previous canonical pair (Absent/Absent)
    - exact canonical-pair cardinality rejection (0 / 1 / 3 files)
    - missing-backup typed RollbackRestore failure (no silent no-op)
    - honest MayHaveChanged recovery state (no NeverModified-on-changed)
    - non-vacuous backup-path assertion (parent == canonicalDir)

  still_open:
    - rollback failure injection (next slice: Correction06C)
    - cleanup failure injection
    - post-install verification failure injection
    - typed RuleCandidates publication mapping
    - verification-binding exact assertions
    - canonical verifier matrix
    - ambiguity rejection restoration
    - unreadable corpus seam
    - CLI capture
    - fresh global gate
```

## 11. Next slice

**Correction06C — rollback failure injection + recovery evidence.**

That slice introduces `CanonicalStateMayHaveChanged` retention
semantics, retained backups, and explicit rollback-failure injection.
`MayHaveChanged` is already present in Correction06B so 06C can
build on it rather than repair 06B semantics first.
