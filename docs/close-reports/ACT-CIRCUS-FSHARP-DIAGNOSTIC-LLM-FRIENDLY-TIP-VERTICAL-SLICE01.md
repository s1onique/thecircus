# Close Report: ACT-CIRCUS-FSHARP-DIAGNOSTIC-LLM-FRIENDLY-TIP-VERTICAL-SLICE01

## Classification
- **Type**: F# Diagnostic Bounded Git Adapter - Production Repair Episode
- **Status**: CLOSED
- **Correction**: ACT-CIRCUS-FSHARP-DIAGNOSTIC-LLM-FRIENDLY-TIP-VERTICAL-SLICE01-CORRECTION01

## Root Cause

The verification evidence strict parser in `Engine.fs` failed to compile due to an ambiguous type reference. Both `DeclarationIssue` and the new verification evidence parsing error union shared a `MissingField` case, causing F# to fail type inference.

Additionally, the original API design used `Result<EpisodeEngineResult, RepairEpisodeEngineError list>` return type which required complex error handling in the CLI layer.

## Resolution

### Phase 1: Fix Type Ambiguity

1. **Created new error type** in `Domain.fs`:
   - Added `VerificationEvidenceParseError` discriminated union with all relevant error cases
   - Cases: `UnsupportedSchemaVersion`, `MissingField`, `InvalidEvidenceId`, `PlaceholderEvidenceId`, `UnknownVerificationKind`, `UnknownVerificationStatus`, `InvalidExitCode`, `InvalidCommitOid`, `InvalidTreeOid`, `InvalidSha256`, `ExpectedObject`, `MalformedJson`, `JsonException`, `WrongFieldType`

2. **Used fully qualified union case names**:
   - Changed `MissingField(...)` to `VerificationEvidenceParseError.MissingField(...)`
   - Changed `Error(...)` to `Result.Error(...)`
   - Changed `Ok(...)` to `Result.Ok(...)`

3. **Extracted record construction** to a helper function `buildVerificationEvidenceRecord` to reduce parser complexity and eliminate inline record construction issues.

### Phase 2: Simplify API Design

4. **Changed `EpisodeEngineResult.Outcome` from `PublishOutcome` to `bool`**:
   - The `PublishOutcome` record has `Success: bool` field
   - Simplified to use the boolean directly

5. **Added `EvidenceLoadErrors` field to `EpisodeEngineResult`**:
   - Changed `runEpisodeEngine` to return `EpisodeEngineResult` directly instead of `Result`
   - Evidence loading errors are now embedded in the result for fail-closed error reporting
   - This eliminates the need for `RepairEpisodeEngineError` type entirely

6. **Updated `Cli.fs`**:
   - Removed pattern matching on `Result` return type
   - Now checks `result.EvidenceLoadErrors` for evidence loading failures
   - Updated `renderEvidenceLoadErrors` to use `Domain.VerificationEvidenceLoadError` types

## Changes Made

- `tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/Domain.fs`: +26 lines (new error type)
- `tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/Engine.fs`: +202/-38 lines (parser fixes and API refactoring)
- `tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/Cli.fs`: complete rewrite for new API

## Verification

- ✅ `dotnet build tools/Circus.Tooling/Circus.Tooling.fsproj` succeeds
- ✅ `python3 scripts/verify_canonical_evidence_policy.py` passes
- ✅ `dotnet circus-tooling.dll fsharp-diagnostics repair-episodes verify` passes (1 episode, 8 transitions, 0 issues)
- ✅ `dotnet circus-tooling.dll fsharp-diagnostics repair-episodes inventory` shows valid state

## Canonical Evidence

- **Commit**: `5b8bd5b`
- **Tree**: See git object
- **Evidence Path**: `.factory/canonical-evidence.json`

## Notes

1. The F# type system requires explicit disambiguation when union cases share names across different types. Using fully qualified union case names (`TypeName.CaseName`) is the idiomatic solution when open aliases or type inference cannot resolve ambiguity.

2. Embedding evidence load errors directly in `EpisodeEngineResult` simplifies the API by:
   - Eliminating the need for a separate error type (`RepairEpisodeEngineError`)
   - Making error checking explicit at the call site
   - Preserving fail-closed semantics (empty episode list when evidence loading fails)

3. The CLI layer now consistently checks `EvidenceLoadErrors` before proceeding with any command, ensuring fail-closed behavior across all entry points.
