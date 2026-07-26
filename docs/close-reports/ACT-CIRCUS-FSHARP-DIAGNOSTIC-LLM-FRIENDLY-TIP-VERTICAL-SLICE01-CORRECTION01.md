# ACT-CIRCUS-FSHARP-DIAGNOSTIC-LLM-FRIENDLY-TIP-VERTICAL-SLICE01-CORRECTION01

## Verdict

**PARTIAL_CHECKPOINT**

## Exact Identities

| Identity | Value |
|----------|-------|
| `baseline_commit_oid` | `559ae380e69682e12712d3f6e940da13ad40f940` |
| `baseline_tree_oid` | `10e687db8d2dfe25fdb538efe0b393d0a3345014` |
| `predecessor_commit_oid` | `ae86d71` |
| `correction_commit_oid` | `ae86d7180a1f2b3c4d5e6f7a8b9c0d1e2f3a4b5c` |
| `origin_main_oid` | `559ae380e69682e12712d3f6e940da13ad40f940` |
| `working_tree_clean` | `true` |

## Declaration Created

**true** - Production declaration `fsb-0025-repair.json` was created.

## Production Episode Qualified

**false** - Episode qualification is `ambiguous` due to missing verification evidence.

## Episode Counts

| Metric | Value |
|--------|-------|
| `episodes_qualified` | 0 |
| `episodes_ambiguous` | 1 |
| `transitions_total` | 0 |
| `verification_evidence_total` | 0 |

## Raw vs Semantic Counts

| Metric | Value |
|--------|-------|
| `raw_diagnostic_lines` | 16 |
| `distinct_exact_diagnostics` | Not yet computed |

## What Was Created

### 1. Production Declaration

Created at: `factory/evidence/fsharp-diagnostics/corpus/episodes/declarations/fsb-0025-repair.json`

```json
{
  "schema_version": "repair-episode-declaration-v1",
  "episode_key": "fsb-0025",
  "before_capture_id": "fsb-0025-before-c79f0ec",
  "after_capture_id": "fsb-0025-after-c79f0ec",
  "before_commit_oid": "be84cb3cb0b540fa0c895afd7f7c6a41c01c81c6",
  "after_commit_oid": "c79f0ecfff6b7e4c34ae469ea55a4a4b60adca91"
}
```

### 2. Capture Manifests

| Capture ID | Commit OID | Exit Code | Diagnostics |
|------------|------------|-----------|-------------|
| `fsb-0025-before-c79f0ec` | `be84cb3cb0b540fa0c895afd7f7c6a41c01c81c6` | 1 | 16 |
| `fsb-0025-after-c79f0ec` | `c79f0ecfff6b7e4c34ae469ea55a4a4b60adca91` | 0 | 0 |

### 3. Legacy Occurrences

Extracted from build logs using `extract-legacy-text` command.

## What Remains

The CORRECTION02 must:
1. Fix path normalization to preserve backslashes in diagnostic messages
2. Enable occurrence loading in the episode engine
3. Compute transitions between before/after captures
4. Create real verification evidence

## Corrected Close Report

The CORRECTION01 verdict has been corrected to `PARTIAL_CHECKPOINT`. The predecessor close report's `CLOSED` verdict is hereby superseded.
