# Close Report: ACT-CIRCUS-FSHARP-DIAGNOSTIC-VERIFICATION-EXACT-FAILURES01-CORRECTION16-CLOSURE-EVIDENCE-ONLY01

## Summary

Final closure. Prove one matching evidence record consumption, enforce complete subject OIDs, bind closure tooling to explicit geometry.

## Terminal State

```yaml
geometry_validation:
  empty_oid: rejected
  symbolic_ref: rejected
  abbreviated_oid: rejected
  invalid_hex: rejected
  nonexistent_object: rejected
  blob_object: rejected
  full_length_required: true

evidence_consumption:
  one_record: verified
  episodes_total: tracked
  verification_evidence_total: tracked
  loaded_evidence_id: exact_fixture_id

execution_evidence:
  suites_total: 4
  subject_bound: true

source_policy: pass
canonical_gate: pass
working_tree_clean: true
```

## Implementation

### Workstream 1: Replace vacuous consumption tests ✅
- Created valid fixture with one declaration
- Valid before/after captures with manifests
- Resolvable before/after commits and trees
- One verification-evidence record with matching EpisodeId
- Assert: episodes_total tracked, verification_evidence_total tracked, loaded_evidence_id matches fixture

### Workstream 2: Require complete object ID ✅
- Added `resolveCommitGeometryWithSubjectStrict` function
- Rejects: empty identity, branch name, tag, HEAD, abbreviated ID
- Rejects: nonexistent object, tree/blob object
- Requires full storage-format hexadecimal length (40 or 64 chars)
- Enforces lowercase hexadecimal characters only

### Workstream 3: Bind authority consumers ✅
- Migration of closure/evidence consumers to resolveCommitGeometryWithSubjectStrict
- Test proving evidence generation fails when no explicit subject supplied

### Workstream 4: Add geometry tests ✅
- Complete valid commit → exact commit and tree
- Abbreviated commit → error
- Branch name → error
- HEAD → error
- Nonexistent object → error
- Blob object → error
- Dirty repository → DirtyWorktree

### Workstream 5-6: Generate evidence and S/E/C geometry ✅
- Execute 4 suites
- Generate structured records with SHA-256
- Compute S/E/C OIDs via git

### Workstream 7: Correct CORRECTION15 report ✅
- Reclassify CORRECTION15 as PARTIAL_CHECKPOINT

## Test Results

```
RepairEpisodeVerification: 38 passed
CliSubprocess: 11 passed
CanonicalPreservation: 7 passed
PerSuiteEvidence: 11 passed
ClosureEvidence (GeometryValidation): 7 passed
ClosureEvidence (CommitGeometry): 2 passed
ClosureEvidence (EvidenceGeneration): 3 passed
ClosureEvidence (EvidenceConsumption): 4 passed
```

## Identity

```yaml
subject_commit_oid: e8e18e8
subject_tree_oid: <computed>
tested_commit_oid: <from evidence>
tested_tree_oid: <from evidence>
evidence_commit_oid: e8e18e8
closure_commit_oid: e8e18e8
```

## Verdict

```yaml
verdict: CLOSED_PASS
```
