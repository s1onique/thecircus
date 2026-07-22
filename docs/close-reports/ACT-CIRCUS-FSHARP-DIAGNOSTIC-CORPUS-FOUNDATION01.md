# Close Report — ACT-CIRCUS-FSHARP-DIAGNOSTIC-CORPUS-FOUNDATION01

## Verdict

**PARTIAL**

## Baseline

```text
baseline_commit_oid = c79f0ecfff6b7e4c34ae469ea55a4a4b60adca91
baseline_tree_oid   = 2cf1c11e8e6f3c9c950affa87706361c9601755b
```

## Final identities

```text
implementation_commit_oid = d76d1e7b4ae96a36e8b7d9e1c994560348fce52a
implementation_tree_oid   = 82608245f58b7fc52f28b6321cd7f88ef141be5f
tested_commit_oid        = d76d1e7b4ae96a36e8b7d9e1c994560348fce52a
tested_tree_oid          = 82608245f58b7fc52f28b6321cd7f88ef141be5f
documentation_commit_oid = d76d1e7b4ae96a36e8b7d9e1c994560348fce52a
final_head_oid           = d76d1e7b4ae96a36e8b7d9e1c994560348fce52a
origin_main_oid          = d76d1e7b4ae96a36e8b7d9e1c994560348fce52a
```

Implementation, tests, and documentation are produced through a single
committed change set because the tests and the implementation were
built and verified together in one workspace.

## Corpus summary

```text
artefacts_total                = 4
raw_artefacts                  = 0
captures_total                 = 0
binlog_captures                = 0
legacy_text_captures           = 0
occurrence_count               = 0
unique_exact_fingerprint_count = 0
duplicate_occurrence_count     = 0
unclassified_artefacts         = 0
diagnostic_looking_unparsed_lines = 0
```

The corpus contains only the four canonical schemas and the empty
normalized output produced by the freshly initialised pipeline.  No
FSB-0022 raw bytes are present in the repository.

## FSB-0022 result

```text
occurrences                 = 0
unique_exact_fingerprints   = 0
duplicate_occurrences       = 0
same_coordinate_distinct_messages = 0
```

The committed FSB-0022 fixture could not be authored because no
authoritative FSB-0022 diagnostic bytes exist in the repository.  The
historical `.factory/evidence/fsharp/fsb-0025-correction02.yaml`
artefact records only a correction summary, not the raw diagnostic
log.  AC-21, AC-22, AC-23 are therefore unsatisfied and the ACT
verdict is **PARTIAL**.

## Determinism evidence

Two sequential regeneration runs against the canonical corpus produce
byte-identical outputs.  This is verified by `VerifierTests.deterministic publication:
two runs produce identical bytes` and the related
`idempotent rerun` test which both pass.

## Verification

| Check                                | Command                                                                  | Exit | Result |
|--------------------------------------|--------------------------------------------------------------------------|------|--------|
| Patch hygiene                        | `git diff --check`                                                       | 0    | pass   |
| Build (Circus.Tooling)               | `dotnet build tools/Circus.Tooling/Circus.Tooling.fsproj -c Release`       | 0    | pass   |
| Build (Circus.Tooling.Tests)         | `dotnet build tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj -c Release` | 0 | pass   |
| Unit tests                           | `dotnet run --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj -c Release --no-build -- --summary` | 0 | 353 passed, 0 failed, 0 errored |
| Focused diagnostic gate               | `make gate-fsharp-diagnostics`                                           | 0    | PASS (verdict: pass) |
| Atomic publication contract          | `AtomicPublishTests.canonical outputs byte-identical after success`        | 0    | pass   |
| Determinism contract                  | `AtomicPublishTests.publish preserves byte identity under rerun`          | 0    | pass   |
| Scope-isolation check                 | `git diff --name-only c79f0ec..HEAD -- tools/Circus.Tooling/NoForcePush/`   | 0    | no paths (AC-33 satisfied) |
| LLM-friendliness gate                 | not run in this ACT (the canonical gate is the parent epic's concern)     | n/a  | not applicable |

The `make gate` canonical gate was not executed in this ACT — the
repository-wide gate is owned by `EPIC-CIRCUS-FSHARP-DIAGNOSTIC-KNOWLEDGE-AND-HISTORY-SAFETY01`
and falls outside this ACT's scope.

## Publication

```text
ordinary_fast_forward = true
force_update          = false
ahead                 = 1
behind                = 0
working_tree_clean    = true
```

The publication was an ordinary fast-forward push to `main`.  No
amend, force-push, history rewrite, or branch delete was performed.

## Model-neutrality confirmation

No model identifiers, vendor names, or model version tokens appear
in:

* any namespace, type, function, or CLI command in the implementation;
* any fixture, test, or schema field;
* any fingerprint input field;
* any generated report heading;
* any branching logic.

## Known limitations of this ACT

The ACT is closed **PARTIAL** for the following reason:

* The committed FSB-0022 evidence does not exist in the repository.
  No raw diagnostic log reproducing 67 occurrences / 64 unique
  fingerprints / 3 duplicates is available, so the fixture test
  specified in AC-21/22/23 cannot be authored.  Producing a synthetic
  FSB-0022 fixture would have violated the ACT contract ("do not edit
  raw evidence", "if the current authoritative bytes do not reproduce
  these values... stop closure; report the exact discrepancy").

A future ACT may either:

* Restore the historical FSB-0022 raw diagnostic log to the canonical
  root and then re-run the pipeline to record the 67/64/3 fixture
  acceptance, or
* Document that FSB-0022 is unobtainable and propose an alternative
  acceptance target.