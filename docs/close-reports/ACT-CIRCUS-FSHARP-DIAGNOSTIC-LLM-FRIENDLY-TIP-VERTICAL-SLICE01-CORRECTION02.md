# ACT-CIRCUS-FSHARP-DIAGNOSTIC-LLM-FRIENDLY-TIP-VERTICAL-SLICE01-CORRECTION02

## Verdict

**PARTIAL**

## Correction01 Verdict Repair

The CORRECTION01 close report verdict has been corrected from `CLOSED` to `PARTIAL_CHECKPOINT`.

## Exact Identities

| Identity | Value |
|----------|-------|
| `baseline_commit_oid` | `559ae380e69682e12712d3f6e940da13ad40f940` |
| `baseline_tree_oid` | `10e687db8d2dfe25fdb538efe0b393d0a3345014` |
| `subject_commit_oid` | `901fb72` |
| `subject_tree_oid` | `2cf1c11e8e6f3c9c950affa87706361c9601755b` |
| `tested_commit_oid` | `901fb72` |
| `tested_tree_oid` | `2cf1c11e8e6f3c9c950affa87706361c9601755b` |
| `before_commit_oid` | `be84cb3cb0b540fa0c895afd7f7c6a41c01c81c6` |
| `before_tree_oid` | `111de4f330d2076f2b7e96d683a3f4b142c3bee4` |
| `after_commit_oid` | `c79f0ecfff6b7e4c34ae469ea55a4a4b60adca91` |
| `after_tree_oid` | `2cf1c11e8e6f3c9c950affa87706361c9601755b` |

## Baseline Inventory

The baseline (CORRECTION01) state:
- `episodes_qualified: 0`
- `episodes_ambiguous: 1`
- `transitions_total: 0`
- `verification_evidence_total: 0`
- `raw_diagnostic_lines: 16`
- `distinct_exact_diagnostics: Not computed`

## Occurrence Loading Implementation

Extended `Episodes.fs` to load occurrence artifacts from capture directories:

```fsharp
let private loadCaptureOccurrences
    (repoRoot: string)
    (captureId: string)
    (manifest: CaptureManifest)
    : DiagnosticOccurrence list =
    // Reads *-occurrences.jsonl files from capture directory
```

Moved `OccurrenceReader.fs` before `Episodes.fs` in project file to resolve compile order dependency.

## Lossless Normalization Proof

Fixed `Normalization.fs` to preserve backslashes in diagnostic messages.

**Before:**
```fsharp
let private normalizePathSeparators (text: string) : string =
    if text.Contains("\\") then
        text.Replace('\\', '/')  // Incorrect: changes '\' to '/'
    else
        text
```

**After:**
```fsharp
let private normalizePathSeparators (text: string) : string =
    if not (text.Contains("\\")) then text
    elif not (text.Contains("/")) then text
    else
        // Only normalize segments that contain both \ and /
        // Backslash-only text (e.g., "Unexpected character '\'") preserved
```

## Raw Replay Classification

The legacy build log emits each diagnostic twice:
1. Once during project compilation
2. Once in the final build-failure summary

The normalized corpus preserves both:
- `raw_occurrence_count: 16` (all emitted lines)
- `semantic_diagnostic_count: 8` (deduplicated by fingerprint)
- `replayed_occurrence_count: 8` (duplicate emissions)

## Semantic Deduplication Proof

Each unique diagnostic (by fingerprint) appears exactly twice in the raw log:
```
FS0603 (SurfaceInventory.fs:191) - 2 raw occurrences → 1 semantic diagnostic
FS1156 (SurfaceInventory.fs:191) - 2 raw occurrences → 1 semantic diagnostic
FS0010 (SurfaceInventory.fs:191) - 2 raw occurrences → 1 semantic diagnostic
FS0010 (GitHubRules.fs:395) - 2 raw occurrences → 1 semantic diagnostic
FS3118 (GitHubRules.fs:391) - 2 raw occurrences → 1 semantic diagnostic
FS0010 (GitHubRules.fs:392) - 2 raw occurrences → 1 semantic diagnostic
FS0010 (GitHubRules.fs:393) - 2 raw occurrences → 1 semantic diagnostic
FS0010 (Types.fs:95) - 2 raw occurrences → 1 semantic diagnostic
```

**Total: 16 raw → 8 semantic**

## Exact Fingerprints

| Fingerprint | Code | Source Path | Transition |
|-------------|------|------------|------------|
| `091d385f...` | FS0603 | SurfaceInventory.fs:191 | eliminated_after |
| `0e3595e3...` | FS0010 | SurfaceInventory.fs:191 | eliminated_after |
| `68314247...` | FS0010 | GitHubRules.fs:395 | eliminated_after |
| `70374f19...` | FS3118 | GitHubRules.fs:391 | eliminated_after |
| `ace804f8...` | FS0010 | GitHubRules.fs:392 | eliminated_after |
| `b8134697...` | FS0010 | Types.fs:95 | eliminated_after |
| `bb5b04ea...` | FS0010 | GitHubRules.fs:393 | eliminated_after |
| `ec4cc986...` | FS1156 | SurfaceInventory.fs:191 | eliminated_after |

**Unique exact fingerprint count: 8**

## Before/After Diagnostic Sets

| Metric | Value |
|--------|-------|
| `before_semantic_diagnostics` | 8 |
| `after_semantic_diagnostics` | 0 |
| `before_raw_occurrences` | 16 |
| `after_raw_occurrences` | 0 |

## Transition Computation

The `runEpisodeEngine` now calls `Transitions.buildTransitions` to compute transitions.

**Before state:** Engine created empty transitions list, never populated it.

**After state:** Engine computes transitions using occurrences from captures.

## Verification Evidence

**Status:** Missing

The declaration contains `["canonical-gate-placeholder"]` which is not a real evidence ID.

The episode still has `verification_level: transition_observed` because no verification evidence was created.

## Episode Qualification

| Metric | Value |
|--------|-------|
| `episodes_qualified` | 0 |
| `episodes_ambiguous` | 1 |
| `episodes_rejected` | 0 |
| `change_sets_total` | 1 |

**Qualification reason:** `verification level is transition_observed`

The episode is `ambiguous` because it lacks real verification evidence.

## Corpus Consistency

The normalized corpus has been regenerated:

```
factory/evidence/fsharp-diagnostics/corpus/normalized/
  repair-episodes-v1.jsonl        ✓ Contains episode
  diagnostic-transitions-v1.jsonl ✓ Contains 8 transitions
  repair-episode-summary-v1.json  ✓ Updated counts
```

## Determinism

Regeneration produces identical output when run multiple times with same inputs.

## Portability

Normalized outputs use `<REPO>` alias instead of absolute paths.

## Manifest Parser Regression Tests

The `Manifest.fs` whitespace fix (added `skipWs` call) is implemented but tests are deferred to CORRECTION03.

## Focused Tests

Parser regression tests, occurrence loading tests, and transition computation tests are deferred to CORRECTION03.

## Source Policy

Build succeeds:
```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

## Canonical Gate

Not executed. The `episodes_qualified` is still 0.

## Changed Paths

| Path | Change |
|------|--------|
| `Normalization.fs` | Fixed path separator normalization |
| `Episodes.fs` | Added occurrence loading |
| `Engine.fs` | Added transition computation call |
| `Circus.Tooling.fsproj` | Reordered file compilation |
| `diagnostic-transitions-v1.jsonl` | Regenerated with 8 transitions |
| `repair-episode-summary-v1.json` | Updated counts |

## Generated Artifact Hashes

Artifacts regenerated by `repair-episodes regenerate`.

## Final Cleanliness

| Check | Result |
|-------|--------|
| `working_tree_clean` | `true` (after commit) |
| `episodes_qualified` | 0 |
| `transitions_total` | 8 |

## Vertical-Slice Handoff

The vertical slice is closer to completion:
- ✅ Production episode declaration exists
- ✅ Occurrences are loaded from captures
- ✅ Transitions are computed (8 semantic diagnostics eliminated)
- ✅ Backslash normalization is fixed
- ❌ Verification evidence is missing
- ❌ Episode remains `ambiguous`

## Summary

This correction enables transition computation in the repair episode engine:

1. **Fixed normalization** - Backslashes in diagnostic messages are preserved
2. **Extended capture loading** - Occurrences read from capture directories
3. **Enabled transition engine** - `runEpisodeEngine` now calls `buildTransitions`
4. **Computed 8 transitions** - From 16 raw occurrences deduplicated to 8 semantic diagnostics

The episode remains `ambiguous` because verification evidence is not yet created. The next correction must create real verification evidence to achieve `episodes_qualified: 1`.
