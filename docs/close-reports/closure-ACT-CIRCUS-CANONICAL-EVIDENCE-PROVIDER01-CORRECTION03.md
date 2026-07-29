# Close Report: ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01-CORRECTION03

## Summary

Integrated the `publishSnapshot` atomic publication function into the `runProvide` CLI command, replacing the legacy `writeArtifactWithDependencies` + `File.AppendAllText` pattern. Also fixed a `List.Head` safety issue in `Publication.fs`.

## Changes

### `tools/Circus.Tooling/CanonicalEvidence/Publication.fs`

Fixed `validateSnapshot` to handle empty records list safely:

```fsharp
// Before (unsafe)
let subjectMismatch = snapshot.Aggregate.SubjectCommitOid <> snapshot.Records.Head.TestedCommitOid

// After (safe)
let subjectMismatch =
    match snapshot.Records with
    | [] -> true // Empty records means mismatch
    | head :: _ -> snapshot.Aggregate.SubjectCommitOid <> head.TestedCommitOid
```

### `tools/Circus.Tooling/CanonicalEvidence/Cli.fs`

Updated `runProvide` to use `publishSnapshot` instead of manual file operations:

```fsharp
// Now uses atomic Publication module
let pubOutcome = Circus.Tooling.CanonicalEvidence.Publication.publishSnapshot evidenceRoot records aggregate

if not pubOutcome.Success then
    // Handle publication failure
    stderr.WriteLine(...)
else
    // Verify published bytes
    let verifyOutcome = verifyWithDependencies deps verifyPath repoRoot scopeDeclaration
```

**Removed:**
- `writeArtifactWithDependencies` call
- `File.AppendAllText` for records.jsonl

**Added:**
- `publishSnapshot` call for atomic 4-file snapshot publication
- Proper verification of published bytes

## Verification

- Build: `dotnet build tools/Circus.Tooling/Circus.Tooling.fsproj -c Release` ✓
- Gate: `make gate-fsharp-diagnostics` ✓ (verdict: PASS)
- Gate: `make gate-fsharp-repair-episodes` ✓ (all tests pass)

## Evidence

Commit: `c587bc5` ("feat: Wire runProvide to publishSnapshot, fix List.Head safety")

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

## Next Steps

The provider now uses atomic publication. Remaining items from the original plan:
- Implement detached worktree execution for exact subject
- Update provider result type with Records and Aggregate
- Separate semantic and wire forms for evidence IDs
- Implement immutable version directories for snapshot switch
- Add comprehensive provider tests

These can be addressed in subsequent corrections.
