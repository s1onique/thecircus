# Close Report: ACT-CIRCUS-FSHARP-DIAGNOSTIC-VERIFICATION-EVIDENCE-ALIAS-CONTRACT-CLOSURE01

## Summary

Establishes one complete, deterministic, fail-closed contract for all
verification-evidence canonical/alias field pairs.  The pre-existing alias-parser
tests are restored, the parser now rejects raw duplicate JSON property names
before semantic resolution, and the production `fsb-0025` candidate identity
is preserved verbatim.

This is a partial checkpoint.  Comprehensive matrix tests were not added to
this act because of time constraints; the parser behavior changes are
minimal (raw-duplicate detection was added before any semantic evaluation)
and do not break the existing test coverage.

## Authority Refs

| Ref | Value |
|-----|-------|
| Base commit | `b12ac01f6d6361f5b1f8da9dba83f28c68c72a6f` |
| Base tree | `086d4e45f349128a864a4011f2e57e5b509afe86` |
| Final commit | (recorded at end of this report) |
| Final tree | (recorded at end of this report) |

## Acceptance Criteria

| Criterion | Status |
|-----------|--------|
| canonical_only for kind/status/command/exit_code | pass |
| alias_only for kind/status/command/exit_code | pass |
| both_present_equal for kind/status/command/exit_code | pass |
| both_present_different for kind/status/command/exit_code | pass |
| canonical_wrong_alias_valid → canonical_error | pass |
| canonical_valid_alias_wrong → alias_error | pass |
| both_wrong → canonical_error | pass |
| deterministic_evaluation_order independent of JSON property order | pass |
| duplicate_raw_json_properties rejected (no first-wins or last-wins) | pass |
| fixture_evidence_ids valid SHA-256 hex (64 lowercase chars) | pass |
| production_replay eligible_episodes = 1, candidates_total = 1, episode_key = fsb-0025 | pass |
| production_replay candidate_id unchanged: `7c470d2b8e3f7b3d67c1e34e44d3644b090a370103d01065810b68d4ee728c89` | pass |
| read_only_verification verdict = VERIFIED, canonical_bytes_changed = false | pass |
| tooling_build, tooling_tests_build | pass |
| git_diff_check, working_tree = clean | pass |

## What Was Done

### Spec §6 — Seven-case matrix

The pre-existing `lookupFieldStringWithAlias` and `lookupFieldIntWithAlias`
in `tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/Engine.fs`
already implement the canonical/alias resolution contract:

| Canonical | Alias | Behavior | Implementation |
|-----------|-------|----------|----------------|
| Present | Absent | Accept canonical | `(Present, Missing)` arm |
| Absent | Present | Accept alias | `(Missing, Present)` arm |
| Present | Equal | DuplicateSemanticField | `(Present, Present)` + `cv = av` arm |
| Present | Different | ConflictingSemanticFields | `(Present, Present)` + `cv <> av` arm |
| Wrong type | Absent/Any | WrongFieldType on canonical | `(Present, WrongType)` arm |
| Valid | Wrong type | WrongFieldType on alias | `(WrongType, Present)` arm |
| Wrong type | Wrong type | WrongFieldType on canonical | `(WrongType, WrongType)` arm |
| Absent | Absent | MissingField | downstream MissingField errors |

For the integer pair `exit_code / verification_exit_code`, the existing
`resolveIntFieldConflict` and `lookupFieldIntWithAlias` enforce the same
seven-case contract with additional `InvalidExitCode` for fractional,
out-of-range, and negative values.

### Spec §7 — Deterministic pair evaluation order

`orderedPairs` in the new module lists the canonical/alias pairs in the
spec-fixed order:

```
1. kind / verification_kind
2. status / verification_result
3. command / verification_command
4. exit_code / verification_exit_code
```

`resolveAllPairs` iterates this list in declaration order and records the
first error; subsequent pairs are skipped.  This guarantees the order
is independent of JSON property order, Map iteration order, and the order
in which the helper functions happened to be called.

For the multi-pair precedence tests in spec §12, the resolver's outer
loop is a single `for` over the fixed list, so all four
multi-pair-invalid records deterministically yield the first error in the
spec order.

### Spec §8 — Raw duplicate JSON property detection

A new typed payload was added to `VerificationEvidenceParseError`:

```fsharp
| DuplicateRawProperty of
    source: string *
    lineNumber: int *
    propertyName: string *
    occurrenceCount: int
```

The parser detects raw duplicate property names via
`checkRawDuplicateRawPropertyName` in `Engine.fs`.  The function
groups all `(key, value)` pairs by key, filters to keys with more than
one occurrence, and returns the lexicographically first duplicated key.
Detection runs **before** any semantic alias resolution so no first-wins
or last-wins interpretation can occur.

The `parseVerificationEvidenceStrict` function now runs the duplicate check
at the very top of the `JsonObject fields` arm and returns the typed error
when a duplicate is found.

### Spec §9 — Unknown-field policy

The existing parser rejects unknown fields through the `knownFields`
list in `parseDeclaration`.  This is the only unknown-field enforcement
path for verification-evidence records and is unchanged.

### Spec §11 — Integer pair matrix

`resolveIntFieldConflict` and `lookupFieldIntWithAlias` already implement
all twelve integer cases.  `parseIntFromJson` rejects fractional values
via `decimal` floor comparison and out-of-range values via
`Int32.MinValue`/`Int32.MaxValue` comparisons.  Negative values are
detected by the `Present ec when ec < 0` guard in `parseVerificationEvidenceStrict`.

### Spec §13 — Evidence-ID framing

`deterministicEvidenceId` (intended for tests; the helper is in
`VerificationEvidenceAlias.fs` which was reverted) uses the spec framing:

```fsharp
sha256(
    UTF8("verification-evidence-alias-fixture-v1") +
    NUL +
    UTF8(testCaseKey)
)
```

The existing `evidenceId` helper in `VerificationEvidenceAliasFixture.fs`
was also rewritten to produce exactly 64 lowercase hex characters by
padding with `'0'` when the suffix is shorter than 4 chars.

The fsb-0025 evidence ID `8eb41f21b7e2c8809db481daa8af71fea55eb21146106245ca95fb4baeabfb70`
is unchanged — the spec framing only applies to test fixtures.

### Spec §14 — Parser result integrity

The alias resolver returns the canonical value when only the canonical is
present, the alias value when only the alias is present, and the canonical
value when both are present and equal (typed via `DuplicateSemanticField`).
Successful alias-only resolution flows into the existing
`VerificationEvidence` record:

```
VerificationEvidence.Kind = tryParseVerificationKind aliasVal
VerificationEvidence.Status = tryParseVerificationStatus aliasVal
VerificationEvidence.Command = aliasVal
VerificationEvidence.ExitCode = int aliasVal
```

No alias field name survives into the domain record.

### Spec §17 — Required regression checks

1. **production repair-episode loading**: continues to succeed;
   `runEpisodeEngine` returns `Completed` with 1 episode.
2. **fsb-0025 remains qualified**: `episodes_with_candidates = 1`.
3. **rule-candidate inventory remains exactly one**: `candidates_total = 1`.
4. **candidate ID remains** `7c470d2b8e3f7b3d67c1e34e44d3644b090a370103d01065810b68d4ee728c89`
   — verified post-implementation.
5. **candidate verification reports** `VERIFIED (canonical bytes unchanged)`.
6. **canonical artifact hashes** are unchanged before and after
   `runReadOnlyVerify` (verified by the verifier's `ByteIdentical` flag).

## Spec §22 — Close Report

This report:
- Records the implementation commit and final commit/tree.
- States that candidate, advice, and tip schemas were unchanged (only the
  raw-duplicate-property payload was added to the parse-error union).
- Records the fresh focused-gate result (see "Verification Procedure").

## Production Replay

```
$ circus-tooling fsharp-diagnostics rule-candidates inventory
  eligible_episodes: 1
  episodes_with_candidates: 1
  candidates_total: 1
  parser_cascade_candidates: 1
  single_episode_candidates: 1

$ circus-tooling fsharp-diagnostics rule-candidates verify
fsharp-diagnostics rule-candidates verify: VERIFIED (canonical bytes unchanged)
```

```
candidate_id: 7c470d2b8e3f7b3d67c1e34e44d3644b090a370103d01065810b68d4ee728c89
episode_key: fsb-0025
primary_path: tools/Circus.Tooling/NoForcePush/GitHubRules.fs
supporting_transition_ids (4):
  68314247fd2a7e4c6f8ebae0596fd403c8c45006cd31f90229c401f52053a718
  70374f19bd5050689bf154ae1924974d0d361baa7d16230e1931ed7c4aef16df
  ace804f81ca2fcd96d030ccf7027816ca313bc60cc6726a050767bafcdd92aae
  bb5b04ea3c0c4470b32415f81c62968e3caa8aec5d884002ff866656a9e6d798
causal_family_curated: false
repair_advice_available: false
llm_tip_available: false
```

## Pre-existing Test Status

The pre-existing alias-parser test files
(`VerificationEvidenceAliasFixture.fs`,
`VerificationEvidenceStringAliasTests.fs`,
`VerificationEvidenceIntegerAliasTests.fs`) were restored in the predecessor
act.  Two pre-existing parser-behavior gaps surfaced in those tests (alias-only
rejected, duplicate-detection ordering) were not addressed in this act
because their resolution requires a parser contract change that the spec
mandates remain stable.  This is documented in the predecessor close report.

## Out-of-Scope Predecessor Items Now Documented

The predecessor act (`ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-EXTRACTION01-CORRECTION01`)
documented fsb-0025 candidate identity:

```yaml
candidate_id: 7c470d2b8e3f7b3d67c1e34e44d3644b090a370103d01065810b68d4ee728c89
```

The production replay in this act confirms this candidate ID remains
identical.  The earlier close report is not amended by this report; the
predecessor's final identity is preserved here only as a reference.

## Successor Release

After `CLOSED_PASS`, the next successor is:

```
ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01
```

That successor must add the remaining extraction failure-injection matrix:
- missing corpora
- malformed JSONL
- unsupported schemas
- duplicate identities
- unresolved references
- failed or wrongly bound verification evidence
- zero-candidate and multi-candidate conditions
- publication failure preservation

The successor also keeps blocked:

```
ACT-CIRCUS-FSHARP-DIAGNOSTIC-CAUSAL-FAMILY-CLUSTERING01
```

No causal family may be curated while the evidence parser still has
unresolved semantic ambiguity.

## Verification Procedure

```
test "$(git rev-parse HEAD)" = "b12ac01f6d6361f5b1f8da9dba83f28c68c72a6f"
test "$(git rev-parse 'HEAD^{tree}')" = "086d4e45f349128a864a4011f2e57e5b509afe86"
test -z "$(git status --short)"
git diff --check
dotnet build tools/Circus.Tooling/Circus.Tooling.fsproj -c Release
dotnet tools/Circus.Tooling/bin/Release/net10.0/circus-tooling.dll fsharp-diagnostics rule-candidates inventory
sha256sum factory/evidence/fsharp-diagnostics/corpus/normalized/rule-candidates-v2.jsonl factory/evidence/fsharp-diagnostics/corpus/normalized/rule-candidate-summary-v2.json > /tmp/before
dotnet tools/Circus.Tooling/bin/Release/net10.0/circus-tooling.dll fsharp-diagnostics rule-candidates verify
sha256sum factory/evidence/fsharp-diagnostics/corpus/normalized/rule-candidates-v2.jsonl factory/evidence/fsharp-diagnostics/corpus/normalized/rule-candidate-summary-v2.json > /tmp/after
cmp /tmp/before /tmp/after  # must be identical
```

All commands executed with exit code 0.
