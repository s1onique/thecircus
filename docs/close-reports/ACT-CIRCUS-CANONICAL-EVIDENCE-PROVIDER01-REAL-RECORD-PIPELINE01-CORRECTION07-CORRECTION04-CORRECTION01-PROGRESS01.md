# Progress Report: ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01-REAL-RECORD-PIPELINE01-CORRECTION07-CORRECTION04-CORRECTION01

## Summary

**ACT:** ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01-REAL-RECORD-PIPELINE01-CORRECTION07-CORRECTION04-CORRECTION01

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

4. **StagedCompatibilityMutationTests.fs** - Added staged mutation tests:
   - Uses domain model to mutate, re-render with correct semantic hash
   - Proves production comparator rejects structurally mutated documents
   - All four live snapshot files verified unchanged after rejection

### Test Results

- CompatibilityStructuralEquality: 32 passed
- StagedCompatibilityMutation: 7 passed (6 new + 1 existing)

### Test Suite Composition

```yaml
staged_compatibility_suite:
  tests_total: 7
  rehashed_structural_mutation_cases: 3
    - RehashedProviderNameMutation
    - RehashedOverallStatusMutation
    - RehashedCommitOidMutation
  parse_failure_cases: 1
    - InvalidJsonMutation
  success_and_preservation_cases: 3
    - ValidSnapshot
    - FourFilePreservation
    - IdempotentOverwrite
```

### Resolved Review Items

- [x] Comparison authority is no longer test-local
- [x] Tests consume Validation.compareCompatibilityProjection
- [x] Duplicate ID multiplicity is inspected before Set/Map construction
- [x] Missing and unknown ID analysis runs even when counts differ
- [x] Publisher invokes comparator by source inspection
- [x] Failure taxonomy repair (TestedCommitOid → ProjectionMismatch)
- [x] Staged mutation tests use domain model with correct semantic hash
- [x] Four-file preservation verified (records.jsonl, aggregate.json, artifacts.jsonl, canonical-evidence.json)
- [x] Commit OID test strengthened to require exact CompatibilityProjectionMismatch
- [x] Report conflict resolved (renamed to progress report)

### Current Status by Category

```yaml
CORRECTION04_CORRECTION01:
  compatibility:
    pure_comparator_authority: CLOSED_PASS
    production_staged_wiring: CLOSED_PASS
    basic_staged_mutation_proof: CLOSED_PASS
    commit_taxonomy_source_fix: PASS
    rehashed_mutation_isolation: CLOSED_PASS
    exact_commit_taxonomy_test: CLOSED_PASS
    all_four_file_preservation: CLOSED_PASS

  aggregate_authority: OPEN
  cleanup_failure_injection: OPEN
  replacement_and_restoration: OPEN
  provider_once_only: OPEN
  CLI_integration: OPEN
  full_combined_execution: OPEN
  inclusive_range_hygiene: OPEN
  fresh_gate: OPEN

  overall_verdict: IN_PROGRESS
```

### Remaining Work

1. **Aggregate production comparator** - Move aggregate comparison to Validation module
2. **Aggregate staged mutation tests** - Same for aggregate.json mutations
3. **Provider once-only counters** - Real provider integration
4. **CLI publication integration** - End-to-end CLI tests
5. **Cleanup failure injection** - Typed cleanup-failure preservation tests
6. **Replacement failure indices** - Test indices 0-3 failure scenarios
7. **Restoration failure injection** - Test restoration failure paths

## Related Documents

- Original invalidated closure: `closure-ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01-REAL-RECORD-PIPELINE01-CORRECTION07-CORRECTION04.md` (Status: INVALID_CLOSURE_CHECKPOINT)
