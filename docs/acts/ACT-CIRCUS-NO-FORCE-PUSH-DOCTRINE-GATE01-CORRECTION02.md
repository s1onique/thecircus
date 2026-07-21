# ACT: ACT-CIRCUS-NO-FORCE-PUSH-DOCTRINE-GATE01-CORRECTION02

## Status

**FAIL** — The correction introduces intended architecture and broader test declarations, but it does not compile, its GitHub ruleset decoder does not match the API, several tests are vacuous or incorrectly constructed, canonical gate evidence remains stale, and the work was recorded under the wrong ACT identity.

## Parent ACT

```
ACT-CIRCUS-NO-FORCE-PUSH-DOCTRINE-GATE01
verdict: PARTIAL
```

CORRECTION01 was appended to the wrong close report. This correction is standalone.

## Summary

CORRECTION02 records and must resolve the following deterministic blockers identified in CORRECTION01:

1. **Compiler errors**: Multiple type and API mismatches prevent compilation
2. **GitHub API model incompatibility**: Schema does not match documented API
3. **Object-format detection**: Wrong command for detecting SHA-256 repositories
4. **OID width validation**: Permissive fallback to SHA-1 still present
5. **Mutation registry surface coverage**: Many mutations bypass production paths
6. **Real repository tests**: Vacuous assertions, incorrect divergent-history test
7. **Static policy coverage**: Narrow regexes miss several mutation forms
8. **Gate wiring**: Makefile incomplete, stale gate-summary.json
9. **ACT identity**: Wrong close report, needs standalone ACT

## P0-1: Compile Integrity Blockers

### Type Mismatches

| Location | Expected | Actual | Fix Required |
|----------|----------|--------|--------------|
| `MutationCase.ApplyMutation` | `Result<unit, string>` | `Result<string, string>` | Change return type |
| `VerifyFile` return | `Diagnostic list` | `DiagnosticId list>` | Import and use `Diagnostic` type |
| `PrePush.fs` OperationalFailure | requires `PrePushRefUpdate` | `""` | Construct valid `PrePushRefUpdate` |

### Function Signature Mismatches

| Location | Expected | Actual | Fix Required |
|----------|----------|--------|--------------|
| `extractCommandsFromContent` | `(parserKind, content, filePath)` | only two args in callers | Add `parserKind` argument to all callers |
| `StaticPolicy` callers | pass three args | pass two args | Update all callers |
| `ParsedCommand.Line/Column` | present | removed in `StaticPolicy` | Add back or update `StaticPolicy` |
| `validatePaths` | requires tracked files | two-arg form | Update caller to pass tracked files |

### Missing Imports

| Location | Issue | Fix Required |
|----------|-------|--------------|
| `Rendering.fs` | `JsonPropertyName`, `JsonIgnoreCondition` without import | Add `open System.Text.Json.Serialization` |

### Invalid API Usage

| Location | Issue | Fix Required |
|----------|-------|--------------|
| `GitHubRules.fs` | `ProcessStartInfo.Timeout` does not exist | Use `Process.WaitForExit(int)` or async pattern |

### Undefined References

| Location | Expected | Actual | Fix Required |
|----------|----------|--------|--------------|
| `GitHubRules.fs` | `RulesNotFound` | only `RulesetNotFound` defined | Define `RulesNotFound` or rename usages |

### F# List/Array Mismatches

| Location | Issue | Fix Required |
|----------|-------|--------------|
| `GhRulesetResponse.Rulesets` | F# list | processed with `Array.filter` | Use `List.filter` or change to array |
| `parseBypassActors` | `List.ofArray` on F# list | deserialize to F# list then `List.ofArray` | Deserialize directly to array or use list throughout |

## P0-2: GitHub API Model Incompatibility

### API Response Schema

**Current expectation:**
```json
{"rulesets": [...]}
```

**Actual GitHub API (`GET /repos/{owner}/{repo}/rulesets`):**
```json
[  // Top-level array, not object with "rulesets" key
  { "id": 1, "name": "...", "enforcement": "active", ... },
  ...
]
```

### Enforcement Values

**Current implementation searches for:** `"enabled"`

**GitHub documented values:** `"active"`, `"disabled"`, `"evaluate"`

### Rule Types

**Current implementation:** `delete_ref`

**GitHub documented:** `deletion`

### Deletion Rule Type

**Current implementation:** `non_fast_forward`

**Correct:** Already matches GitHub docs, but confirm `non_fast_forward` is the correct rule name for force-push blocking

### Bypass Endpoint

**Current implementation:** `/repos/{owner}/{repo}/rulesets/{id}/bypass`

**GitHub documented:** Bypass actors are in the detailed ruleset response, not a separate endpoint. Also requires write access to view.

### Organization Rulesets

**Current behavior:** Filter with `rs.Source = "repository"`

**Correct behavior:** "rules for a branch" endpoint returns ALL applicable rules including organization rules. Do not filter by source.

## P0-3: Object Format Detection

**Current implementation:**
```fsharp
git config core.repositoryformatversion
```

**Correct command:**
```fsharp
git rev-parse --show-object-format=storage
```

`core.repositoryformatversion` returns the repository format version (0, 1, etc.), NOT the object hash algorithm.

## P0-4: OID Width Validation

**Current permissive logic:**
```fsharp
actualWidths.Head <> expected && actualWidths.Head <> 40
```

**Correct behavior:** Enforce exact width with no SHA-1 fallback when SHA-256 is detected.

**`isNullOid`:** Should only accept exactly 40 or exactly 64 zeros, not "at least 4 zeros".

## P0-5: Mutation Registry Surface Coverage

### Current Problem

`executeCase` always verifies `script.sh`, but many mutations create other files:
- `factory/no-force-push-surfaces.csv`
- `factory/inventory.csv`
- `action.yml`
- `Dockerfile`
- `Makefile`
- `publish.sh`

### Required Fix

Each mutation case must:
1. Declare its target surface path
2. Apply mutation to the correct file
3. Verify the correct file
4. Update inventory to include all surfaces

### Missing Mutation Proofs

- NFP-011: Unclassified surface (requires inventory to NOT include `publish.sh`)
- NFP-012: Doctrine drift (requires factory CSV)
- NFP-013: Malformed inventory (requires factory CSV)
- YAML/Dockerfile/Makefile extraction: Not tested through their declared parser kinds

## P0-6: Real Repository Tests

### Divergent History Test

**Current issue:** Passes `firstOid` as `RemoteOid`, but `firstOid` is still an ancestor of the divergent commit.

**Required fix:** Obtain the actual remote tip after the remote has been updated.

### Permissive Assertions

**Current:**
```fsharp
| OperationalFailure _ -> ()
| _ -> ()
```

**Required:** Remove permissive fallthrough. Each test must assert exactly one outcome.

### Ignored Exit Codes

Git init, commit, clone, push operations mostly ignore exit codes.

**Required:** Add error handling for every git operation.

### Branch Name Assumption

**Current:** Assumes `main` branch without explicitly creating it.

**Required:** Explicitly create the intended branch.

## P0-7: Static Policy Coverage

### Narrow Regexes

**Force options:** Match only `-f` and `-uf`, not `-af`, `-bf`, etc.

**Deletion options:** Match only `-d`, not `-fd`, `-ad`, etc.

**Leading-plus:** Require `:refs/`, miss `+main`

**Empty-source deletion:** Require `:refs/`, miss `:feature`

### Missing Patterns

**Eval patterns:** Cover `sh -c` but not `bash -c`, `zsh -c`, etc.

**Verify surface:** Still analyzes `RawSource` rather than parsed executable/arguments.

## P0-8: Gate Wiring

### Makefile Issues

**Current:**
```make
publication-gate: build-source-policy
	$(CIRCUS_TOOLING) no-force-push verify
	$(MAKE) verify-github-no-force-push
```

**Required:** `publication-gate` should depend on the complete canonical `gate`.

### Stale Gate Summary

`.factory/gate-summary.json` still shows 4 checks, not the claimed 6.

**Required:** Regenerate after fixing all compilation errors.

## P0-9: ACT Identity

### Wrong Close Report

CORRECTION01 was appended to `ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-VERIFIER-INTEGRITY-CONVERGENCE01-CORRECTION01.md`, which already had an unconditional pass.

### Required Action

This CORRECTION02 is the correct ACT. No evidence should be appended to the already-closed ML-source-policy report.

## Required Fixes

1. [ ] Fix all compiler errors before any completion claim
2. [ ] Replace GitHub ruleset transport with correct API endpoints:
   - `GET /repos/{owner}/{repo}/rules/branches/{branch}` for effective rules
   - `GET /repos/{owner}/{repo}/rulesets/{id}` for detail
3. [ ] Use `git rev-parse --show-object-format=storage`
4. [ ] Enforce exact OID widths with no fallback
5. [ ] Rewrite mutations to declare and verify actual surfaces
6. [ ] Add error handling for all git operations
7. [ ] Fix divergent-history test to use actual remote tip
8. [ ] Remove all permissive `_ -> ()` assertions
9. [ ] Make `publication-gate` depend on `gate`
10. [ ] Regenerate and verify six-check gate summary
11. [ ] Keep no-force-push evidence in its own ACT
12. [ ] Keep parent ACT PARTIAL until all checks pass

## Next Steps

1. Create standalone close report for this ACT
2. Prioritize compilation errors
3. Test incrementally
4. Do not claim completion until `dotnet build` succeeds
