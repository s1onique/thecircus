# Close Report — ACT-CIRCUS-FSHARP-DIAGNOSTIC-CORPUS-FOUNDATION01

## Verdict

**PARTIAL** (correction recorded in
`ACT-CIRCUS-FSHARP-DIAGNOSTIC-CORPUS-FOUNDATION01-CORRECTION01.md`
and `ACT-CIRCUS-FSHARP-DIAGNOSTIC-CORPUS-FOUNDATION01-CORRECTION02.md`)

## Closure binding

```text
closure_binding_kind = annotated_tag_v1
closure_tag_name     = act/circus-fsharp-diagnostic-corpus-foundation01-partial-v1
```

This close report is **bound to its final commit by an annotated
tag**, not by self-embedding the commit OID.  The tag carries the
historical identities and acceptance verdict.  See
`ACT-CIRCUS-FSHARP-DIAGNOSTIC-CORPUS-FOUNDATION01-CORRECTION02.md`
for the binding protocol.

## Baseline

```text
baseline_commit_oid = c79f0ecfff6b7e4c34ae469ea55a4a4b60adca91
baseline_tree_oid   = 2cf1c11e8e6f3c9c950affa87706361c9601755b
```

## Historical identities (known at write time)

The identities below are already recorded in the repository's git
history and are cited as evidence.  They are **not** a self-claim
of the close report's own commit OID.

```text
implementation_commit_oid = d76d1e7b4ae96a36e8b7d9e1c994560348fce52a
implementation_tree_oid   = 82608245f58b7fc52f28b6321cd7f88ef141be5f

tested_commit_oid          = d76d1e7b4ae96a36e8b7d9e1c994560348fce52a
tested_tree_oid            = 82608245f58b7fc52f28b6321cd7f88ef141be5f

earlier_documentation_commit_oid = ce9803afd844c3cb54ed0597163a410027553359
correction01_commit_oid           = 646921141a4caecc8d943aaf4ef8c5224dfa94d4
```

The final HEAD and its annotated tag OID are produced **after** this
report is committed and are recorded by the tag itself.

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
authoritative FSB-0022 diagnostic bytes exist in the repository.
A recovery attempt (`CORRECTION01`) searched the filesystem, the
FSB-0025 rescue git bundle, the FSB-0020 reconciliation bundle's
`untracked.tar.gz`, and Leamas digest artefacts at
`/tmp/fsb0022_*.txt`.  Four files were found containing 64
deduplicated occurrences, but their SHA-256 (`3d16fe59...`) does
not match the historically recorded `3cf6d94e...` SHA-256 of
`fsb-0022-production.log`.  Per the recovery authority rule the
recovery is insufficient for acceptance.

AC-21/22/23 remain unsatisfied.

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
| LLM-friendly gate (FSharpDiagnostics) | `leamas factory verify llm-friendly`                                       | 0    | pass on FSharpDiagnostics surface (after CORRECTION02 splits long strings) |
| LLM-friendly gate (whole repo)        | `leamas factory verify llm-friendly`                                       | non-0 | FAIL on 129 pre-existing violations across docs/web/db trees |
| Canonical `make gate`                 | `make gate`                                                                | non-0 | FAIL on pre-existing `test-postgres` failures |
| Closure binding (annotated tag)       | `git tag -a act/circus-fsharp-diagnostic-corpus-foundation01-partial-v1 ...` | 0 | produced |

## Publication

```text
ordinary_fast_forward = true
force_update          = false
ahead                 = 0
behind                = 0
working_tree_clean    = true
```

```text
$ git rev-list --left-right --count origin/main...HEAD
0   0
```

The publication is an ordinary fast-forward push to `main` followed
by an annotated tag created after the final commit.  No amend,
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
   on disk or in git history.  AC-21/22/23 remain unsatisfied.

2. **LLM-friendly gate fails on pre-existing violations** (129 in
   docs, web, and db trees).  The two new long-line violations in
   `Cli.fs` lines 267 and 280 were fixed in `CORRECTION02`.
   AC-32 partial.

3. **Canonical `make gate` fails at `test-postgres`** due to
   pre-existing failures in `Circus.Persistence.Postgres.Tests`
   unrelated to this ACT.  The format-check segment now passes after
   Fantomas reformatting.  AC-29/30 partial.

## Next step

A separate ACT
(`ACT-CIRCUS-FSHARP-DIAGNOSTIC-REPAIR-EPISODE-LINKING01`) will bind
compiler states to source changes and subsequent diagnostics without
attempting to reconstruct the FSB-0022 raw evidence.
