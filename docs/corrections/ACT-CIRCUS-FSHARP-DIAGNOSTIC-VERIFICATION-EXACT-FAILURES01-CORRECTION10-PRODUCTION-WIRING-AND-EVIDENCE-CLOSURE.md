# CORRECTION10 Close Report

## Summary
**Status:** COMPLETE

Final production wiring and evidence closure for ACT-CIRCUS-FSHARP-DIAGNOSTIC-VERIFICATION-EXACT-FAILURES01.

## Workstreams Completed

### Workstream 1: Wire FieldLookup into parseVerificationEvidence ✅
- `parseVerificationEvidenceStrict` now uses `lookupFieldString`, `lookupFieldInt`
- Each field distinguishes: absent → Missing, wrong type → WrongType, invalid → semantic error
- Required fields: verification_evidence_id, episode_id, verification_kind, verification_command, verification_result, verification_exit_code

### Workstream 2: Preserve expected/actual types ✅
- `WrongFieldType` now retains both `expected_type` and `actual_type`
- Type signature: `WrongFieldType of source * lineNumber * fieldName * expectedType * actualType`
- CLI rendering includes both expected and actual types

### Workstream 3: Safe integer validation ✅
- Exit code validation steps:
  1. Check if JsonNumber has fractional part (rejected as WrongType)
  2. Compare to Int32.MinValue and Int32.MaxValue (rejected as WrongType)
  3. Only then convert
  4. Validate exit-code range (< 0 → InvalidExitCode)
- Error tokens: string → WrongFieldType, fractional → WrongType, overflow → WrongType

### Workstream 4: Physical line provenance ✅
- `LocatedVerificationEvidence` type with SourcePath and SourceLine
- SourceLine assigned from original File.ReadAllLines index BEFORE removing blanks
- Preserves exact physical line numbers for error reporting

### Workstream 5: Canonical semantic equality ✅
- `verificationEvidenceSemanticallyEqual` compares all 14 fields:
  - SchemaVersion, EvidenceId, EpisodeId, Kind, Command
  - WorkingDirectory, TestedCommitOid, TestedTreeOid, ExitCode
  - StdoutSha256, StderrSha256, CombinedLogPath, Status

### Workstream 6: Multi-record groups ✅
- Records sorted by source line number
- First record as reference for duplicate detection
- All remaining records compared using semantic equality
- `DuplicateEvidenceId` if all identical
- `ConflictingEvidenceRecord` if any differs

### Workstream 7: Engine completion tests ✅
- Empty evidence: Completed with verification_evidence_total = 0
- One record: Completed with verification_evidence_total = 1, exact ID
- 26 total tests covering all error conditions

### Workstream 8: Subject-bound commit geometry ✅
- `resolveCommitGeometry: string → Result<CommitGeometry, CommitGeometryError>`
- Fail-closed on dirty worktree and unspecified HEAD

### Workstream 9: Per-suite execution evidence ✅
- Structured evidence records from actual runner output
- 48 tests across 4 suites

### Workstream 10: Non-recursive binding ✅
- Complete OID recording in commit geometry

### Workstream 11: Create close reports ✅
- CORRECTION09 (PARTIAL_CHECKPOINT) created
- CORRECTION10 (COMPLETE) created

### Workstream 12: Patch hygiene ✅
- Trailing whitespace removed from PerSuiteEvidenceTests.fs

## Key Types

```fsharp
type FieldLookup<'value> =
    | Missing
    | WrongType of expectedType: string * actualType: string
    | Present of 'value

type LocatedVerificationEvidence = {
    Evidence: VerificationEvidence
    SourcePath: string
    SourceLine: int
}

type VerificationEvidenceParseError =
    | WrongFieldType of source * lineNumber * fieldName * expectedType * actualType
    // ... other variants
```

## Test Results
```
RepairEpisodeVerification: 26 tests PASS
CliSubprocess: 11 tests PASS
CanonicalPreservation: 7 tests PASS
PerSuiteEvidence: 4 tests PASS
make test-tooling-targets: PASS
git diff --check: PASS
```

## Verification Commands
```bash
dotnet build tools/Circus.Tooling/Circus.Tooling.fsproj -c Release
dotnet build tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj -c Release
./tests/RunTests.sh --filter RepairEpisodeVerification
./tests/RunTests.sh --filter CliSubprocess
./tests/RunTests.sh --filter CanonicalPreservation
./tests/RunTests.sh --filter PerSuiteEvidence
make test-tooling-targets
git diff --check
```

---
*Commit: 1c09800a5093e075fad319b6ec9bfe8fc3a0c1b4*
