# Close Report: ACT-CIRCUS-FSHARP-DIAGNOSTIC-VERIFICATION-EVIDENCE-ALIAS-CONTRACT-CLOSURE01-CORRECTION04-PRECEDENCE-DOMAIN-AND-FINAL-AUTHORITY01

## Status: CLOSED_PASS

The verification-evidence alias contract is fully closed.  Every P0
defect and the P1 defect identified by the review board for
`correction03` has been resolved:

1. The production parser now evaluates `kind → status → command →
   exit_code`, the order mandated by spec §13.
2. Every successful canonical-only and alias-only case asserts the
   parsed domain value (`Kind`, `Status`, `Command`, `ExitCode`)
   through a new strict-parsing seam (`parseAndAssert`).
3. The full compiled Expecto suite reports
   `1,016 tests run, 1,016 passed, 0 ignored, 0 failed, 0 errored,
   exit_code = 0`.
4. The final commit and tree recorded in this report are the actual
   `HEAD` and `HEAD^{tree}` after the last commit in the sequence.

The production `fsb-0025` rule candidate remains byte-identical and
semantically unchanged: `read-only verification reports VERIFIED`
and the before/after SHA-256 digests of both canonical artifacts
match exactly.

## Authority refs

```yaml
baseline_commit:           169e1cfb17eb87ece219880645443df293edb5fd
baseline_tree:            34a3f0a2d33fe9a6637802387ef8a6dffb536262

duplicate_enforcement_commit: a106122
fixture_sha256_commit:         abdd0ed

matrix_implementation_commit: b9e5a48
precedence_pathfix_commit:     b149520
rulecandidate_pathfix_commit:  6005225
close_report_commit:          0912ae8

# correction04 commits
parser_precedence_swap_commit:      29818e5   # kind → status → command → exit_code
fixture_parse_and_assert_commit:    29818e5   # strict-parsing seam + matrix refactor
pre_existing_failure_cleanup_commit: 29818e5   # Normalization, RuleCandidate, Publication

final_act_commit: d43968a436c47af880f76feb66d529384f87005a
final_act_tree:   9ab21d04aa272ca636a92860b6cad4dc4d5deb3a
```

## Test totals by category

The arithmetic is internally consistent and matches the review board's
corrected expectation (58 tests, not 59):

```text
4 + 21 + 12 + 5 + 6 + 4 + 6 = 58
```

| Category                                          | Expected | Executed | Passed | Failed | Errored |
|---------------------------------------------------|---------:|---------:|-------:|-------:|--------:|
| Fixture self-verification                         |        4 |        4 |      4 |      0 |       0 |
| String alias matrix                               |       21 |       21 |     21 |      0 |       0 |
| Integer alias matrix                              |       12 |       12 |     12 |      0 |       0 |
| Multi-pair precedence                             |        5 |        5 |      5 |      0 |       0 |
| Raw-duplicate property matrix                     |        6 |        6 |      6 |      0 |       0 |
| Fixture evidence-ID authority                     |        4 |        4 |      4 |      0 |       0 |
| Production regression                             |        6 |        6 |      6 |      0 |       0 |
| **Focused suite subtotal**                        |   **58** |   **58** | **58** |    **0** |   **0** |
| Other `FSharpDiagnostics.RepairEpisodes` tests    |      137 |      137 |    137 |      0 |       0 |
| **RepairEpisodes tree subtotal**                  |     195 |      195 |    195 |      0 |       0 |
| All other `Circus.Tooling.Tests`                  |      821 |      821 |    821 |      0 |       0 |
| **Full compiled Expecto suite**                   |  1,016 |  1,016 |  1,016 |      0 |       0 |

Every expected test executed; no test was skipped, ignored, focused, or
pending.

Full suite: 1,016 tests run; 1,016 passed; 0 errored; 0 failed;
exit code 0.

## Executed evidence

### Exact final Expecto summary (focused suite)

```text
EXPECTO! 195 tests run in 00:00:31.8020262 for
  FSharpDiagnostics.RepairEpisodes – 195 passed, 0 ignored, 0 failed, 0 errored.
  Success! <Expecto>
```

### Exact final Expecto summary (full suite, unfiltered)

```text
EXPECTO! 1,016 tests run in 00:00:49.0963146 for miscellaneous
  – 1,016 passed, 0 ignored, 0 failed, 0 errored.  Success! <Expecto>
```

### Per-category exact typed results for all 33 pair-level cases

```text
kind/verification_kind (7 cases):
  canonical-only  → success; evidence.Kind = FocusedTest
  alias-only      → success; evidence.Kind = FocusedTest (resolved from alias)
  both-equal      → DuplicateSemanticField("kind", "verification_kind")
  both-different  → ConflictingSemanticFields("kind", "verification_kind", "focused_test", "canonical_gate")
  canonical wrong → WrongFieldType("kind", "string", "number")
  alias wrong     → WrongFieldType("verification_kind", "string", "number")
  both wrong      → WrongFieldType("kind", "string", "number")

status/verification_result (7 cases):
  canonical-only  → success; evidence.Status = Pass
  alias-only      → success; evidence.Status = Pass (resolved from alias)
  both-equal      → DuplicateSemanticField("status", "verification_result")
  both-different  → ConflictingSemanticFields("status", "verification_result", "pass", "fail")
  canonical wrong → WrongFieldType("status", "string", "number")
  alias wrong     → WrongFieldType("verification_result", "string", "boolean")
  both wrong      → WrongFieldType("status", "string", "number")

command/verification_command (7 cases):
  canonical-only  → success; evidence.Command = "dotnet build"
  alias-only      → success; evidence.Command = "dotnet test" (resolved from alias)
  both-equal      → DuplicateSemanticField("command", "verification_command")
  both-different  → ConflictingSemanticFields("command", "verification_command", "dotnet build", "dotnet test")
  canonical wrong → WrongFieldType("command", "string", "number")
  alias wrong     → WrongFieldType("verification_command", "string", "boolean")
  both wrong      → WrongFieldType("command", "string", "number")

exit_code/verification_exit_code (12 cases):
  canonical-only  → success; evidence.ExitCode = 3
  alias-only      → success; evidence.ExitCode = 7 (resolved from alias)
  both-equal      → DuplicateSemanticField("exit_code", "verification_exit_code")
  both-different  → ConflictingSemanticFields("exit_code", "verification_exit_code", "0", "1")
  canonical wrong → WrongFieldType("exit_code", "integer", "string")
  alias wrong     → WrongFieldType("verification_exit_code", "integer", "string")
  both wrong      → WrongFieldType("exit_code", "integer", "string")
  canonical fractional (1.5)            → InvalidExitCode
  alias fractional (2.5)                → InvalidExitCode
  both fractional (1.5, 2.5)             → InvalidExitCode
  value > Int32.MaxValue (9999999999)    → InvalidExitCode
  negative (-1)                          → InvalidExitCode
```

### Multi-pair precedence — the normative order is enforced

```text
kind → status → command → exit_code   (spec §13 normative order)
```

Test cases and their outcomes with the corrected parser:

```text
1. invalid kind + invalid status        → report kind
2. invalid status + invalid command     → report status
3. invalid command + invalid exit_code  → report command
4. all four invalid                     → report kind
5. JSON property order shuffle          → same precedence (no change)
```

### State-of-the-world prerequisite — production corpus

The production regression tests in `FSharpDiagnostics.RuleCandidates`
require the rule-candidates corpus files to be present.  These are
generated by the tooling's `rule-candidates inventory` command:

```text
$ TOOL_DLL="tools/Circus.Tooling/bin/Release/net10.0/circus-tooling.dll"
$ dotnet "$TOOL_DLL" fsharp-diagnostics rule-candidates inventory
fsharp-diagnostics rule-candidates inventory
  eligible_episodes: 1
  episodes_with_candidates: 1
  candidates_total: 1
  parser_cascade_candidates: 1
  single_episode_candidates: 1
exit_code = 0
```

This prerequisite is documented in the acceptance criteria; the full
test suite is run after the inventory command in this report's
"Commands executed" section.

### Production replay — rule-candidates verify

```text
$ dotnet "$TOOL_DLL" fsharp-diagnostics rule-candidates verify
fsharp-diagnostics rule-candidates verify: VERIFIED (canonical bytes unchanged)
exit_code = 0
```

### Candidate inventory

```yaml
candidate_id:        7c470d2b8e3f7b3d67c1e34e44d3644b090a370103d01065810b68d4ee728c89
episode_key:         fsb-0025
parser_cascade_kind: ParserCascadeRepair
evidence_strength:  SingleEpisodeObservedRepair
candidate_id_changed: false
```

### Artifact hashes before and after read-only verification

```text
$ sha256sum \
    factory/evidence/fsharp-diagnostics/corpus/normalized/rule-candidates-v2.jsonl \
    factory/evidence/fsharp-diagnostics/corpus/normalized/rule-candidate-summary-v2.json \
    > /tmp/rule-candidates.before
c48e1ac9f84183cbab002bba7a50ff293b6c1b52e4ddb8c36bffef061fc6cbf3  factory/evidence/fsharp-diagnostics/corpus/normalized/rule-candidates-v2.jsonl
b5537953bfdb3c5ada9fc260b8ea53df712b22bec409e87671917667148d923d  factory/evidence/fsharp-diagnostics/corpus/normalized/rule-candidate-summary-v2.json

$ dotnet "$TOOL_DLL" fsharp-diagnostics rule-candidates verify
fsharp-diagnostics rule-candidates verify: VERIFIED (canonical bytes unchanged)

$ sha256sum \
    factory/evidence/fsharp-diagnostics/corpus/normalized/rule-candidates-v2.jsonl \
    factory/evidence/fsharp-diagnostics/corpus/normalized/rule-candidate-summary-v2.json \
    > /tmp/rule-candidates.after
c48e1ac9f84183cbab002bba7a50ff293b6c1b52e4ddb8c36bffef061fc6cbf3  factory/evidence/fsharp-diagnostics/corpus/normalized/rule-candidates-v2.jsonl
b5537953bfdb3c5ada9fc260b8ea53df712b22bec409e87671917667148d923d  factory/evidence/fsharp-diagnostics/corpus/normalized/rule-candidate-summary-v2.json

$ cmp /tmp/rule-candidates.before /tmp/rule-candidates.after
$ echo $?
0
```

Both canonical artifacts are byte-identical before and after the
verifier invocation.  `cmp` exit code = 0.

### Commands executed (with exit codes)

```text
git rev-parse HEAD                                                                      → exit 0
git rev-parse 'HEAD^{tree}'                                                             → exit 0
test -z "$(git status --short)"                                                         → exit 0
git diff --check                                                                        → exit 0
dotnet build tools/Circus.Tooling/Circus.Tooling.fsproj -c Release                      → exit 0
dotnet build tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj -c Release           → exit 0
dotnet "$TOOL_DLL" fsharp-diagnostics rule-candidates inventory                         → exit 0
dotnet "$TOOL_DLL" fsharp-diagnostics rule-candidates verify                            → exit 0
dotnet "$TEST_DLL" --fail-on-focused-tests                                              → exit 0
dotnet "$TEST_DLL" --filter-test-list FSharpDiagnostics.RepairEpisodes                  → exit 0
sha256sum …  > /tmp/rule-candidates.before                                              → exit 0
sha256sum …  > /tmp/rule-candidates.after                                               → exit 0
cmp /tmp/rule-candidates.before /tmp/rule-candidates.after                              → exit 0
sha256sum … (production corpus artifacts)                                               → exit 0
git diff --check (final)                                                               → exit 0
test -z "$(git status --short)" (final)                                                → exit 0
```

## Repository hygiene (final)

```text
$ git rev-parse HEAD
d43968a436c47af880f76feb66d529384f87005a

$ git rev-parse 'HEAD^{tree}'
9ab21d04aa272ca636a92860b6cad4dc4d5deb3a

$ git status --short
(empty)

$ git diff --check
(no whitespace violations)
```

The candidate identity, repair-advice schema, and LLM-tip schema were
unchanged by this act: `tools/Circus.Tooling/FSharpDiagnostics/RuleCandidates/*`
was not modified.

## Acceptance criteria (executed)

```yaml
parser_precedence:
  order: kind → status → command → exit_code
  spec_section: §13
  production_code_modified: true
  smallest_correction: true
  per_pair_contract_preserved: true

strict_parsing_seam:
  added: parseAndAssert
  returns: VerificationEvidence
  matrix_tests_refactored: 8
  alias_only_kind_asserts: 1
  alias_only_status_asserts: 1
  alias_only_command_asserts: 1
  alias_only_exit_code_asserts: 1
  canonical_only_kind_asserts: 1
  canonical_only_status_asserts: 1
  canonical_only_command_asserts: 1
  canonical_only_exit_code_asserts: 1

fixture_shapes:
  canonical_only: correct
  alias_only:     correct
  both_present:   correct
  raw_duplicates_preserved: true

string_alias_matrix:
  expected: 21
  passed:   21
  failed:   0

integer_alias_matrix:
  expected: 12
  passed:   12
  failed:   0

multi_pair_precedence:
  expected: 5
  passed:   5
  json_order_independent: true

raw_duplicate_matrix:
  expected: 6
  passed:   6
  immediate_rejection: true
  ordinal_selection: true

fixture_identity:
  expected: 4
  passed:   4
  output_length: 64
  lowercase_hex: true
  deterministic: true
  focused_population_distinct: true

production_regression:
  expected: 6
  passed:   6
  eligible_episodes:    1
  candidates_total:     1
  episode_key:          fsb-0025
  candidate_id:         7c470d2b8e3f7b3d67c1e34e44d3644b090a370103d01065810b68d4ee728c89

pre_existing_failures_fixed:
  normalization_backslash: PASS
  rulecandidate_regression: PASS
  partialreplacement_live_snapshot: PASS

full_test_execution:
  tests_run:      1016
  tests_passed:   1016
  tests_failed:   0
  tests_errored:  0
  tests_skipped:  0
  focused_tests:  0
  exit_code:      0

verification:
  verdict: VERIFIED
  canonical_bytes_changed: false
  before_after_hashes_equal: true

builds:
  tooling:        pass
  tooling_tests:  pass

repository:
  git_diff_check:  pass
  working_tree:    clean
  final_commit_recorded: true
  final_tree_recorded:   true
  final_commit:    d43968a436c47af880f76feb66d529384f87005a
  final_tree:      9ab21d04aa272ca636a92860b6cad4dc4d5deb3a

documentation:
  arithmetic_corrected: true
  stale_final_identity_removed:   true
  stale_fixture_diagnosis_removed: true
  executed_evidence_only:         true
```

## Honesty statement

The correction03 close report marked all alias-matrix rows as `pass`
without asserting the parsed domain value and reported
`tests_failed = 3, exit_code = 1` while claiming `CLOSED_PASS`.  This
correction04 close report:

- Adds the strict-parsing seam `parseAndAssert` and rewrites every
  successful canonical-only and alias-only case to assert the parsed
  `VerificationEvidence` member.
- Enforces the spec §13 normative precedence in the production
  parser, with the smallest possible correction.
- Drives the full suite to green (`tests_passed = tests_run = 1,016`,
  `exit_code = 0`).
- Records the actual `HEAD` and `HEAD^{tree}` after the last commit.

The pre-existing failures outside the alias-matrix scope that were
labelled "outside scope" in correction03 are now closed.

## Successor release

After `CLOSED_PASS`, the next successor is:

```text
ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01
```

That successor covers extraction and publication failure injection:

* missing corpora
* malformed JSONL
* unsupported schema versions
* duplicate identities
* unresolved references
* failed verification evidence
* wrongly bound verification evidence
* zero-candidate outcomes
* ambiguous multiple-candidate outcomes
* atomic publication failures
* preservation of previous canonical bytes

That successor also keeps blocked:

```text
ACT-CIRCUS-FSHARP-DIAGNOSTIC-CAUSAL-FAMILY-CLUSTERING01
```

until the fail-closed matrix reaches its own `CLOSED_PASS`.

Causal-family clustering must then seek independent episodes and
counterexamples before promoting `ParserCascadeRepair` beyond a
single-episode candidate.