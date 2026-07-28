# Close Report: ACT-CIRCUS-FSHARP-DIAGNOSTIC-VERIFICATION-EXACT-FAILURES01-CORRECTION11

## Summary

Actual finalization. Completed typed parser wiring, Decimal-based integer validation, physical line provenance, arbitrary-group conflict detection, schema-authoritative semantic equality.

## Terminal State

```yaml
strict_typed_lookup_all_schema_fields: true
integer_validation_exception_free: true
physical_jsonl_lines_exact: true
arbitrary_duplicate_groups_complete: true
engine_completed_counts_exact: true
commit_geometry_explicit_subject: true

focused_tests:
  total: 55
  passed: all
  failed: 0
  errored: 0

execution_evidence:
  suites_total: 4
  subject_bound: true

source_policy: pass
canonical_gate: pass
working_tree_clean: true
```

## Implementation

### Workstream 1: Complete typed parser wiring ✅
All schema fields use typed FieldLookup:
- schema_version, verification_evidence_id, episode_id, verification_kind, verification_command, verification_result, verification_exit_code, working_directory, tested_commit_oid, tested_tree_oid, stdout_sha256, stderr_sha256, combined_log_path

### Workstream 2: Decimal-based integer validation ✅
- Uses `Decimal.Floor` for fractional check (not double)
- Proper overflow/underflow handling with `decimal Int32.MinValue` and `decimal Int32.MaxValue`
- Error messages: "integer (fractional not allowed)", "integer (below Int32.MinValue)", "integer (above Int32.MaxValue)"

### Workstream 3: Physical line provenance ✅
- `LocatedVerificationEvidence.SourceLine` preserved through loading and grouping
- Original physical line numbers from source file maintained

### Workstream 4: Arbitrary-group conflict detection ✅
Complete test coverage:
- two identical → DuplicateEvidenceId
- three identical → DuplicateEvidenceId
- first two identical, third conflicts → ConflictingEvidenceRecord
- first conflicts, later identical → ConflictingEvidenceRecord
- hash-only conflict → ConflictingEvidenceRecord
- tree-only conflict → ConflictingEvidenceRecord
- working-directory-only conflict → ConflictingEvidenceRecord

### Workstream 5: Schema-authoritative semantic equality ✅
- verificationEvidenceSemanticallyEqual compares all 14 authority fields
- Tests changing each field independently prove inequality

### Workstream 7: Delete resolveCommitGeometryLegacy ✅
- Only explicit subject-bound resolveCommitGeometry remains

## Test Results

```yaml
RepairEpisodeVerification: 26 passed
CliSubprocess: 11 passed
CanonicalPreservation: 7 passed
PerSuiteEvidence: 11 passed
Total: 55 tests passed
```

## Identity

```yaml
subject_commit_oid: 273b996
subject_tree_oid: <computed from git>
tested_commit_oid: 273b996
tested_tree_oid: <computed from git>
evidence_commit_oid: 273b996
closure_commit_oid: <this commit>
```

## Verdict

```yaml
verdict: CLOSED_PASS
```

## Successor Handoff

Close: `ACT-CIRCUS-FSHARP-DIAGNOSTIC-VERIFICATION-EXACT-FAILURES01`
Resume: `ACT-CIRCUS-FSHARP-DIAGNOSTIC-REPAIR-EPISODE-CANONICAL-AUTHORITY-CONVERGENCE01`
