# Close Report: ACT-CIRCUS-FSHARP-DIAGNOSTIC-VERIFICATION-EXACT-FAILURES01-CORRECTION01

## Summary

Refactored `verifyPipeline` to use typed `EpisodeEngineExecution` discriminated union pattern instead of accessing embedded `EvidenceLoadErrors` field on `EpisodeEngineResult`. This ensures exact verification failures are preserved and properly propagated through the verification pipeline.

## Changes

### Engine.fs

1. **Added `EpisodeEngineFailure` type** (before `EpisodeEngineResult`):
```fsharp
[<RequireQualifiedAccess>]
type EpisodeEngineFailure =
    | VerificationEvidenceLoadFailed of VerificationEvidenceLoadError list
    | DeclarationLoadFailed of DeclarationIssue list
    | PublicationFailed of canonicalByteIdentical: bool * message: string
    | InternalFailure of operation: string * message: string
```

2. **Added `EpisodeEngineExecution` type**:
```fsharp
[<RequireQualifiedAccess>]
type EpisodeEngineExecution =
    | Completed of EpisodeEngineResult
    | Failed of EpisodeEngineFailure
```

3. **Updated `runEpisodeEngine`** to return `EpisodeEngineExecution`:
- Returns `Failed (VerificationEvidenceLoadFailed errors)` on evidence load failure
- Returns `Completed result` on success
- No longer embeds errors in `EpisodeEngineResult`

4. **Added to `VerificationIssue`**:
```fsharp
| VerificationEvidenceLoadFailed of errors: VerificationEvidenceLoadError list
| EpisodeEngineFailed of failure: EpisodeEngineFailure
```

5. **Updated `verifyPipeline`** to pattern match on `EpisodeEngineExecution`:
```fsharp
match runEpisodeEngine repoRoot options with
| Failed (EpisodeEngineFailure.VerificationEvidenceLoadFailed errors) ->
    { Issues = [ VerificationIssue.VerificationEvidenceLoadFailed errors ]
      ... }
| Failed failure ->
    { Issues = [ VerificationIssue.EpisodeEngineFailed failure ]
      ... }
| Completed result ->
    // ordinary corpus verification
```

### Cli.fs

Updated all callers of `runEpisodeEngine` to use pattern matching on `EpisodeEngineExecution`.

## Acceptance Criteria

| Criterion | Status |
|-----------|--------|
| `verifyPipeline` consumes `EpisodeEngineExecution` | ✅ |
| No embedded `EvidenceLoadErrors` on this path | ✅ |
| Exact verification load errors preserved | ✅ |
| Other engine failures preserved separately | ✅ |
| `DeclarationInvalid 0` not used as surrogate | ✅ |
| Build succeeds | ✅ (0 warnings, 0 errors) |
| Historical ACT reports exist | ✅ |
| `git diff --check` passes | ✅ |

## Files Modified

- `tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/Engine.fs` (+146/-136 lines)
- `tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/Cli.fs` (+137/-136 lines)

## Verification

```bash
dotnet build tools/Circus.Tooling/Circus.Tooling.fsproj -c Release
# Build succeeded: 0 Warning(s), 0 Error(s)

git diff --check
# No output (passes)
```

## Notes

- Source-policy gate shows pre-existing violations in shell scripts and Python files that are unrelated to this change
- The behavioral change ensures that when evidence loading fails, callers receive the exact error information rather than a generic `DeclarationInvalid 0` signal
