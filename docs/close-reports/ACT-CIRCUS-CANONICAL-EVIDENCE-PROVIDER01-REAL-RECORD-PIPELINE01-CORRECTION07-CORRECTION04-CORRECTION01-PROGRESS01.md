# ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01-REAL-RECORD-PIPELINE01-CORRECTION07-CORRECTION04-CORRECTION01

## Progress Report: StagedAggregateMutationTests Complete

### Summary

Successfully completed all required fixes to `StagedAggregateMutationTests.fs`:

1. **Fixed vacuous four-file preservation tests** - Tests now publish a valid snapshot first, then attempt mutation, verifying byte-identical preservation of all four live files

2. **Restored complete derived-field mutation tests** - All 14 derived fields are tested:
   - RecordsTotal, RecordsPassed, RecordsFailed, RecordsUnavailable
   - TestsTotal, TestsPassed, TestsIgnored, TestsFailed, TestsErrored
   - RequiredChecksTotal, RequiredChecksPassed, RequiredChecksFailed
   - RecordIds, OverallStatus

3. **Added decisive staged-record divergence tests** - Tests that mutate `records.jsonl` without updating `aggregate.json`, proving record-derived aggregate authority

4. **Added malformed records isolation tests** - Tests that prove parse failures don't incorrectly claim aggregate authority

5. **Fixed all compile errors**:
   - Removed unused `createDerivedFieldMutationTest` helper that referenced undefined `fixture` variable
   - Removed stale call to deleted helper

### Test Structure

```
StagedAggregateMutation
├── BaselinePublication          (1 test)
├── FourFilePreservation          (2 tests) - Now non-vacuous
├── SchemaVersionMutation        (1 test)
├── SubjectOidMutation           (2 tests)
├── DerivedFieldMutation         (14 tests) - Complete matrix
├── StagedRecordDivergence       (2 tests) - New, decisive
├── MalformedRecordIsolation      (2 tests) - New, isolating
└── SemanticHashSelfIntegrity   (1 test)
```

### Verification

**Build Status**: ✅ SUCCESS
```
dotnet build tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj -c Release
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

**Test Execution**: ✅ ALL PASS (July 30, 2026 10:00 UTC+3)
```
dotnet tests/Circus.Tooling.Tests/bin/Release/net10.0/Circus.Tooling.Tests.dll --filter "StagedAggregateMutation"

EXPECTO! 25 tests run in 00:00:00.5112255 for StagedAggregateMutation – 25 passed, 0 ignored, 0 failed, 0 errored. Success!
```

**CanonicalEvidence Suite**: ✅ 61/61 PASS
```
dotnet tests/Circus.Tooling.Tests/bin/Release/net10.0/Circus.Tooling.Tests.dll --filter "CanonicalEvidence"

EXPECTO! 61 tests run in 00:00:02.6789104 for CanonicalEvidence – 61 passed, 0 ignored, 0 failed, 0 errored. Success!
```

### Test Execution Method

Tests are executed directly using the built DLL:
```bash
dotnet tests/Circus.Tooling.Tests/bin/Release/net10.0/Circus.Tooling.Tests.dll --filter "<TestGroup>"
```

This bypasses `dotnet test` and runs Expecto directly.

### Patch Hygiene

**git diff --check**: ✅ PASS
```
git diff --check b4389cb^..HEAD
(no output - clean)
```

### Files Changed

- `tests/Circus.Tooling.Tests/CanonicalEvidence/StagedAggregateMutationTests.fs` (NEW)
- `tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj` (updated for new test file)
- `tools/Circus.Tooling/CanonicalEvidence/EvidenceRecords.fs` (added parseAggregateWithResult)
- `tools/Circus.Tooling/CanonicalEvidence/Validation.fs` (malformed record isolation fix)
- `tools/Circus.Tooling/Circus.Tooling.fsproj` (updated)
- `tests/Circus.Tooling.Tests/CanonicalEvidence/AggregateStructuralEqualityTests.fs` (sorted record IDs)
- `docs/close-reports/ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01-REAL-RECORD-PIPELINE01-CORRECTION07-CORRECTION04-CORRECTION01-PROGRESS01.md` (this report)

### Commits

- `aa509e7` - fix(CanonicalEvidence): malformed record isolation + sorted record IDs
- `4fa5c68` - chore: remove trailing whitespace from StagedAggregateMutationTests.fs

### ACT Classification

- **Type**: Progress Report / Correction
- **Authority**: ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01-REAL-RECORD-PIPELINE01-CORRECTION07-CORRECTION04-CORRECTION01
- **Status**: Code Complete, Test Execution Verified
