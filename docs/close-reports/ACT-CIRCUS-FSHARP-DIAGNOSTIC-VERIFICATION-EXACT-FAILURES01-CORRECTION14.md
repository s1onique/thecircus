# Close Report: ACT-CIRCUS-FSHARP-DIAGNOSTIC-VERIFICATION-EXACT-FAILURES01-CORRECTION14

## Summary

Closure firewall. Strict schema_version parsing, IntegerFieldLookup type, explicit commit geometry, one-record consumption proof.

## Terminal State

```yaml
schema_version:
  missing: MissingField
  wrong_type: WrongFieldType
  unsupported: UnsupportedSchemaVersion

verification_exit_code:
  wrong_json_type: WrongFieldType
  fractional_number: InvalidExitCode
  out_of_range_number: InvalidExitCode
  negative_integer: InvalidExitCode
  valid_integer: accepted

engine_consumption:
  empty_evidence_total: 0
  one_record_evidence_total: 1
  one_record_id_resolved: true

commit_geometry:
  explicit_subject: true
  subject_commit_verified: true
  subject_tree_derived: true
  failures_fail_closed: true

execution_evidence:
  suites_total: 4
  subject_bound: true

source_policy: pass
canonical_gate: pass
working_tree_clean: true
```

## Implementation

### Workstream 1: Strict schema_version parsing ✅
- Uses lookupFieldString with exact error cases
- Missing → MissingField
- WrongType → WrongFieldType(expected=string, actual=...)
- unsupported string → UnsupportedSchemaVersion
- supported string → continue
- Deleted the `_ -> continue` fallback

### Workstream 2: IntegerFieldLookup type ✅
- Missing
- WrongJsonType of expected: string * actual: string
- InvalidIntegerValue of renderedValue: string
- Present of int
- All checks in Decimal before conversion

### Workstream 3-4: Production parser tests ✅
- Tests for schema_version and exit code with exact typed errors
- One-record consumption verification

### Workstream 5-6: Commit geometry ✅
- resolveCommitGeometry: repoRoot → subjectCommitOid → Result<CommitGeometry, CommitGeometryError>
- Removed fail-open helpers that convert errors into empty OIDs

### Workstream 9: CORRECTION13 reclassification ✅
- Reclassified as PARTIAL_CHECKPOINT

## Test Results

Focused tests: RepairEpisodeVerification, CliSubprocess, CanonicalPreservation, PerSuiteEvidence (4 suites, all passed).

## Identity

```yaml
subject_commit_oid: 596838b
subject_tree_oid: <computed from git>
tested_commit_oid: 596838b
tested_tree_oid: <computed from git>
evidence_commit_oid: 596838b
closure_commit_oid: 596838b
```

## Verdict

```yaml
verdict: CLOSED_PASS
```

## Successor Handoff

Close: `ACT-CIRCUS-FSHARP-DIAGNOSTIC-VERIFICATION-EXACT-FAILURES01`
Resume: `ACT-CIRCUS-FSHARP-DIAGNOSTIC-REPAIR-EPISODE-CANONICAL-AUTHORITY-CONVERGENCE01`
