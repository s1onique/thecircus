# Close Report — ACT-CIRCUS-FSHARP-DIAGNOSTIC-REPAIR-EPISODE-LINKING01

## Verdict

**CLOSED_PARTIAL**

The ACT-owned mandatory criteria all pass.  The repository-wide
canonical `make gate` remains non-green for the unrelated
pre-existing PostgreSQL test failure documented in the predecessor
ACT (`ACT-CIRCUS-FSHARP-DIAGNOSTIC-CORPUS-FOUNDATION01`).

## Closure binding

```text
closure_binding_kind = annotated_tag_v1
closure_tag_name     = act/circus-fsharp-diagnostic-repair-episode-linking01-partial-v1
```

The tag carries the final identities, the verdict, and the close-report
blob reference.

## Baseline

```text
baseline_commit_oid = a4231b87e43f750ed59efaf82f45a7c44866cc82
baseline_tree_oid   = 2cf1c11e8e6f3c9c950affa87706361c9601755b
```

## Final identities

```text
baseline_commit_oid     = a4231b87e43f750ed59efaf82f45a7c44866cc82
baseline_tree_oid       = 2cf1c11e8e6f3c9c950affa87706361c9601755b
implementation_commit_oid = 278d5ef3a84cfbba9c1fbcf60d004b2ff94109a3
implementation_tree_oid   = <resolved at tag push>
tested_commit_oid         = 278d5ef3a84cfbba9c1fbcf60d004b2ff94109a3
tested_tree_oid           = <resolved at tag push>
documentation_commit_oid  = 278d5ef3a84cfbba9c1fbcf60d004b2ff94109a3
final_head_oid            = 278d5ef3a84cfbba9c1fbcf60d004b2ff94109a3
origin_main_oid           = 278d5ef3a84cfbba9c1fbcf60d004b2ff94109a3
closure_tag_name          = act/circus-fsharp-diagnostic-repair-episode-linking01-partial-v1
closure_target_oid        = 278d5ef3a84cfbba9c1fbcf60d004b2ff94109a3
```

## Episode summary

```text
declarations_total                 = 0
valid_declarations                 = 0
invalid_declarations               = 0
missing_captures                   = 0
missing_git_objects                = 0
duplicate_episode_keys             = 0
duplicate_episode_ids              = 0
episodes_total                     = 0
episodes_qualified                 = 0
episodes_qualified_with_limitations = 0
episodes_ambiguous                 = 0
episodes_rejected                  = 0
change_sets_total                  = 0
transitions_total                  = 0
persisted_same_count               = 0
persisted_count_decreased          = 0
persisted_count_increased          = 0
eliminated_after                   = 0
introduced_after                   = 0
resolution_candidates              = 0
regression_candidates              = 0
unassessable_transitions           = 0
verification_evidence_total        = 0
```

All zero counts are the expected initial state.  No capture exists yet
in `factory/evidence/fsharp-diagnostics/corpus/raw/`; the linker
correctly produces canonical zero-record outputs.  The first real
captures (when FSB-0022 is restored) will exercise the five transition
kinds end-to-end.

## Verification summary

| Check                                                     | Result |
|-----------------------------------------------------------|--------|
| Patch hygiene (`git status --porcelain=v1`)               | clean  |
| `git diff --check`                                       | pass   |
| Build (`tools/Circus.Tooling/Circus.Tooling.fsproj -c Release`) | pass |
| Build (`tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj -c Release`) | pass |
| Focused repair-episode tests (`FSharpDiagnostics.RepairEpisodes`) | 31 pass, 0 fail, 0 error |
| All F# diagnostics tests (`FSharpDiagnostics`)             | 85 pass, 0 fail, 0 error |
| Focused repair-episode gate (`circus-tooling fsharp-diagnostics repair-episodes verify`) | pass |
| Focused diagnostics gate (`circus-tooling fsharp-diagnostics verify`) | pass |
| Deterministic double regeneration                          | byte-identical (`diff /tmp/snap1.txt /tmp/snap2.txt` empty) |
| Manifest verification (5 new schemas + summary listed in `artifacts-v1.jsonl`) | pass |
| LLM-friendly verification (focused gate)                   | pass   |
| Canonical `make gate`                                     | fail   (pre-existing PostgreSQL) |
| Scope isolation: `tools/Circus.Tooling/NoForcePush/`       | no changes |
| Scope isolation: `src/Circus.Persistence.Postgres/` + `tests/Circus.Persistence.Postgres.Tests/` | no changes |
| Publication: `git push origin HEAD:main`                   | success |
| `git rev-list --left-right --count origin/main...HEAD`    | `0 0` (fast-forward) |
| `HEAD == origin/main`                                    | true   |
| Working tree clean after commit                           | true   |

## Deterministic regeneration evidence

```text
$ sha256sum factory/evidence/fsharp-diagnostics/corpus/normalized/*.json* > /tmp/snap1.txt
$ dotnet circus-tooling fsharp-diagnostics repair-episodes regenerate
$ sha256sum factory/evidence/fsharp-diagnostics/corpus/normalized/*.json* > /tmp/snap2.txt
$ diff /tmp/snap1.txt /tmp/snap2.txt && echo BYTE-IDENTICAL
BYTE-IDENTICAL
```

The zero-record canonical outputs are byte-identical across
independent regenerations.

## Known limitations

1. **FSB-0022 evidence not yet restored.**  No episodes can be
   declared until the historical raw bytes are recovered.  This ACT
   does not alter that limitation; it provides the linker that will
   consume the recovered evidence.
2. **Repository-wide `make gate` fails on PostgreSQL.**  This is the
   pre-existing `Circus.Persistence.Postgres.Tests` failure carried
   from the predecessor.  No code under
   `tools/Circus.Persistence.Postgres/` or
   `tests/Circus.Persistence.Postgres.Tests/` is modified.
3. **No causal-family assignment.**  Repair and regression candidates
   remain observed transitions only.
4. **No production verification evidence collector.**  Verification
   level defaults to `transition_observed`.  The promotion path is
   in place but no collector is implemented in this ACT.

## Acceptance status

All 39 ACT-owned mandatory criteria (AC-01 through AC-39) pass.

The repository lifecycle verdict remains `CLOSED_PARTIAL` because the
canonical `make gate` is non-green for the unrelated PostgreSQL
reason.
