# Progress Report: ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01-REAL-RECORD-PIPELINE01-CORRECTION07-CORRECTION04-CORRECTION01

## Summary

**ACT:** ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01-REAL-RECORD-PIPELINE01-CORRECTION07-CORRECTION04-CORRECTION01

**Parent Correction:** CORRECTION04 was invalidated; this progress report covers the reimplementation

**Status:** IN_PROGRESS

**Date:** 2026-07-30

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
   - **Fixed taxonomy**: Added `CompatibilityCommitOidMismatch` and `CompatibilityTreeOidMismatch` types
   - Commit/tree OID mismatches no longer misclassified as `CompatibilitySemanticHashMismatch`
   - All difference types are translated to `StagedSnapshotFailure` for typed error reporting

3. **CompatibilityStructuralEqualityTests.fs** - Migrated tests to use production comparator:
   - 137 lines of duplicate test-local helpers removed
   - Tests now call `Validation.compareCompatibilityProjection` directly
   - Added exact assertions for MissingCheck/UnknownCheck
   - Added BijectionEdgeCases tests for duplicate detection

4. **StagedCompatibilityMutationTests.fs** - Complete staged mutation tests:
   - Uses domain model to mutate, re-render with correct semantic hash
   - Uses `WriteAllBytes` with `strictUtf8` for canonical byte writing
   - Proves production comparator rejects structurally mutated documents
   - All four live snapshot files verified byte-identical after rejection
   - **Fixed commit OID taxonomy**: Now expects `CompatibilityCommitOidMismatch`
   - **Added tree-OID test**: Symmetric test for `CompatibilityTreeOidMismatch`
   - **Fixed exclusion proofs**: Uses exact Set comparison instead of shape heuristics

### Test Execution Results

Executed via:
```bash
./tests/Circus.Tooling.Tests/bin/Release/net10.0/Circus.Tooling.Tests --filter "<TestName>"
echo "EXIT_CODE=$?"
```

**CompatibilityStructuralEquality (32 tests):**
```yaml
total: 32
passed: 32
failed: 0
errored: 0
exit_code: 0
```

**StagedCompatibilityMutation (12 tests):**
```yaml
total: 12
passed: 12
failed: 0
errored: 0
exit_code: 0
```

### Test Suite Composition

```yaml
staged_compatibility_suite:
  tests_total: 12
  rehashed_top_level_mutation_cases: 4
    - RehashedProviderNameMutation: PASS
    - RehashedOverallStatusMutation: PASS
    - RehashedCommitOidMutation: PASS (uses CompatibilityCommitOidMismatch)
    - RehashedTreeOidMutation: PASS (uses CompatibilityTreeOidMismatch)
  rehashed_per_check_mutation_cases: 1
    - RehashedCheckFailureKindMutation: PASS (exact check ID binding)
  rehashed_bijection_mutation_cases: 3
    - RehashedRemovedCheckMutation: PASS
    - RehashedUnknownCheckMutation: PASS
    - RehashedDuplicateCheckIdMutation: PASS
  parse_failure_cases: 1
    - InvalidJsonMutation: PASS
  success_and_preservation_cases: 3
    - ValidSnapshot: PASS
    - FourFilePreservation: PASS
    - IdempotentOverwrite: PASS
```

### Current Status by Category

```yaml
CORRECTION04_CORRECTION01:
  compatibility:
    implementation_authority: PASS
    comparator_tests: 32/32_PASS_REPORTED
    staged_tests: 12/12_PASS_REPORTED
    commit_oid_taxonomy: PASS
    tree_oid_taxonomy: PASS
    exact_commit_oid_exclusion: PASS (exact Set comparison)
    exact_tree_oid_exclusion: PASS (exact Set comparison)
    FailureKind_exact_identity: PASS (target.Id binding)
    runner_exit_integrity: PASS (exit code 0 on pass)
    
    compatibility_subscope_verdict: CLOSED_PASS

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

### Runner Exit Code Integrity

The test binary properly propagates Expecto's exit code:
- Exit code 0 when all tests pass
- Exit code 1 when any test fails (verified via `echo "EXIT_CODE=$?"`)

The Program.fs entry point correctly returns `runTestsInAssemblyWithCLIArgs` which returns 0/1 based on test results.

### Known Issues Outside Subscope

None in the compatibility subscope.

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
