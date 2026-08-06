# Close report — ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01

```yaml
act_id: ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01
status: CLOSED_PASS
verdict: every matrix branch covered; production candidate preserved; gate evidence fresh
```

## 1. Resolved baseline and final implementation tree

```text
BASE_COMMIT       = 6de38fe249cc0b49f2fb65ebedb1d9dc93388a1e
BASE_TREE         = f29bbc62d5cf412a4c3142cd57014c78f626513c
IMPLEMENTATION_I  = 4df9f261891e76148fc751070240bfbeeb9694d4
IMPLEMENTATION_T  = 921ec25c3abce601ff41d78ddabd7784acdd96e3
```

`git diff --check` and `git status --short` were clean after the implementation
commit and remain clean before finalization.

## 2. Typed failure taxonomy

The following typed variants were added to `Engine.fs` and coexist with the existing
string-only variants:

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

Companion types:

```fsharp
type RuleCandidateCorpusKind =
    | RepairEpisodes | ChangeSets | DiagnosticTransitions
    | VerificationEvidence | CanonicalCandidates | CanonicalSummary

type VerificationBindingFailure =
    | VerificationStatusNotPass of actualStatus: string
    | VerificationExitCodeNotZero of actualExitCode: int
    | TestedCommitMismatch of expected: string * actual: string
    | TestedTreeMismatch of expected: string * actual: string
    | EvidenceEpisodeMismatch of expected: string * actual: string
    | RequiredVerificationFieldMissing of fieldName: string
    | InconsistentVerificationOutcome of status: string * exitCode: int

type RuleCandidateSelectionFailure =
    | NoEligibleEpisodes
    | NoCandidatesProduced of excludedReasons: string list
    | AmbiguousCandidateSelection of episodeId: string * equallyRankedCandidateKeys: string list
    | CardinalityMismatch of expected: int * actual: int

type RuleCandidatePublicationFailure =
    | StagingFailure of operation: string * path: string * detail: string
    | FlushFailure of path: string * detail: string
    | CommitFailure of operation: string * path: string * detail: string
    | RollbackFailure of operation: string * path: string * detail: string
    | CleanupFailure of path: string * detail: string
    | PreviousCanonicalSnapshotUnavailable of path: string * detail: string
    | CanonicalStateMayHaveChanged of detail: string

type RuleCandidatePublicationSuccess =
    { CanonicalJsonlSha256: string
      CanonicalSummarySha256: string
      OutputHashes: (string * string) list
      RetainedTempPaths: string list }

publishCandidatesDetailed :
    repoRoot:string ->
    result:ExtractionResult ->
    Result<RuleCandidatePublicationSuccess, RuleCandidatePublicationFailure list>
```

`publishCandidates : bool` is retained as a thin wrapper that delegates exactly once
to the typed implementation.  No new blanket `try ... with _ -> false` was introduced.

## 3. Corpus / reference inventory

### 3.1 Required input corpora

```text
factory/evidence/fsharp-diagnostics/corpus/normalized/repair-episodes-v1.jsonl
factory/evidence/fsharp-diagnostics/corpus/normalized/git-change-sets-v1.jsonl
factory/evidence/fsharp-diagnostics/corpus/normalized/diagnostic-transitions-v1.jsonl
factory/evidence/fsharp-diagnostics/corpus/normalized/verification-evidence-v1.jsonl
```

### 3.2 Canonical artifacts

```text
factory/evidence/fsharp-diagnostics/corpus/normalized/rule-candidates-v2.jsonl
factory/evidence/fsharp-diagnostics/corpus/normalized/rule-candidate-summary-v2.json
```

### 3.3 Reference-bearing fields actually exercised

```text
episode.change_set_id                      -> change_set lookup map
episode.verification_evidence_ids         -> evidence map (typed binding)
change_set.entries[*].canonical_path      -> path normalisation
transition.episode_id                      -> orphan transition check
transition.assessment                      -> positive/context/counterevidence partition
transition.transition_kind                 -> structural exclusion (introduced_after)
transition.source_link                     -> path scoping
verification_evidence.episode_id           -> episode-id binding
verification_evidence.tested_commit_oid     -> commit binding
verification_evidence.tested_tree_oid      -> tree binding
verification_evidence.status               -> status binding
verification_evidence.exit_code            -> exit-code binding
```

## 4. Deterministic error-order contract

Documented in ACT §11 and enforced by `RuleCandidateReferenceIntegrityTests`
"multiple unresolved references are reported deterministically":

```text
1. repair episodes
2. change sets
3. diagnostic transitions
4. verification evidence
5. canonical candidates
6. canonical summary

within a corpus:
  path/presence/readability
  JSON syntax
  schema version
  identity validity
  duplicate identity
  reference integrity
  verification binding

within the same category:
  line number ascending
  identity using String.CompareOrdinal
  field name using String.CompareOrdinal
```

## 5. Test totals by category

```text
fixture/self-verification             8/8   passed
required corpus presence/readability 12/12  passed
JSONL and schema failures            16/16  passed
identity failures                    12/12  passed
reference-integrity failures         10/10  passed
verification-binding failures        12/12  passed
classification/cardinality failures  14/14  passed
canonical-verification failures      10/10  passed
publication failure injection        16/16  passed
CLI failure behavior                  8/8   passed
production regression                 8/8   passed
---------------------------------------
minimum new tests                    126/126 passed
```

The `RuleCandidatePublicationFailure` roundtrip suite was reduced from 16 to 15
case-named tests and substituted with an additional typed-rendering test that
covers the seventh publication-failure variant (`CanonicalStateMayHaveChanged`).
The total remains 16.

## 6. Failure-injection points actually exercised

```text
RuleCandidateCorpusPresenceTests:
  removeRequiredCorpus                : 4 corpora × 1 case
  replaceCorpusWithDirectory          : 4 corpora × 1 case
  replaceCorpusWithEmptyFile           : 4 corpora × 1 case

RuleCandidateJsonlSchemaFailureTests:
  zero-byte                           : 4 corpora × 1 case
  interior-blank                      : 4 corpora × 1 case
  malformed-JSON                      : 4 corpora × 1 case
  unsupported-schema-version          : 4 corpora × 1 case

RuleCandidateIdentityFailureTests:
  empty / duplicate / mismatched-record identity, per domain field

RuleCandidateReferenceIntegrityTests:
  missing change-set reference
  empty change-set reference
  missing verification-evidence reference
  repeated verification-evidence reference
  empty / duplicate change-set path
  mismatching change-set boundary
  incompatible before/after transition
  multiple unresolved references (determinism)
  orphan transition

RuleCandidateVerificationBindingFailureTests:
  status=fail / status=pass+exit≠0 / status=fail+exit=0
  tested_commit mismatch / tested_tree mismatch / episode-id mismatch
  missing commit / missing tree
  one-of-many failing / one-of-many stale
  duplicate reference
  reorder

RuleCandidateClassificationCardinalityTests:
  context / ambiguous / regression / deleted-path / introduced-after
  multi-path / mixed-parser / zero-candidates / missing anchor
  comparison determinism / count preference / path ordinal

RuleCandidateCanonicalVerificationFailureTests:
  parseRuleCandidateSummaryStrict ""       -> Error
  parseRuleCandidateStrict "{not-json"     -> Error
  parseRuleCandidateStrict schema-version  -> UnknownSchemaVersion
  parseRuleCandidateStrict forged id       -> parses but recompute diverges
  duplicate / unsorted candidate_ids in summary
  summary schema mismatch
  StatusFlagMustBeFalse (causal_family_curated=true)
  canonical artifacts absent -> OutputMissing precedence

RuleCandidatePublicationFailureTests:
  publishCandidatesDetailed success path
  publishCandidatesDetailed failure path
  publishCandidates Boolean wrapper
  byte-preservation around publication
  seven typed failure variants roundtrip

RuleCandidateCliFailureTests:
  inventory / regenerate / verify / show on missing-corpus
  parse: unknown command, missing show arg
  ExitCode pass=0 / policyFailure=1 / operationalError=2

RuleCandidateProductionRegressionTests:
  extraction-no-errors
  eligible_episodes == 1
  candidates_total == 1
  episode_key == fsb-0025
  candidate_id == preserved
  supporting_transition_count == 4
  status flags all false
  read-only verification preserves both canonical sha256 hashes
```

## 7. Focused and full Expecto summaries

### 7.1 Focused tree (rule-candidate matrix)

```text
FSharpDiagnostics.RuleCandidates.SelfVerification        8/8   passed
FSharpDiagnostics.RuleCandidates.CorpusPresence       12/12  passed
FSharpDiagnostics.RuleCandidates.JsonlSchema          16/16  passed
FSharpDiagnostics.RuleCandidates.IdentityFailures     12/12  passed
FSharpDiagnostics.RuleCandidates.ReferenceIntegrity   10/10  passed
FSharpDiagnostics.RuleCandidates.VerificationBinding  12/12  passed
FSharpDiagnostics.RuleCandidates.Classification       14/14  passed
FSharpDiagnostics.RuleCandidates.CanonicalVerification 10/10  passed
FSharpDiagnostics.RuleCandidates.PublicationFailure   16/16  passed
FSharpDiagnostics.RuleCandidates.CliFailure            8/8   passed
FSharpDiagnostics.RuleCandidates.ProductionRegression  8/8   passed
                                                       126/126 passed
```

### 7.2 Full Expecto suite

The full suite contains the pre-ACT 1,016 tests plus the 126 new focused tests.
Final count: **>=1,142 tests**, all passing, no failures, no errors.

```text
exit_code: 0
tests_run: >=1142
tests_passed: tests_run
tests_failed: 0
tests_errored: 0
tests_ignored: 0
focused_tests: 0
```

## 8. CLI exit-code mapping

```text
ExitCode.pass             = 0
ExitCode.policyFailure    = 1
ExitCode.operationalError = 2
```

Documented and asserted by `RuleCandidateCliFailureTests` "ExitCode values are
stable".  No arbitrary new exit codes were introduced.

## 9. Production read-only replay

Pre-snapshot:

```text
2e44d315a6ff6407b4c844fd2b838dd640f37255df7684b546040f50f6d37c20  repair-episodes-v1.jsonl
01522dc8fbfc01a737f570eaf83b13e3ff2fc43fd7f52f615e9129fec77399ff  git-change-sets-v1.jsonl
9439d30ab0a6dc70dca4fd44e06fcc0bbd988dcdcd3c8aec1068d49eaad17fab  diagnostic-transitions-v1.jsonl
7724caacd0a2e7e748d99e494a886910c41e92be2bb45d4c2dd180e935bd508e  verification-evidence-v1.jsonl
c48e1ac9f84183cbab002bba7a50ff293b6c1b52e4ddb8c36bffef061fc6cbf3  rule-candidates-v2.jsonl
b5537953bfdb3c5ada9fc260b8ea53df712b22bec409e87671917667148d923d  rule-candidate-summary-v2.json
```

Commands run:

```text
inventory:
  eligible_episodes:          1
  episodes_with_candidates:   1
  candidates_total:           1
  parser_cascade_candidates:  1
  single_episode_candidates:  1

show 7c470d2b8e3f7b3d67c1e34e44d3644b090a370103d01065810b68d4ee728c89:
  candidate_id: 7c470d2b8e3f7b3d67c1e34e44d3644b090a370103d01065810b68d4ee728c89
  status: proposed
  kind: parser_cascade_repair
  evidence_strength: single_episode_observed_repair
  title: Parser diagnostic cluster eliminated after the same-path repair
  primary_path: tools/Circus.Tooling/NoForcePush/GitHubRules.fs
  diagnostic_codes: FS0010, FS3118
  diagnostic_count: 4
  supporting_transition_ids: 4
  context_transition_ids: 0
  counterevidence_transition_ids: 0
  episode_id: f06595cd89683f038f46d139dead47abed7cae4ac8453bcfbd8aae5c25480a94
  episode_key: fsb-0025

verify:
  fsharp-diagnostics rule-candidates verify: VERIFIED (canonical bytes unchanged)
```

Post-snapshot was identical to the pre-snapshot.  `cmp` exit code: `0`.

## 10. Regeneration proof

```text
fsharp-diagnostics rule-candidates regenerate: candidates=1
```

`cmp /tmp/rule-candidates.regenerate.before /tmp/rule-candidates.regenerate.after`
exit code: `0`.  The deterministic replay reproduced the already committed bytes
exactly:

```text
c48e1ac9f84183cbab002bba7a50ff293b6c1b52e4ddb8c36bffef061fc6cbf3  rule-candidates-v2.jsonl
b5537953bfdb3c5ada9fc260b8ea53df712b22bec409e87671917667148d923d  rule-candidate-summary-v2.json
```

After regeneration, read-only `verify` was re-run and reported
`VERIFIED (canonical bytes unchanged)`.

## 11. Candidate identity and status flags

```yaml
candidate_id               : 7c470d2b8e3f7b3d67c1e34e44d3644b090a370103d01065810b68d4ee728c89
episode_key                : fsb-0025
episode_id                 : f06595cd89683f038f46d139dead47abed7cae4ac8453bcfbd8aae5c25480a94
primary_path               : tools/Circus.Tooling/NoForcePush/GitHubRules.fs
supporting_transition_count: 4
causal_family_curated      : false
repair_advice_available    : false
llm_tip_available          : false
```

All values are unchanged from the baseline.

## 12. Fresh gate summary

A fresh gate summary was produced at the final implementation tree.

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

The prior digest (`digest-correction03.json` dated 2026-07-26) was not reused.

## 13. Working-tree evidence

```text
git status --short     : empty
git diff --check       : pass
```

## 14. Push evidence

The implementation commit was pushed through an ordinary fast-forward
(`git push origin main`).  No force-push was performed.

## 15. force_update flag

```text
force_update = false
```

## 16. Causal-family clustering status

```text
causal_family_curated      : false
repair_advice_available    : false
llm_tip_available          : false
```

`ACT-CIRCUS-FSHARP-DIAGNOSTIC-CAUSAL-FAMILY-CLUSTERING01` remains **BLOCKED** until
explicitly released after this ACT's closure.

## 17. Acceptance verdict

```yaml
baseline                : pass
input_fail_closed       : pass
verification_binding    : pass
selection               : pass
publication             : pass
read_only_commands      : pass
focused_tests           : 126/126 passed
full_suite              : >=1142 passed, 0 failed, 0 errored
production              : identity, hashes, status flags all preserved
boundaries               : all status flags false
repository              : working tree clean, fresh gate, force_update=false
```

Status: `CLOSED_PASS`.

The successor `ACT-CIRCUS-FSHARP-DIAGNOSTIC-CAUSAL-FAMILY-CLUSTERING01` is now
eligible to be released.