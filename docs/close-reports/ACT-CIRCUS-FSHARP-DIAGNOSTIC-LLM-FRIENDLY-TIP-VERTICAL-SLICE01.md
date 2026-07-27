# Close Report: ACT-CIRCUS-FSHARP-DIAGNOSTIC-LLM-FRIENDLY-TIP-VERTICAL-SLICE01

## Classification
- **Type**: F# Diagnostic Bounded Git Adapter - Production Repair Episode
- **Status**: CLOSED
- **Correction**: ACT-CIRCUS-FSHARP-DIAGNOSTIC-LLM-FRIENDLY-TIP-VERTICAL-SLICE01-CORRECTION01

## Root Cause

The verification evidence strict parser in `Engine.fs` failed to compile due to an ambiguous type reference. Both `DeclarationIssue` and the new verification evidence parsing error union shared a `MissingField` case, causing F# to fail type inference.

The code used unqualified `MissingField` and `Error` references that resolved to the wrong type depending on context, resulting in compilation errors like:
```
error FS0001: This expression was expected to have type 'VerificationEvidenceParseError' but here has type 'DeclarationIssue'
```

## Resolution

1. **Created new error type** in `Domain.fs`:
   - Added `VerificationEvidenceParseError` discriminated union with all relevant error cases
   - Cases: `UnsupportedSchemaVersion`, `MissingField`, `InvalidEvidenceId`, `PlaceholderEvidenceId`, `UnknownVerificationKind`, `UnknownVerificationStatus`, `InvalidExitCode`, `InvalidCommitOid`, `InvalidTreeOid`, `InvalidSha256`, `ExpectedObject`, `MalformedJson`, `JsonException`

2. **Used fully qualified union case names**:
   - Changed `MissingField(...)` to `VerificationEvidenceParseError.MissingField(...)`
   - Changed `Error(...)` to `Result.Error(...)`
   - Changed `Ok(...)` to `Result.Ok(...)`

3. **Extracted record construction** to a helper function `buildVerificationEvidenceRecord` to reduce parser complexity and eliminate inline record construction issues.

4. **Fixed `loadVerificationEvidenceStrict`** and **`loadVerificationEvidence`** to use fully qualified Result type patterns.

## Changes Made

- `tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/Domain.fs`: +26 lines (new error type)
- `tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/Engine.fs`: +202/-38 lines (parser fixes)

## Verification

- ✅ `dotnet build tools/Circus.Tooling/Circus.Tooling.fsproj -c Release` succeeds
- ✅ `circus-tooling.dll canonical-evidence verify` passes

## Canonical Evidence

- **Commit**: `bf44f89`
- **Tree**: See git object
- **Evidence Path**: `.factory/canonical-evidence.json`

## Notes

The F# type system requires explicit disambiguation when union cases share names across different types. Using fully qualified union case names (`TypeName.CaseName`) is the idiomatic solution when open aliases or type inference cannot resolve ambiguity.
