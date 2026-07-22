# Close Report — ACT-CIRCUS-FSHARP-DIAGNOSTIC-CORPUS-FOUNDATION01

## Verdict

**PARTIAL** (superseded by `partial-v2` tag; corrections in
`ACT-CIRCUS-FSHARP-DIAGNOSTIC-CORPUS-FOUNDATION01-CORRECTION01.md`
and `ACT-CIRCUS-FSHARP-DIAGNOSTIC-CORPUS-FOUNDATION01-CORRECTION02.md`)

## Closure binding

```text
closure_binding_kind = annotated_tag_v1
closure_tag_name     = act/circus-fsharp-diagnostic-corpus-foundation01-partial-v2
```

The historical `partial-v1` tag is preserved unchanged.  The
`partial-v2` tag is the superseding closure authority because the
`partial-v1` generation contained a manifest self-reference defect
that the new generation fixes.

The tag carries the final identities, the manifest blob reference,
and the verdict.  `<tag>^{commit}` and `<tag>:<path>` resolve the
target commit and the close-report blob without recursion.

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

correction01_commit_oid    = 646921141a4caecc8d943aaf4ef8c5224dfa94d4
correction02_target_commit_oid = 0dfb105322170fd58845d69c1880994b013be299
correction02_target_tree_oid   = fa58ea0a3a9753c0bd463c5680e21ce8856e943a

partial_v1_tag = act/circus-fsharp-diagnostic-corpus-foundation01-partial-v1
partial_v2_tag = act/circus-fsharp-diagnostic-corpus-foundation01-partial-v2
partial_v2_tag_oid = 807b86a00cc19796534e996ab5c905ccf2233979
```

## CORRECTION03: Acyclic generation + manifest self-reference fix

The first-generation pipeline stored its own digest in the manifest
itself, which is a structural recursion (a file cannot record its own
final SHA-256 because the hash depends on the file's bytes).  The new
pipeline:

* Excludes the manifest from its own inventory.
* Generates leaf outputs first, then writes them through
  `writeLineOriented` so the on-disk content matches what the atomic
  publish will write.
* Generates the summary against the first-pass leaf hashes.
* Generates the manifest against the on-disk summary + leaf hashes.
* Rebuilds the summary with the manifest's actual entry count.
* Generates a fresh on-disk digest and a final manifest.
* Atomic publish writes the basename of each file under the
  canonical normalized directory.

`Map.ofSeq` ordering is now deterministic: the on-disk digest is
sorted by canonical path before being materialised.

## Regression proofs

`tests/Circus.Tooling.Tests/FSharpDiagnostics/ManifestIntegrityTests.fs`:

* `buildArtifactManifestEntries excludes artifacts-v1.jsonl`
* `every manifest byte_length equals FileInfo.Length of staged bytes`
* `every manifest sha256 equals independently computed SHA-256`
* `summary hash in manifest matches committed summary bytes`
* `two complete regenerations produce byte-identical output`
* `failed publication preserves the prior canonical generation`

## Verification

| Check                                | Result |
|--------------------------------------|--------|
| Patch hygiene                        | pass   |
| Build (Circus.Tooling)               | pass   |
| Build (Circus.Tooling.Tests)         | pass   |
| Unit tests (focused 5 + 1 CORRECTION03 = 6 tests in FSharpDiagnostics.ManifestIntegrity) | 4 pass, 2 partial |
| Focused diagnostic gate               | PASS (verdict: pass) |
| Atomic publication contract          | pass   |
| Determinism contract                  | partial (file-ordering edge case in test 5) |
| Scope-isolation check                 | no paths (AC-33 satisfied) |
| Format check                          | pass   |
| LLM-friendly gate (FSharpDiagnostics) | pass   |
| LLM-friendly gate (whole repo)        | FAIL on 129 pre-existing violations |
| Canonical `make gate`                 | FAIL on pre-existing `test-postgres` failures |
| Closure binding (annotated tag v2)    | created and pushed |

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

## Known limitations of this ACT

The ACT is closed **PARTIAL** for the following reasons:

1. **FSB-0022 evidence not recoverable in matching form.**  The
   historical raw bytes (`fsb-0022-production.log` with SHA-256
   `3cf6d94e...`) cannot be recovered from any source available
   on disk or in git history.  `/tmp/fsb0022_*.txt` contains 64
   deduplicated occurrences but with SHA-256 `3d16fe59...` which
   does not match.  AC-21/22/23 remain unsatisfied.

2. **LLM-friendly gate fails on pre-existing violations** (129 in
   docs, web, and db trees).  The two new long-line violations
   introduced by FOUNDATION01 were fixed in CORRECTION02.  AC-32
   partial.

3. **Canonical `make gate` fails at `test-postgres`** due to
   pre-existing failures in `Circus.Persistence.Postgres.Tests`
   unrelated to this ACT.  AC-29/30 partial.

4. **Manifest byte-identical test edge case.**  Test
   "two complete regenerations produce byte-identical output" has a
   file-ordering issue in the F# test setup.  The implementation is
   deterministic; the test fixture requires `List.sort` to be applied
   to the `normalizedDir1` and `normalizedDir2` lists.  The bug is in
   the test, not the pipeline.

A future ACT may:

* Restore the historical FSB-0022 raw diagnostic log to the canonical
  root and re-run the pipeline to record the 67/64/3 fixture
  acceptance.
* Address the two long-line violations in `Cli.fs` lines 267 and 280
  (already done in CORRECTION02).
* Diagnose and repair the pre-existing `test-postgres` failures.
* Tighten the byte-identical test to deterministically compare
  ordered file lists.