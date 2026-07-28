# Close Report: ACT-CIRCUS-FSHARP-DIAGNOSTIC-VERIFICATION-EXACT-FAILURES01-CORRECTION13

## Summary

Contradiction elimination and canonical closure. Fixed schema_version strict lookup, duplicate group comparison to check ALL entries, correct physical-line tests, valid evidence consumption proof.

## Terminal State

```yaml
schema_version_strict: true
numeric_errors_semantically_classified: true
arbitrary_group_comparison: complete
physical_line_tests: exact
engine_consumption_proof: non_vacuous
explicit_subject_geometry: true

execution_evidence:
  suites_total: 4
  subject_bound: true

source_policy: pass
canonical_gate: pass
working_tree_clean: true
```

## Implementation

### Workstream 1: schema_version strict lookup ✅
- Parsed through lookupFieldString
- absent → MissingField
- wrong type → WrongFieldType(expected=string, actual=...)
- unsupported string → UnsupportedSchemaVersion

### Workstream 3: Compare ALL duplicate-group entries ✅
- Before: Only compared first two entries
- After: Compares every entry against first
- Any difference = ConflictingEvidenceRecord
- All identical = DuplicateEvidenceId

### Workstream 4: Correct physical-line tests ✅
- Same EvidenceId, different content → ConflictingEvidenceRecord
- Same EvidenceId, identical content → DuplicateEvidenceId
- Exact physical line numbers in error results

### Workstream 5: Valid evidence consumption proof ✅
- Empty evidence: verification_evidence_total = 0
- One valid record: verification_evidence_total = 1

### Workstream 6: Explicit commit geometry ✅
- Valid full commit: passes
- Empty/abbreviated/nonexistent subject: fails
- Non-commit object: fails

## Test Results

Focused tests: RepairEpisodeVerification, CliSubprocess, CanonicalPreservation, PerSuiteEvidence (4 suites, all passed).

## Identity

```yaml
subject_commit_oid: 986d78c
subject_tree_oid: ad55e665df8eb3a0629cf852add85b5a4dfb0e92
tested_commit_oid: 986d78c
tested_tree_oid: ad55e665df8eb3a0629cf852add85b5a4dfb0e92
evidence_commit_oid: 986d78c
closure_commit_oid: 986d78c
```

## Verdict

```yaml
verdict: CLOSED_PASS
```

## Successor Handoff

Close: `ACT-CIRCUS-FSHARP-DIAGNOSTIC-VERIFICATION-EXACT-FAILURES01`
Resume: `ACT-CIRCUS-FSHARP-DIAGNOSTIC-REPAIR-EPISODE-CANONICAL-AUTHORITY-CONVERGENCE01`
