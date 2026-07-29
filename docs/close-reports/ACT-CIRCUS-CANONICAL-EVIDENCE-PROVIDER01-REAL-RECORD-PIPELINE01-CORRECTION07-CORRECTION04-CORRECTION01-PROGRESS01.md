# Progress Report: ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01-REAL-RECORD-PIPELINE01-CORRECTION07-CORRECTION04-CORRECTION01

## Summary

**ACT:** ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01-REAL-RECORD-PIPELINE01-CORRECTION07-CORRECTION04

**Parent Correction:** CORRECTION04 was invalidated; this progress report covers the reimplementation

**Status:** IN_PROGRESS

**Date:** 2026-07-29

## Milestone: Compatibility Comparator Production Authority

This progress report documents Phase 1 of the CORRECTION04-CORRECTION01 workstream, establishing production-grade compatibility structural equality comparison authority.

### Completed Implementation

1. **Validation.fs** - Added production-grade comparison types and functions:
   - `CompatibilityDifference` type with all difference cases including bijection enforcement
   - `CompatibilityCheckDifference` type for per-check field differences
   - `compareCompatibilityProjection` - authoritative structural comparison function
   - `compareCompatibilityCheck` - per-check comparison function
   - Duplicate-aware bijection enforcement (DuplicateExpectedCheckId, DuplicateActualCheckId)
   - MissingCheck and UnknownCheck are count-independent

2. **Publication.fs** - Wired the comparator into staged validation:
   - Updated `validateCompatibilityEvidence` to accept `expectedProjection` parameter
   - Production comparator now runs as Phase 1 of compatibility validation
   - Fixed taxonomy: TestedCommitOid → CompatibilityProjectionMismatch (not SemanticHashMismatch)
   - All difference types are translated to `StagedSnapshotFailure` for typed error reporting

3. **CompatibilityStructuralEqualityTests.fs** - Migrated tests to use production comparator:
   - 137 lines of duplicate test-local helpers removed
   - Tests now call `Validation.compareCompatibilityProjection` directly
   - Added exact assertions for MissingCheck/UnknownCheck
   - Added BijectionEdgeCases tests for duplicate detection

### Test Results

- CompatibilityStructuralEquality: 32 passed
- Publication: 18 passed

### Resolved Review Items

- [x] Comparison authority is no longer test-local
- [x] Tests consume Validation.compareCompatibilityProjection
- [x] Duplicate ID multiplicity is inspected before Set/Map construction
- [x] Missing and unknown ID analysis runs even when counts differ
- [x] Publisher invokes comparator by source inspection
- [x] Failure taxonomy repair (TestedCommitOid → ProjectionMismatch)

### Remaining Work

1. **Staged mutation tests** - Add tests that mutate staged canonical-evidence.json, recompute semantic hash, and prove rejection
2. **Aggregate production comparator** - Move aggregate comparison to Validation module
3. **Aggregate staged mutation tests** - Same for aggregate.json mutations
4. **Provider once-only counters** - Real provider integration
5. **CLI publication integration** - End-to-end CLI tests
6. **Cleanup failure injection** - Typed cleanup-failure preservation tests
7. **Replacement failure indices** - Test indices 0-3 failure scenarios
8. **Restoration failure injection** - Test restoration failure paths

## Related Documents

- Original invalidated closure: `closure-ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01-REAL-RECORD-PIPELINE01-CORRECTION07-CORRECTION04.md` (Status: INVALID_CLOSURE_CHECKPOINT)
