# Close Report: ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01-REAL-RECORD-PIPELINE01-CORRECTION07-CORRECTION04

## Summary

**ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01-REAL-RECORD-PIPELINE01-CORRECTION07-CORRECTION04** has been successfully implemented.

This correction consolidates compatibility structural equality comparison authority into the production `Validation` module, ensuring single source of truth for all compatibility comparison logic.

## Problem Statement

The canonical evidence provider's staged validation pipeline needed production-grade structural comparison between expected and actual compatibility projections. Previously:

1. Test files contained local comparison helpers that duplicated production logic
2. The `compareCompatibilityProjection` function was not available in production
3. Bijection edge cases (duplicate check IDs, missing/unknown checks) were not consistently detected

## Solution

### Phase 1: Production Comparator Authority

Added to `Validation.fs`:

```fsharp
[<RequireQualifiedAccess>]
type CompatibilityDifference =
    | SchemaVersion of expected: int * actual: int
    | ProviderName of expected: string * actual: string
    // ... all top-level fields
    | CheckCount of expected: int * actual: int
    | MissingCheck of checkId: string
    | UnknownCheck of checkId: string
    | CheckDifference of checkId: string * difference: CompatibilityCheckDifference
    | DuplicateExpectedCheckId of checkId: string * count: int
    | DuplicateActualCheckId of checkId: string * count: int

let compareCompatibilityProjection (expected: CanonicalEvidence) (actual: CanonicalEvidence) : CompatibilityDifference list
```

### Phase 2: Test Migration

- Updated `CompatibilityStructuralEqualityTests.fs` to use production comparator
- Added exact assertion tests for `MissingCheck` and `UnknownCheck` 
- Added `BijectionEdgeCases` test group for duplicate detection
- Removed 137 lines of duplicate test-local helpers

### Phase 3: Staged Validation Wiring

Updated `Publication.fs`:

```fsharp
let private validateCompatibilityEvidence
    (compatPath: string)
    (compatBytes: byte array)
    (expectedProjection: CanonicalEvidence)  // NEW PARAMETER
    (records: CanonicalExecutionEvidence list)
    (aggregate: CanonicalExecutionAggregate)
    : StagedSnapshotFailure list =
    // ...
    let projectionDiffs = compareCompatibilityProjection expectedProjection diskCompat
    for diff in projectionDiffs do
        match diff with
        | CompatibilityDifference.SchemaVersion (expected, actual) -> ...
        // ... all difference cases
```

## Test Results

```
CompatibilityStructuralEquality: 32 passed, 0 failed
Publication: 18 passed, 0 failed
```

## Files Modified

1. `tools/Circus.Tooling/CanonicalEvidence/Validation.fs` - Added comparison types and functions
2. `tests/Circus.Tooling.Tests/CanonicalEvidence/CompatibilityStructuralEqualityTests.fs` - Updated to use production comparator
3. `tools/Circus.Tooling/CanonicalEvidence/Publication.fs` - Wired comparator into staged validation

## Invariant Guarantees

- **Single Authority**: All comparison logic is now in `Validation` module
- **Bijection Enforcement**: Missing checks, unknown checks, and duplicates are all detected
- **Staged Validation**: Production comparator is used in the staged publication pipeline
- **Mutation Detection**: The staged validation with mutation seam detects any corruption

## Evidence

All tests pass, demonstrating:
1. Valid documents compare exactly equal
2. Any structural mutation is detected
3. Bijection edge cases are handled correctly
4. Staged validation correctly rejects corrupted snapshots
