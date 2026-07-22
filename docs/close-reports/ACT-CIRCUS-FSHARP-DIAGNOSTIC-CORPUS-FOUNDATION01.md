# Close Report — ACT-CIRCUS-FSHARP-DIAGNOSTIC-CORPUS-FOUNDATION01

## Verdict

**PARTIAL** (correction recorded in
`ACT-CIRCUS-FSHARP-DIAGNOSTIC-CORPUS-FOUNDATION01-CORRECTION01.md`)

## Baseline

```text
baseline_commit_oid = c79f0ecfff6b7e4c34ae469ea55a4a4b60adca91
baseline_tree_oid   = 2cf1c11e8e6f3c9c950affa87706361c9601755b
```

## Final identities (corrected)

```text
implementation_commit_oid = d76d1e7b4ae96a36e8b7d9e1c994560348fce52a
implementation_tree_oid   = 82608245f58b7fc52f28b6321cd7f88ef141be5f

tested_commit_oid          = d76d1e7b4ae96a36e8b7d9e1c994560348fce52a
tested_tree_oid            = 82608245f58b7fc52f28b6321cd7f88ef141be5f

documentation_commit_oid   = ce9803afd844c3cb54ed0597163a410027553359
documentation_tree_oid     = 82608245f58b7fc52f28b6321cd7f88ef141be5f

final_head_oid             = ce9803afd844c3cb54ed0597163a410027553359
origin_main_oid            = ce9803afd844c3cb54ed0597163a410027553359
```

Implementation and tests were produced through a single commit
(`d76d1e7b...`); documentation was committed separately
(`ce9803af...`) to record this close report.

## Corpus summary

```text
artefacts_total                  = 4
raw_artefacts                    = 0
captures_total                   = 0
binlog_captures                  = 0
legacy_text_captures             = 0
occurrence_count                 = 0
unique_exact_fingerprint_count   = 0
duplicate_occurrence_count       = 0
unclassified_artefacts           = 0
diagnostic_looking_unparsed_lines = 0
```

The corpus contains only the four canonical schemas and the empty
normalized output produced by the freshly initialised pipeline.  No
FSB-0022 raw bytes are present in the repository.

## FSB-0022 result

```text
occurrences                   = 0
unique_exact_fingerprints     = 0
duplicate_occurrences         = 0
same_coordinate_distinct_messages = 0
```

The committed FSB-0022 fixture could not be authored because no
authoritative FSB-0022 diagnostic bytes exist in the repository.  The
historical `.factory/evidence/fsharp/fsb-0025-correction02.yaml`
artefact records only a correction summary, not the raw diagnostic
log.  AC-21, AC-22, AC-23 are therefore unsatisfied and the ACT
verdict is **PARTIAL**.

A recovery attempt (recorded in `CORRECTION01`) searched the
filesystem, the FSB-0025 rescue git bundle, the FSB-0020
reconciliation bundle's untracked.tar.gz, and Leamas digest
artefacts in `/tmp/fsb0022_*.txt`.  Four files were found in `/tmp`
containing 64 deduplicated diagnostic occurrences from FSB-0022,
but their SHA-256 (`3d16fe59...`) does not match the historically
recorded `3cf6d94e...` SHA-256 of `fsb-0022-production.log`, so they
cannot be accepted as the original raw artefact under the recovery
authority rule.

## Determinism evidence

Two sequential regeneration runs against the canonical corpus
produce byte-identical outputs.  This is verified by
`VerifierTests.deterministic publication: two runs produce identical
bytes` and the related `idempotent rerun` test, both of which pass.

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
| Format check                          | `make format-check`                                                       | 0    | pass (after Fantomas reformatting of 9 FSharpDiagnostics test files) |
| **LLM-friendliness gate**             | `leamas factory verify llm-friendly`                                       | non-0 | **FAIL** — 131 pre-existing violations across `docs/close-reports/`, `docs/contracts/`, `web/`, `db/migrations/`; two new long-line violations in `tools/Circus.Tooling/FSharpDiagnostics/Cli.fs` lines 267 and 280. **AC-32 unsatisfied** |
| **Canonical `make gate`**             | `make gate`                                                                | non-0 | **FAIL** — fails at `test-postgres` (12 failed, 4 errored in `Circus.Persistence.Postgres.Tests`, pre-existing infrastructure issue unrelated to this ACT). **AC-29/30 partial — format check now passes after Fantomas run** |

The LLM-friendly gate and canonical `make gate` were previously
declared non-applicable or out of scope.  They were reclassified to
**FAIL (run, not skipped)** during `CORRECTION01`.  Both gates fail
for pre-existing reasons unrelated to this ACT.  No new gate
regression was introduced by this ACT.

## Publication

```text
ordinary_fast_forward = true
force_update          = false
ahead                 = 0
behind                = 0
working_tree_clean    = true
```

After the documentation commit is pushed:

```text
$ git rev-list --left-right --count origin/main...HEAD
0   0
```

Both refs identify the same commit; the count is `0 0`.  The
publication was an ordinary fast-forward push to `main`.  No amend,
force-push, history rewrite, or branch delete was performed.

## Model-neutrality confirmation

No model identifiers, vendor names, or model version tokens appear
in:

* any namespace, type, function, or CLI command in the implementation;
* any fixture, test, or schema field;
* any fingerprint input field;
* any generated report heading;
* any branching logic.

## Known limitations of this ACT

The ACT is closed **PARTIAL** for the following reasons:

1. **FSB-0022 evidence not recoverable in matching form.**  The
   historical raw bytes (`fsb-0022-production.log` with SHA-256
   `3cf6d94e...`) cannot be recovered from any source available
   on disk or in git history.  Leamas digest artefacts at
   `/tmp/fsb0022_*.txt` contain 64 deduplicated occurrences but
   fail the strict recovery authority rule.  AC-21/22/23
   remain unsatisfied.

2. **LLM-friendly gate fails with 131 violations**, of which two
   (long lines in `tools/Circus.Tooling/FSharpDiagnostics/Cli.fs`
   lines 267 and 280) were introduced by this ACT.  The remaining
   129 are pre-existing.  AC-32 unsatisfied.

3. **Canonical `make gate` fails at `test-postgres`** due to
   pre-existing failures in `Circus.Persistence.Postgres.Tests`
   unrelated to this ACT.  The format-check segment now passes after
   Fantomas reformatting.  AC-29/30 partial.

A future ACT may:

* Restore the historical FSB-0022 raw diagnostic log to the canonical
  root and re-run the pipeline to record the 67/64/3 fixture
  acceptance.
* Address the two long-line violations in `Cli.fs` lines 267 and 280.
* Diagnose and repair the pre-existing `test-postgres` failures.