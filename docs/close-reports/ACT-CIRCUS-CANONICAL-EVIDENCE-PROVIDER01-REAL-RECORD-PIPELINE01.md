# Close Report: ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01-REAL-RECORD-PIPELINE01

## Summary

Implemented the real per-check execution record pipeline for the canonical evidence provider with CORRECTION02, CORRECTION03, CORRECTION04, and CORRECTION05.

## Corrections

### CORRECTION02 - Core Implementation

1. **Single Failure Hierarchy** - `ProvideFullFailure` type removed. All failures return `ProvideFailure` directly.

2. **Single Execution Pipeline** - `provideWithDependencies` delegates to `provideWithDependenciesFull`.

3. **Per-Check Start Times** - Clock sampled immediately before each `RunCheck` and preserved through `ExecutedCanonicalCheck`.

4. **ExecutedCanonicalCheck Type** - Captures per-check execution context:
```fsharp
type ExecutedCanonicalCheck = {
    Definition: EvidenceCheckDefinition
    Result: EvidenceCheckResult
    StartedAt: DateTimeOffset
}
```

### CORRECTION03 - Timestamp Transport Fixes (Reviewer P0)

1. **Captured Timestamps Preserved** - `buildProviderResult` accepts `ExecutedCanonicalCheck list` directly.

2. **Post-Execution Timestamp Removed** - Shared timestamp eliminated from execution envelope.

3. **Definition/Result Pairing Preserved** - Exact pairs from execution retained.

### CORRECTION04 - Publication and CLI Fixes

1. **Single Compatibility Authority** - `publishSnapshotWithCompatibilityProjection` accepts exact provider-computed `CanonicalEvidence`.

2. **CLI Updated** - Passes `providerResult.CompatibilityProjection` unchanged to publisher.

3. **Single Validation Authority** - Duplicate validation removed from `provideWithDependenciesFull`.

### CORRECTION05 - Staged Bytes Validation Fixes (Reviewer Additional P0)

1. **Staged Bytes Read from Disk** - `publishSnapshotWithCompatibilityProjection` now:
   - Writes staging files
   - **Reads the written bytes from disk**
   - Parses the read bytes
   - Compares parsed document fields

2. **Old API Marked Obsolete** - `publishSnapshot` annotated with `[<Obsolete(..., true)>]` (hard error).

3. **Publication Tests Added** - Three focused tests (fixture issues acknowledged).

### CORRECTION06 - Staging Cleanup and Sole Authority (Reviewer Additional P0)

1. **Staging Directory Cleanup Fixed** - Added `safeDeleteDir` helper that uses `Directory.Delete(path, true)` instead of `File.Delete`:
```fsharp
let private safeDeleteDir (path: string) : unit =
    if Directory.Exists path then Directory.Delete(path, true)
```

2. **Hard Error on Old API** - `publishSnapshot` now has `[<Obsolete(..., true)>]` which raises a compile-time error if called, establishing sole callable publication authority.

### CORRECTION07 - Strict Parser Semantics and Manifest Inventory

1. **ISO 8601 Timestamp Validation** - Added `isValidIso8601Timestamp` helper and `InvalidTimestamp` error type:
   - Validates `started_at` field in evidence wire parsing
   - Rejects malformed timestamps

2. **Manifest Exact Inventory** - Added validation that artifacts.jsonl contains exactly the required paths:
   - Only `records.jsonl`, `aggregate.json`, `canonical-evidence.json` are permitted
   - Reports `UnknownArtifactPath` for extra entries
   - Reports `DuplicateArtifactPath` for duplicates
   - Reports `MissingFile` for missing entries

3. **Type Distinction** - Required field parsers distinguish null from wrong type, reporting `WrongFieldType` errors.

## Files Created

### `tools/Circus.Tooling/CanonicalEvidence/RecordPipeline.fs`
- `RecordValidationIssue` - discriminated union for validation issues
- `RecordPipelineFailure` - discriminated union for pipeline failures
- `validateBijection` - validates definition/result pairing
- `convertCheckResultToRecord` - converts single result to evidence record
- `convertExecutedCheckToRecord` - converts ExecutedCanonicalCheck to record
- `convertExecutedChecksToRecords` - batch conversion with per-check timestamps
- `validateRecords` - validates record integrity
- `buildCompatibilityProjection` - builds canonical evidence document
- `CanonicalEvidenceProviderResult` - full provider result type
- `ExecutedCanonicalCheck` - per-check execution context with StartedAt

### `tests/Circus.Tooling.Tests/CanonicalEvidence/RecordPipelineTests.fs`
- **RecordPipeline tests** (38 individual test cases):
  - validateBijection (7 tests)
  - validateRecords (2 tests)
  - status mapping (3 tests)
  - failure rendering (4 tests)
  - validation rendering (3 tests)
  - ExecutedCanonicalCheck (2 tests)
  - convertExecutedChecksToRecords (10 tests)
  - AggregateDerivation (6 tests)
  - ExecutedCheckStartTime (1 test)
- **Publication tests** (3 test cases):
  - staged bytes read from disk (3 tests)

## Files Modified

- `tools/Circus.Tooling/CanonicalEvidence/EvidenceRecords.fs` - Added FailureKind field
- `tools/Circus.Tooling/CanonicalEvidence/Provider.fs` - Uses ExecutedCanonicalCheck
- `tools/Circus.Tooling/CanonicalEvidence/Publication.fs` - Staged bytes validation + obsolete marker
- `tools/Circus.Tooling/CanonicalEvidence/Cli.fs` - Uses new publication function
- `tools/Circus.Tooling/Circus.Tooling.fsproj` - Added RecordPipeline.fs
- `tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj` - Added test file

## Build Verification

```
dotnet build tools/Circus.Tooling/Circus.Tooling.fsproj -c Release
Build succeeded. 0 Warning(s), 0 Error(s)

dotnet build tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj -c Release
Build succeeded. 0 Warning(s), 0 Error(s)
```

## Test Discovery

```
$ dotnet run --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj --list-tests -c Release --no-build

RecordPipeline.validateBijection.empty definitions returns DefinitionsEmpty
RecordPipeline.validateBijection.empty results returns ResultsEmpty
RecordPipeline.validateBijection.duplicate definition ID returns DuplicateDefinitionId
RecordPipeline.validateBijection.duplicate result ID returns DuplicateResultId
RecordPipeline.validateBijection.missing result returns DefinitionMissingResult
RecordPipeline.validateBijection.missing definition returns ResultMissingDefinition
RecordPipeline.validateBijection.matching pairs returns Ok
RecordPipeline.validateRecords.empty records returns RecordsEmpty issue
RecordPipeline.validateRecords.valid records returns Valid
RecordPipeline.mapStatusToRecordStatus.Pass maps to RecordPass
RecordPipeline.mapStatusToRecordStatus.Fail maps to RecordFail
RecordPipeline.mapStatusToRecordStatus.Unavailable maps to RecordUnavailable
RecordPipeline.recordPipelineFailureToString.DefinitionsEmpty has correct message
RecordPipeline.recordPipelineFailureToString.ResultsEmpty has correct message
RecordPipeline.recordPipelineFailureToString.DuplicateDefinitionId includes ID
RecordPipeline.recordPipelineFailureToString.EmptyCommand includes ID
RecordPipeline.recordValidationIssueToString.RecordsEmpty has correct message
RecordPipeline.recordValidationIssueToString.EvidenceIdEmpty includes ID
RecordPipeline.recordValidationIssueToString.SubjectMismatch includes all details
RecordPipeline.ExecutedCanonicalCheck.can create with definition result and startedAt
RecordPipeline.ExecutedCanonicalCheck.multiple executed checks have different timestamps
RecordPipeline.convertExecutedChecksToRecords.one executed check produces one record
RecordPipeline.convertExecutedChecksToRecords.ten executed checks produce ten records
RecordPipeline.convertExecutedChecksToRecords.record ID is nonempty
RecordPipeline.convertExecutedChecksToRecords.record ID is 64 lowercase hexadecimal characters
RecordPipeline.convertExecutedChecksToRecords.record ID recomputes
RecordPipeline.convertExecutedChecksToRecords.subject commit is preserved
RecordPipeline.convertExecutedChecksToRecords.subject tree is preserved
RecordPipeline.convertExecutedChecksToRecords.working tree clean is preserved
RecordPipeline.convertExecutedChecksToRecords.FailureKind is preserved
RecordPipeline.convertExecutedChecksToRecords.valid records pass validation
RecordPipeline.AggregateDerivation.required pass -> required_failed=0, overall pass
RecordPipeline.AggregateDerivation.required fail -> required_failed=1, overall fail
RecordPipeline.AggregateDerivation.required unavailable -> required_failed=1, overall fail
RecordPipeline.AggregateDerivation.optional unavailable -> required_failed unchanged
RecordPipeline.AggregateDerivation.record IDs are sorted
RecordPipeline.AggregateDerivation.aggregate semantic hash recomputes
RecordPipeline.ExecutedCheckStartTime.each check gets its own start time
Publication.staged bytes read from disk.writes and reads compatibility projection from disk
Publication.staged bytes read from disk.fails when staged compatibility file is corrupted
Publication.staged bytes read from disk.fails when commit mismatch between projection and aggregate
```

**Test Status:**
- Focused tests discovered: 41 (38 RecordPipeline + 3 Publication)
- Focused tests executed and passed: not_proven (publication fixtures acknowledged as needing correction)
- Repository state: dirty (3 untracked files)

**Precise Terminology:**
- `staged_compatibility_bytes_read_from_disk: true`
- `complete_staged_snapshot_round_trip: false` (only canonical-evidence.json is re-read)
- `publication_mode: staged_multi_file_replacement`
- `complete_snapshot_atomicity: deferred`

## Status

**verdict: PARTIAL_CHECKPOINT**

```yaml
implemented:
  real_per_check_records: true
  per_check_timestamps: true
  exact_definition_result_pairing: true
  single_provider_execution: true
  provider_projection_reaches_publisher: true
  staged_compatibility_disk_read: true
  recursive_staging_cleanup_primitive: true
  legacy_publisher_compile_blocked: true

not_proven:
  focused_tests_passed: true
  valid_publication_success_fixture: true
  actual_staged_corruption_rejection: true
  all_staged_files_round_trip: true
  full_prepublication_semantic_validation: true
  committed_subject_binding: true
  fresh_gate: true
```

**CORRECTION02:**
- [x] Single failure hierarchy
- [x] Single execution pipeline
- [x] Per-check start times
- [x] ExecutedCanonicalCheck type
- [x] FailureKind preserved

**CORRECTION03:**
- [x] buildProviderResult accepts ExecutedCanonicalCheck list
- [x] Post-execution shared timestamp removed
- [x] Definition/result pairing preserved

**CORRECTION04:**
- [x] publishSnapshotWithCompatibilityProjection added
- [x] CLI uses new publication function
- [x] Duplicate validation removed

**CORRECTION05:**
- [x] Staged bytes validation reads from disk (not pre-write string)
- [x] Old publishSnapshot marked obsolete
- [x] Publication tests added (3 tests)

**CORRECTION06:**
- [x] Staging directory cleanup uses Directory.Delete(path, true)
- [x] Legacy publisher compile-blocked with IsError=true
- [x] Build succeeds (0 errors, 0 warnings)
- [x] 41 tests discovered (38 RecordPipeline + 3 Publication)

## Known Issues

The Publication tests (3 tests) have fixture issues that need to be addressed:
- Tests create sample CanonicalEvidence but serialization may have schema requirements
- Test fixtures need to provide all required fields for successful round-trip

The 38 RecordPipeline tests build and are discovered correctly.

## Deferred Work (PARTIAL_CHECKPOINT)

- Exact supplied-subject bytes execution
- Immutable snapshot switching
- Strict inventory show
- `FailureKind` wire format binding
- Provider orchestration tests (full integration with injected clock/runner)
- CLI publication tests
- Committed subject proof and fresh gate
- Fix Publication test fixtures for successful round-trip
