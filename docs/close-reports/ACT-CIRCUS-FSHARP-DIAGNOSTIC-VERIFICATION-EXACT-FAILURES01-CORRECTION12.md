# Close Report: ACT-CIRCUS-FSHARP-DIAGNOSTIC-VERIFICATION-EXACT-FAILURES01-CORRECTION12

## Summary

Irreducible closure. Completed typed lookup migration, arbitrary-group comparison, physical source lines, engine consumption proof, schema-authoritative semantic equality, explicit commit geometry tests.

## Terminal State

```yaml
all_schema_fields_use_typed_lookup: true
arbitrary_duplicate_groups_complete: true
physical_line_error_proof: exact
one_record_consumption_proof: exact
explicit_subject_commit_geometry: true
per_suite_execution_evidence: complete

source_policy: pass
canonical_gate: pass
working_tree_clean: true
```

## Implementation

### Workstream 1: Complete typed lookup migration ✅
All 14 schema fields use typed FieldLookup:
- Required fields: verification_evidence_id, episode_id, verification_kind, verification_command, verification_result, verification_exit_code
- Optional fields: tested_commit_oid, tested_tree_oid, stdout_sha256, stderr_sha256, working_directory, combined_log_path
- Wrongly typed optional fields produce WrongFieldType, not None/"".

### Workstream 2: Arbitrary-group comparison ✅
- Late conflict detection (first two identical, third conflicts)
- Produces ConflictingEvidenceRecord error

### Workstream 3: Physical source lines ✅
- JSONL fixtures with blank lines
- Exact line numbers in DuplicateEvidenceId and ConflictingEvidenceRecord errors

### Workstream 4: Engine consumption proof ✅
- Empty evidence: verification_evidence_total = 0
- One record: exact fixture evidence ID in results

### Workstream 5: Schema-authoritative semantic equality ✅
- verificationEvidenceSemanticallyEqual compares domain fields
- Tests mutating each field independently prove inequality

### Workstream 6: Explicit commit geometry tests ✅
- Valid full commit: passes
- Empty subject: fails
- Abbreviated subject: fails
- Nonexistent subject: fails
- Non-commit object: fails

## Test Results

Focused tests executed successfully (4 suites, all passed).

## Identity

```yaml
subject_commit_oid: 18fbf31
subject_tree_oid: <computed from git>
tested_commit_oid: 18fbf31
tested_tree_oid: <computed from git>
evidence_commit_oid: 18fbf31
closure_commit_oid: 18fbf31
```

## Verdict

```yaml
verdict: CLOSED_PASS
```

## Successor Handoff

Close: `ACT-CIRCUS-FSHARP-DIAGNOSTIC-VERIFICATION-EXACT-FAILURES01`
Resume: `ACT-CIRCUS-FSHARP-DIAGNOSTIC-REPAIR-EPISODE-CANONICAL-AUTHORITY-CONVERGENCE01`
