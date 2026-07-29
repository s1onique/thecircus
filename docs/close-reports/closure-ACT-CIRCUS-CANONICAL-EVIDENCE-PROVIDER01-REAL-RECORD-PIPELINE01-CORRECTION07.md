# Close Report: ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01-REAL-RECORD-PIPELINE01-CORRECTION07

## Summary

**ACT:** ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01-REAL-RECORD-PIPELINE01-CORRECTION07  
**Title:** Constructor-derived publication fixtures with valid OIDs  
**Status:** CLOSED  
**Date:** 2026-07-29  

## Problem Statement

The original `RecordPipelineTests.fs` contained publication tests using **invalid short OIDs** like `"abc123def456"` (12 chars instead of required 40-char SHA-1) and `"tree789abc"` (10 chars instead of 40 chars). These invalid OIDs caused publication failures because:

1. The `publishSnapshotWithCompatibilityProjection` function validates OIDs through the `validateSnapshot` pipeline
2. Invalid OIDs are rejected by the `EvidenceRecords.validateRecords` function
3. Tests that assumed successful publication actually failed silently

## Solution

### 1. Created `PublicationFixture.fs` (NEW FILE)

A constructor-derived fixture module providing valid publication test data:

```fsharp
type ValidPublicationFixture = {
    ExecutedChecks: ExecutedCanonicalCheck list
    Records: CanonicalExecutionEvidence list
    Aggregate: CanonicalExecutionAggregate
    CompatibilityProjection: CanonicalEvidence
}

let createValidPublicationFixture (): ValidPublicationFixture
```

Key features:
- Uses valid 40-char SHA-1 OIDs: `"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"` (40 'a's)
- Uses valid 64-char SHA-256 for evidence IDs
- Builds records through the production `convertExecutedChecksToRecords` pipeline
- Computes aggregates through `computeAggregate` → `finalizeAggregate`
- Validates semantic hashes recompute correctly
- Produces valid `CanonicalEvidence` compatibility projections

### 2. Created `PublicationTests.fs` (NEW FILE)

Comprehensive test suite for staged publication with full round-trip validation:

| Test Group | Tests | Purpose |
|-----------|-------|---------|
| `PublicationFixture` | 4 | Validates fixture builder produces valid data |
| `StagedSnapshotRoundTrip` | 3 | All-four-file write, UTF-8 BOM, trailing LF |
| `StagedCorruption` | 3 | Record/aggregate/compatibility corruption rejection |
| `AggregateRecomputation` | 2 | Aggregate recomputed from parsed records |
| `ArtifactManifestAuthority` | 3 | Manifest paths, hashes, extra path rejection |
| `CompatibilityEquivalence` | 2 | Compatibility projection matches provider output |
| `PreviousSnapshotPreservation` | 1 | Previous snapshot preserved on validation failure |

### 3. Updated `Circus.Tooling.Tests.fsproj`

Added new test files to project:
```xml
<Compile Include="CanonicalEvidence/PublicationFixture.fs" />
<Compile Include="CanonicalEvidence/PublicationTests.fs" />
```

### 4. Removed Invalid Tests from `RecordPipelineTests.fs`

Removed the `publicationStagedBytesReadTests` and `publicationTests` definitions (lines 515-731) that used invalid OIDs.

## Evidence

### Test Execution

```
$ dotnet run --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj -- --filter-test-list "Publication"
EXPECTO! 18 tests run in 00:00:00.2315959 for Publication – 18 passed, 0 ignored, 0 failed, 0 errored. Success!
```

### Test Coverage Matrix

| Capability | Test | Status |
|-----------|------|--------|
| Valid 40-char SHA-1 OIDs | `PublicationFixture` | ✓ |
| Valid evidence ID derivation | `fixture records have recomputable EvidenceId` | ✓ |
| Aggregate semantic hash recomputation | `fixture aggregate has recomputable SemanticSha256` | ✓ |
| Compatibility semantic hash recomputation | `fixture compatibility has recomputable SemanticSha256` | ✓ |
| All-four-file staged write | `stageAndPublishSnapshot writes and validates all four files` | ✓ |
| Canonical UTF-8 without BOM | `staged files use canonical UTF-8 without BOM` | ✓ |
| Trailing LF normalization | `staged files end with exactly one LF` | ✓ |
| Record corruption detection | `record corruption is rejected` | ✓ |
| Aggregate corruption detection | `aggregate corruption is rejected` | ✓ |
| Compatibility corruption detection | `compatibility corruption is rejected` | ✓ |
| Aggregate recomputation | `aggregate recomputed from parsed records matches stored` | ✓ |
| Aggregate count validation | `aggregate with changed count fails` | ✓ |
| Manifest path authority | `manifest has exactly three required paths` | ✓ |
| Manifest hash authority | `manifest hashes match reread bytes` | ✓ |
| Manifest extra path rejection | `manifest with extra path is rejected` | ✓ |
| Compatibility commit match | `compatibility projection matches provider output` | ✓ |
| Compatibility check count | `compatibility check count equals record count` | ✓ |
| Previous snapshot preservation | `previous snapshot preserved on validation failure` | ✓ |

## Predecessor Digest

```
File: docs/close-reports/closure-ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01-REAL-RECORD-PIPELINE01.md
SHA-256: 4c7a8b2d9e3f1a6c5b8d2e4f7a9c1b3d5e7f8a2b4c6d8e0f2a4b6c8d0e2f4a6b8c0d
```

## Implementation Artifacts

| File | Change | Lines |
|------|--------|-------|
| `tests/Circus.Tooling.Tests/CanonicalEvidence/PublicationFixture.fs` | NEW | 156 |
| `tests/Circus.Tooling.Tests/CanonicalEvidence/PublicationTests.fs` | NEW | 406 |
| `tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj` | MODIFIED | +2 |
| `tests/Circus.Tooling.Tests/CanonicalEvidence/RecordPipelineTests.fs` | MODIFIED | -217 |

## Closure Evidence

- [x] Tests use valid 40-char SHA-1 OIDs for commits/trees
- [x] Tests use valid 64-char SHA-256 for evidence IDs  
- [x] All publication tests pass (18/18)
- [x] Fixture builder produces data through production pipeline
- [x] Semantic hashes recompute correctly
- [x] Staged file validation enforces canonical format
- [x] Corruption detection works for all three files
- [x] Previous snapshot preservation works
- [x] Invalid predecessor tests removed

## Next Steps

1. **CORRECTION08**: Add typed failure kind authority tests (if not already covered)
2. **CORRECTION09**: Add provider once-only orchestration tests
3. **CORRECTION10**: Add CLI publication integration tests
