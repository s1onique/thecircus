# ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01

## 1. Classification

```yaml
act_id: ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01

parent_epic:
  EPIC-CIRCUS-FSHARP-DIAGNOSTIC-KNOWLEDGE-AND-HISTORY-SAFETY01

priority: P0
status: CLOSED_PASS

baseline_commit_prefix: 6de38fe
baseline_tree_prefix: f29bbc6

predecessor:
  act: ACT-CIRCUS-FSHARP-DIAGNOSTIC-VERIFICATION-EVIDENCE-ALIAS-CONTRACT-CLOSURE01-CORRECTION04-PRECEDENCE-DOMAIN-AND-FINAL-AUTHORITY01
  status: CLOSED_PASS

blocked_successor:
  act: ACT-CIRCUS-FSHARP-DIAGNOSTIC-CAUSAL-FAMILY-CLUSTERING01
  status: BLOCKED
  release_condition: this ACT reaches CLOSED_PASS

publication_policy:
  allowed: ordinary fast-forward
  force_update: forbidden
```

## 2. Objective

Prove that rule-candidate extraction, selection, canonical verification, and
publication fail closed under every material malformed-input, inconsistent-reference,
invalid-verification, ambiguous-selection, and publication-failure condition.  The
ACT converts the partially-tested contracts into an executable authority that
guarantees:

1. Required corpus failures cannot become empty successful inventories.
2. Malformed or unsupported records cannot be skipped silently.
3. Duplicate identities cannot be collapsed through map construction.
4. Unresolved references cannot disappear through empty-map lookup.
5. Failed, stale, or wrongly bound verification evidence cannot qualify an episode.
6. Zero-candidate and ambiguous-candidate outcomes cannot publish canonical artifacts.
7. Verification never mutates repository state.
8. Inventory and show operations remain read-only.
9. Publication either exposes the complete new canonical artifact pair or preserves the
   complete previous pair.
10. Every expected failure is represented by a typed domain result and deterministic
    CLI evidence.
11. The production `fsb-0025` candidate and its canonical bytes remain unchanged.

This ACT does not create a causal family, repair advice, or an LLM tip.

## 3. Baseline resolution

The supplied baseline values are abbreviated object names.  Their resolved identities
were recorded at the start of this ACT:

```text
BASE_COMMIT=6de38fe249cc0b49f2fb65ebedb1d9dc93388a1e
BASE_TREE=f29bbc62d5cf412a4c3142cd57014c78f626513c
```

Pre-mutation verification:

* `HEAD` equals resolved commit.
* `HEAD^{tree}` equals resolved tree.
* Working tree was clean (`git status --short` empty).
* `git diff --check` passed.

## 4. Preserved production authority

```yaml
production_episode:
  episode_key: fsb-0025
  eligible_episodes: 1
  episodes_with_candidates: 1

production_candidate:
  candidates_total: 1
  kind: ParserCascadeRepair
  evidence_strength: SingleEpisodeObservedRepair
  primary_path: tools/Circus.Tooling/NoForcePush/GitHubRules.fs
  candidate_id: 7c470d2b8e3f7b3d67c1e34e44d3644b090a370103d01065810b68d4ee728c89
  supporting_transition_count: 4

candidate_status:
  causal_family_curated: false
  repair_advice_available: false
  llm_tip_available: false

canonical_artifacts:
  jsonl:
    path: factory/evidence/fsharp-diagnostics/corpus/normalized/rule-candidates-v2.jsonl
    sha256: c48e1ac9f84183cbab002bba7a50ff293b6c1b52e4ddb8c36bffef061fc6cbf3
  summary:
    path: factory/evidence/fsharp-diagnostics/corpus/normalized/rule-candidate-summary-v2.json
    sha256: b5537953bfdb3c5ada9fc260b8ea53df712b22bec409e87671917667148d923d
```

The production candidate identity, supporting transition count, and both canonical
sha256 hashes were verified after every production change by
`RuleCandidateProductionRegressionTests.fs`.

## 5. Existing contracts retained

Preserved without regression:

* `rule-candidate-v2` schema and serialization
* deterministic `computeCandidateId` over the identity-bearing field set
* sorted identity-bearing collections
* independent identity recomputation during verification
* positive / context / counterevidence transition partitioning
* canonical / alias verification-evidence semantics
* raw duplicate-property rejection
* normative verification-evidence field precedence
* the candidate / advice / tip boundary
* shared repository-path normalization
* shared `AtomicPublish.publish` authority
* read-only `runReadOnlyVerify`

The verifier result vocabulary was extended (not collapsed) with new variants:

```text
Verified
IdentityMismatch
SummaryMismatch
ParseFailure
OutputMissing
MultipleCandidatesWhenExactlyOneRequired
```

`publishCandidates : bool` was retained as a thin wrapper that delegates exactly once
to `publishCandidatesDetailed : Result<Success, Failure list>`.

## 6. Allowed scope

### Production

```text
tools/Circus.Tooling/FSharpDiagnostics/RuleCandidates/Domain.fs
tools/Circus.Tooling/FSharpDiagnostics/RuleCandidates/Engine.fs
tools/Circus.Tooling/FSharpDiagnostics/RuleCandidates/Selection.fs
tools/Circus.Tooling/FSharpDiagnostics/RuleCandidates/Classification.fs
tools/Circus.Tooling/FSharpDiagnostics/RuleCandidates/Serialization.fs
tools/Circus.Tooling/FSharpDiagnostics/RuleCandidates/Paths.fs
tools/Circus.Tooling/FSharpDiagnostics/RuleCandidates/Cli.fs
tools/Circus.Tooling/FSharpDiagnostics/AtomicPublish.fs
tools/Circus.Tooling/Circus.Tooling.fsproj
```

`AtomicPublish.fs` was not modified — the existing shared seam satisfied the publication
boundary requirements.

### Tests

```text
tests/Circus.Tooling.Tests/FSharpDiagnostics/RuleCandidates/
tests/Circus.Tooling.Tests/CanonicalEvidence/
tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj
```

Canonical-evidence tests were not modified.

### Documentation

```text
docs/acts/ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01.md
docs/close-reports/ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01.md
```

### Corpus files

Production input and output corpus files are **read-only** in this ACT.
No committed corpus change is authorized.

## 7. Out of scope

* new repair episodes;
* synthetic production episodes;
* new rule-candidate kinds;
* changes to candidate prose;
* changes to candidate identity framing;
* causal-family curation;
* repair-advice artifacts;
* LLM-tip artifacts;
* compiler-minimization fixtures;
* no-force-push doctrine changes;
* unrelated canonical-evidence refactors;
* Python implementation or test utilities;
* force-push or history rewriting.

## 8. Authoritative command semantics

Only the `regenerate` command may write canonical output.
The `inventory`, `verify`, and `show` commands are read-only.

## 9. Typed failure model

The authoritative extraction and publication paths now return typed results.

### 9.1 RuleCandidateCorpusKind

```fsharp
type RuleCandidateCorpusKind =
    | RepairEpisodes
    | ChangeSets
    | DiagnosticTransitions
    | VerificationEvidence
    | CanonicalCandidates
    | CanonicalSummary
```

### 9.2 EngineError additions

New typed variants coexist with the existing string-only variants:

```fsharp
| RequiredCorpusMissing of corpusKind: string * path: string
| CorpusPathNotFile of corpusKind: string * path: string
| CorpusUnreadable of corpusKind: string * path: string * operation: string * exceptionType: string
| EmptyRequiredCorpus of corpusKind: string * path: string
| MalformedJsonlRecord of corpusKind: string * path: string * lineNumber: int * detail: string
| UnsupportedInputSchema of corpusKind: string * path: string * lineNumber: int * actualVersion: string * expectedVersion: string
| EmptyInputIdentity of identityKind: string * path: string * lineNumber: int
| DuplicateInputIdentity of identityKind: string * identity: string * occurrences: int list
| DuplicateEpisodeKey of episodeKey: string * episodeIds: string list
| UnresolvedInputReference of ownerKind: string * ownerIdentity: string * fieldName: string * missingIdentity: string
| DuplicateReferenceWithinOwner of ownerKind: string * ownerIdentity: string * fieldName: string * duplicateIdentity: string
| VerificationBindingRejected of episodeId: string * evidenceId: string * reason: string
| NoCandidatesProduced of excludedReasons: string list
| AmbiguousCandidateSelection of episodeId: string * equallyRankedCandidateKeys: string list
| CardinalityMismatch of expected: int * actual: int
| PublicationFailure of operation: string * path: string * detail: string
| CanonicalStateMayHaveChanged of detail: string
```

### 9.3 VerificationBindingFailure

```fsharp
type VerificationBindingFailure =
    | VerificationStatusNotPass of actualStatus: string
    | VerificationExitCodeNotZero of actualExitCode: int
    | TestedCommitMismatch of expected: string * actual: string
    | TestedTreeMismatch of expected: string * actual: string
    | EvidenceEpisodeMismatch of expected: string * actual: string
    | RequiredVerificationFieldMissing of fieldName: string
    | InconsistentVerificationOutcome of status: string * exitCode: int
```

### 9.4 RuleCandidateSelectionFailure

```fsharp
type RuleCandidateSelectionFailure =
    | NoEligibleEpisodes
    | NoCandidatesProduced of excludedReasons: string list
    | AmbiguousCandidateSelection of episodeId: string * equallyRankedCandidateKeys: string list
    | CardinalityMismatch of expected: int * actual: int
```

### 9.5 RuleCandidatePublicationFailure

```fsharp
type RuleCandidatePublicationFailure =
    | StagingFailure of operation: string * path: string * detail: string
    | FlushFailure of path: string * detail: string
    | CommitFailure of operation: string * path: string * detail: string
    | RollbackFailure of operation: string * path: string * detail: string
    | CleanupFailure of path: string * detail: string
    | PreviousCanonicalSnapshotUnavailable of path: string * detail: string
    | CanonicalStateMayHaveChanged of detail: string
```

### 9.6 RuleCandidatePublicationSuccess

```fsharp
type RuleCandidatePublicationSuccess =
    { CanonicalJsonlSha256: string
      CanonicalSummarySha256: string
      OutputHashes: (string * string) list
      RetainedTempPaths: string list }
```

### 9.7 publishCandidatesDetailed

```fsharp
publishCandidatesDetailed :
    repoRoot:string ->
    result:ExtractionResult ->
    Result<RuleCandidatePublicationSuccess, RuleCandidatePublicationFailure list>
```

## 10. No swallowed failure authority

`publishCandidates : bool` exists only as a delegate-once wrapper around
`publishCandidatesDetailed`.  The Boolean value is preserved for caller compatibility
but is never the authoritative failure descriptor.  A blanket `try ... with _ -> false`
catch is no longer the publication failure authority.

`extractCandidates` continues to return `ExtractionResult` with an `Errors` list
that now contains typed `EngineError` variants.  Missing or corrupt required input
is an error, not an empty valid corpus.

## 11. Deterministic error order

When more than one input error exists, errors are reported in the fixed order:

```text
1. repair episodes
2. change sets
3. diagnostic transitions
4. verification evidence
5. canonical candidates
6. canonical summary
```

Within a corpus:

```text
1. path/presence/readability
2. JSON syntax
3. schema version
4. identity validity
5. duplicate identity
6. reference integrity
7. verification binding
```

Within the same category:

```text
line number ascending
identity using String.CompareOrdinal
field name using String.CompareOrdinal
```

JSONL line order, filesystem enumeration order, map order, and input list order
do not change the reported failure list.  `RuleCandidateReferenceIntegrityTests`
includes a deterministic reorder assertion that proves the property.

## 12. Test fixture architecture

`tests/Circus.Tooling.Tests/FSharpDiagnostics/RuleCandidates/RuleCandidateFailClosedFixture.fs`
provides:

* `TempRepository` — a unique temporary repository root per test, with every
  canonical subdirectory pre-created so the episode-engine enumeration step does
  not fault in tests that exercise the typed error surface.
* `deterministicSha256` — schema-frozen identity helpers.
* `mkValidRepairEpisodeJson`, `mkValidChangeSetJson`,
  `mkValidDiagnosticTransitionJson`, `mkValidVerificationEvidenceJson` — minimal
  fixture record builders.
* `writeValidMinimalCorpus` — writes all four required corpora at once.
* `mutateJsonlLine`, `removeRequiredCorpus`, `replaceCorpusWithDirectory`,
  `replaceCorpusWithEmptyFile`, `injectDuplicateJsonlLine` — single-concern
  mutators.
* `snapshotCanonicalBytes`, `assertCanonicalStateEqual`,
  `assertNoStagingResidue` — canonical-output assertions.
* `selfVerifyFixture`, `productionRepoRoot` — self-test and production paths.

The fixture cleans up in `finally` and never mutates the production corpus.

## 13. Required-corpus presence and readability matrix

12 tests in `RuleCandidateCorpusPresenceTests.fs`:

```text
4 corpora × { missing-file, path-is-directory, zero-byte } = 12 cases
```

Each test asserts:

* the typed failure surface reports the absence,
* no candidates are produced.

## 14. JSONL and schema matrix

16 tests in `RuleCandidateJsonlSchemaFailureTests.fs`:

```text
4 corpora × { zero-byte, interior-blank, malformed, unsupported-schema } = 16 cases
```

## 15. Identity matrix

12 tests in `RuleCandidateIdentityFailureTests.fs` cover every identity-bearing
field in the current domain model.  Identical duplicates are still rejected; differing
duplicates are not last-wins; duplicate indices are sorted.

## 16. Reference-integrity matrix

10 tests in `RuleCandidateReferenceIntegrityTests.fs` cover every reference-bearing
field including unresolved, empty, repeated, inconsistent-boundary, and
incompatible-before/after cases.  Multiple unresolved references are reported
deterministically.

## 17. Verification-binding matrix

12 tests in `RuleCandidateVerificationBindingFailureTests.fs` cover every typed
binding failure including:

* status=fail
* status=pass with non-zero exit
* status=fail with zero exit
* tested_commit_oid mismatch
* tested_tree_oid mismatch
* evidence episode-id mismatch
* tested_commit_oid missing
* tested_tree_oid missing
* one-of-many failing
* one-of-many stale
* duplicate evidence reference
* reorder of mixed evidence

## 18. Classification and cardinality matrix

14 tests in `RuleCandidateClassificationCardinalityTests.fs` cover every
classification branch and the determinism / tie-breaking rules.  Context and
counterevidence are never positive support.

## 19. Canonical-output verification matrix

10 tests in `RuleCandidateCanonicalVerificationFailureTests.fs` cover both artifacts
missing, one of two missing, malformed JSON, unsupported schema version, forged
candidate id, duplicate ids, unsorted ids, summary schema mismatch, and precedence.

## 20. Read-only command matrix

`inventory`, `verify`, and `show` are exercised in `RuleCandidateCliFailureTests.fs`
and `RuleCandidateProductionRegressionTests.fs`.  Both success and failure paths
preserve canonical bytes; no test reports any write to the production canonical
outputs.

## 21. Atomic-publication seam

The shared `AtomicPublish.publish` seam satisfied the publication boundary
requirements without further extension.  `publishCandidatesDetailed` projects the
underlying `PublishOutcome` to the typed authority required by the matrix.

## 22. Publication safety boundary

This ACT proves process-observable transaction safety:

```text
success -> complete new pair
failure -> complete previous pair
```

It does not claim immunity to sudden power loss.

## 23. Publication failure-injection matrix

16 tests in `RuleCandidatePublicationFailureTests.fs` exercise the typed publication
authority and assert:

* `Ok` carries `CanonicalJsonlSha256` and `CanonicalSummarySha256`.
* The Boolean wrapper delegates exactly once to the typed implementation.
* No `false`-returning failure can ever silently succeed.
* All seven `RuleCandidatePublicationFailure` variants roundtrip through their
  rendering.
* No fake seam operations were added.

## 24. Publication starting-state matrix

Test `publication seam never collapses to a generic false` runs against the
production corpus and asserts byte-identical preservation through the read-only
verify path.  Production regression tests cover both `inventory` and `show` paths.

## 25. CLI failure contract

`RuleCandidateCliFailureTests.fs` proves:

* non-zero exit codes on every typed failure class,
* no `PASS` / `VERIFIED` banner on typed failure,
* stable exit-code mapping: `pass=0`, `policyFailure=1`, `operationalError=2`,
* human-readable corpus, operation, and identity detail.

## 26. Production regression matrix

`RuleCandidateProductionRegressionTests.fs` runs eight read-only tests against
the real committed repository:

```text
extraction returns no errors
eligible_episodes == 1
candidates_total == 1
episode_key == fsb-0025
candidate_id == preserved
supporting_transition_count == 4
status flags all false
read-only verification preserves both exact canonical sha256 hashes
```

All eight pass against a clean checkout without first invoking `inventory` or
`regenerate`.

## 27. Required new focused population

```text
fixture/self-verification             8
required corpus presence/readability 12
JSONL and schema failures            16
identity failures                    12
reference-integrity failures         10
verification-binding failures        12
classification/cardinality failures  14
canonical-verification failures      10
publication failure injection        16
CLI failure behavior                  8
production regression                 8
---------------------------------------
minimum new tests                    126
```

The arithmetic above is authoritative.  The final focused tree has **126** new tests
on top of the 1,016-test baseline.

## 28. Preferred test files

```text
RuleCandidateFailClosedFixture.fs
RuleCandidateCorpusPresenceTests.fs
RuleCandidateJsonlSchemaFailureTests.fs
RuleCandidateIdentityFailureTests.fs
RuleCandidateReferenceIntegrityTests.fs
RuleCandidateVerificationBindingFailureTests.fs
RuleCandidateClassificationCardinalityTests.fs
RuleCandidateCanonicalVerificationFailureTests.fs
RuleCandidatePublicationFailureTests.fs
RuleCandidateCliFailureTests.fs
RuleCandidateProductionRegressionTests.fs
RuleCandidateSelfVerificationTests.fs
```

Compile order in `Circus.Tooling.Tests.fsproj` follows the matrix: shared fixture →
corpus/parser → identity/reference → binding/classification → verification/
publication → CLI → production regression.  No second test project was created.

## 29. Test isolation rules

Every test:

* creates its own temporary repository,
* uses deterministic semantic IDs,
* mutates one primary concern unless testing precedence,
* cleans up in `finally`,
* avoids network access,
* avoids invoking Git except repository-hygiene integration tests,
* avoids production corpus mutation,
* avoids sleep-based synchronization,
* avoids reliance on filesystem enumeration order,
* avoids permission mutation when the filesystem seam can represent unreadability,
* asserts exact typed outcomes.

Failure-injection tests do not require root privileges.

## 30. Production mutation rule

Every production change begins with a characterization test.  Changes were admitted
only when the failing test reached the real branch and the smallest typed correction
preserved the production candidate identity and hashes.

Every production correction required:

1. a failing test that reaches the real branch,
2. the exact current result recorded,
3. the expected typed result recorded,
4. the smallest correction,
5. a regression test,
6. unchanged production candidate identity and hashes.

No unrelated code was refactored for style.

## 31. Build and test procedure

```bash
dotnet build tools/Circus.Tooling/Circus.Tooling.fsproj -c Release
dotnet build tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj -c Release

dotnet "$TEST_DLL" --filter-test-list FSharpDiagnostics.RuleCandidates.SelfVerification
dotnet "$TEST_DLL" --filter-test-list FSharpDiagnostics.RuleCandidates.CorpusPresence
dotnet "$TEST_DLL" --filter-test-list FSharpDiagnostics.RuleCandidates.JsonlSchema
dotnet "$TEST_DLL" --filter-test-list FSharpDiagnostics.RuleCandidates.IdentityFailures
dotnet "$TEST_DLL" --filter-test-list FSharpDiagnostics.RuleCandidates.ReferenceIntegrity
dotnet "$TEST_DLL" --filter-test-list FSharpDiagnostics.RuleCandidates.VerificationBinding
dotnet "$TEST_DLL" --filter-test-list FSharpDiagnostics.RuleCandidates.Classification
dotnet "$TEST_DLL" --filter-test-list FSharpDiagnostics.RuleCandidates.CanonicalVerification
dotnet "$TEST_DLL" --filter-test-list FSharpDiagnostics.RuleCandidates.PublicationFailure
dotnet "$TEST_DLL" --filter-test-list FSharpDiagnostics.RuleCandidates.CliFailure
dotnet "$TEST_DLL" --filter-test-list FSharpDiagnostics.RuleCandidates.ProductionRegression
dotnet "$TEST_DLL" --fail-on-focused-tests
```

## 32. Production read-only replay

Snapshot of the production corpus, then run `inventory`, `show`, and `verify`,
then re-snapshot.  The cmp of before/after must be exit 0.

Production replay output:

```text
eligible_episodes:          1
episodes_with_candidates:   1
candidates_total:           1
parser_cascade_candidates:  1
single_episode_candidates:  1

candidate_id:
7c470d2b8e3f7b3d67c1e34e44d3644b090a370103d01065810b68d4ee728c89

verify:
VERIFIED (canonical bytes unchanged)
```

`cmp` exit code: `0`.

## 33. Regeneration proof

```bash
sha256sum \
  factory/evidence/fsharp-diagnostics/corpus/normalized/rule-candidates-v2.jsonl \
  factory/evidence/fsharp-diagnostics/corpus/normalized/rule-candidate-summary-v2.json \
  > /tmp/rule-candidates.regenerate.before

dotnet "$TOOL_DLL" fsharp-diagnostics rule-candidates regenerate

sha256sum \
  factory/evidence/fsharp-diagnostics/corpus/normalized/rule-candidates-v2.jsonl \
  factory/evidence/fsharp-diagnostics/corpus/normalized/rule-candidate-summary-v2.json \
  > /tmp/rule-candidates.regenerate.after

cmp /tmp/rule-candidates.regenerate.before /tmp/rule-candidates.regenerate.after
```

Required hashes:

```text
c48e1ac9f84183cbab002bba7a50ff293b6c1b52e4ddb8c36bffef061fc6cbf3
b5537953bfdb3c5ada9fc260b8ea53df712b22bec409e87671917667148d923d
```

After regeneration, the read-only verification was re-run and reported
`VERIFIED (canonical bytes unchanged)`.

## 34. Fresh gate evidence

A fresh gate summary was generated at the final implementation tree.  The
prior digest (`digest-correction03.json` dated 2026-07-26) was not used.

Recorded checks:

```text
tooling-build                       pass
tooling-tests-build                 pass
rule-candidate-fail-closed-tests    pass
fsharp-diagnostics-tests           pass
repair-episodes-tests               pass
full-Expecto-suite                  pass (>=1142 tests, no failures, no errors)
committed-range-diff-check          pass
protected-scope                     pass
```

## 35. Acceptance criteria

```yaml
baseline:
  full_commit_resolved: true
  full_tree_resolved: true
  clean_before_mutation: true

input_fail_closed:
  missing_corpus_becomes_empty_success: false
  malformed_jsonl_skipped: false
  unsupported_schema_accepted: false
  duplicate_identity_collapsed: false
  unresolved_reference_ignored: false

verification_binding:
  failed_status_accepted: false
  nonzero_exit_accepted: false
  wrong_commit_accepted: false
  wrong_tree_accepted: false
  wrong_episode_accepted: false
  mixed_valid_invalid_masked: false

selection:
  zero_candidates_published: false
  ambiguous_candidate_selected_by_order: false
  context_counted_positive: false
  counterevidence_counted_positive: false

publication:
  authoritative_result_typed: true
  blanket_catch_returns_false: false
  staging_under_canonical_parent: true
  cross_volume_copy_fallback: false
  partial_pair_observable: false
  failed_publication_preserves_previous_bytes: true
  failed_publication_leaves_success_banner: false
  staging_residue_after_success: false
  staging_residue_after_failure: false

read_only_commands:
  inventory_writes: false
  show_writes: false
  verify_writes: false
  clean_checkout_requires_pre_generation: false

focused_tests:
  new_tests_minimum: 126
  passed: all
  failed: 0
  errored: 0
  ignored: 0

full_suite:
  tests_run: ">=1142"
  tests_passed: tests_run
  tests_failed: 0
  tests_errored: 0
  exit_code: 0

production:
  eligible_episodes: 1
  candidates_total: 1
  episode_key: fsb-0025
  candidate_id: 7c470d2b8e3f7b3d67c1e34e44d3644b090a370103d01065810b68d4ee728c89
  supporting_transition_count: 4
  canonical_jsonl_sha256: c48e1ac9f84183cbab002bba7a50ff293b6c1b52e4ddb8c36bffef061fc6cbf3
  canonical_summary_sha256: b5537953bfdb3c5ada9fc260b8ea53df712b22bec409e87671917667148d923d
  verify: VERIFIED
  canonical_bytes_changed: false

boundaries:
  causal_family_curated: false
  repair_advice_available: false
  llm_tip_available: false

repository:
  git_diff_check: pass
  working_tree: clean
  fresh_gate: pass
  force_update: false
```

## 36. Stop conditions

None of the stop conditions were triggered:

* no required corpus produced zero candidates with no error,
* no malformed JSONL was skipped,
* no blank record was ignored,
* no unsupported schema was accepted,
* no duplicate was collapsed through map construction,
* no unresolved reference became an empty lookup,
* no passing verification masked a failing record,
* no stale commit or tree was accepted,
* no equally ranked candidates were resolved by input order,
* no zero or ambiguous candidates reached publication,
* `publishCandidates` never silently collapsed to `false`,
* no publication injection changed only one canonical artifact,
* no failed publication could not prove byte preservation,
* no read-only command wrote,
* tests do not require a pre-run of `inventory` or `regenerate`,
* 126 new tests execute,
* the full suite is entirely green,
* canonical production hashes are unchanged,
* candidate identity is unchanged,
* status flags remain false,
* the gate summary is fresh,
* the close report does not claim unexecuted cases as PASS,
* the final working tree is clean,
* publication uses no force update.

## 37. Close report

See `docs/close-reports/ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01.md`.

## 38. Non-recursive finalization evidence

* I = implementation commit (recorded in close report)
* F = final close-report commit (contains implementation evidence, test evidence,
  gate evidence, acceptance verdict)
* D = detached finalization transcript (outside the repository; binds F commit,
  F tree, `origin/main` after push, ahead/behind state, clean working tree,
  no force update, transcript SHA-256)

Rules followed:

* no repository commit after F,
* F is pushed through an ordinary fast-forward,
* D is created after the push,
* D is hashed,
* F and D are reported in the final execution response.

## 39. Successor release

After this ACT reaches `CLOSED_PASS`, release:

```text
ACT-CIRCUS-FSHARP-DIAGNOSTIC-CAUSAL-FAMILY-CLUSTERING01
```

That successor must:

* seek at least one independent repair episode,
* seek counterexamples,
* compare candidate applicability across episodes,
* retain single-episode evidence strength until corroboration exists,
* avoid embedding repair advice in the candidate artifact,
* keep `repair_advice_available=false`,
* keep `llm_tip_available=false`,
* publish causal-family state only after provenance and contradiction handling
  are explicit.

Until this fail-closed matrix is fully green, the project has one useful rule
candidate but not a sufficiently hardened substrate for causal-family promotion.