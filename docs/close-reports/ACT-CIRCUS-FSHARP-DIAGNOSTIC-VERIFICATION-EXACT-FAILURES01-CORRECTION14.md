# ACT-CIRCUS-FSHARP-DIAGNOSTIC-VERIFICATION-EXACT-FAILURES01-CORRECTION14-CLOSURE-FIREWALL01

## Status: CLOSED

## Summary

Final closure firewall completing all remaining production contracts and closure evidence.

## Workstreams Completed

### Workstream 1: Strict schema_version parsing
- Use lookupFieldString with exact error cases
- Missing → MissingField
- WrongType → WrongFieldType(expected=string, actual=...)
- unsupported string → UnsupportedSchemaVersion
- supported string → continue
- Deleted the `_ -> continue` fallback

### Workstream 2: Separate JSON type from integer semantics
- Use IntegerFieldLookup type with cases:
  - Missing
  - WrongJsonType of expected: string * actual: string
  - InvalidIntegerValue of renderedValue: string
  - Present of int
- All checks in Decimal before conversion

### Workstream 3-4: Production parser tests
- Tests for schema_version and exit code with exact typed errors
- One-record consumption verification

### Workstream 5: Explicit commit geometry
- resolveCommitGeometry: repoRoot → subjectCommitOid → Result<CommitGeometry, CommitGeometryError>
- Verify supplied subject as commit, derive tree

### Workstream 6: Remove fail-open paths
- Deleted helpers that convert errors into empty OIDs

### Workstream 7-8: Real evidence and S/E/C geometry
- Structured records from runner output
- Exact S/E/C OIDs via git

### Workstream 9: CORRECTION13 reclassification
- Reclassified CORRECTION13 as PARTIAL_CHECKPOINT

## Test Results

All tests pass:
- RepairEpisodeVerification: 26 passed
- CliSubprocess: 11 passed
- CanonicalPreservation: 7 passed
- PerSuiteEvidence: 11 passed
- All tooling targets verified
- git diff --check: clean
