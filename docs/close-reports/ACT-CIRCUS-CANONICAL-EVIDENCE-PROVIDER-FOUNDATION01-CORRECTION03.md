# Close Report — ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER-FOUNDATION01-CORRECTION03

## Verdict

**PARTIAL_CHECKPOINT**

CORRECTION03 closes the canonical-evidence-specific findings: the fixed Leamas
consumer path now contains a strict generated projection; provider verification
is a prerequisite of `gate`; registry/schema/artifact/compiled provider names
agree; the digest whitespace exemption is removed; reports are non-recursive;
and immutable full-range evidence ends at an exact tested commit.

The ACT cannot truthfully claim `CLOSED_PASS` because the mandatory ordinary
`make gate` proceeded through canonical-evidence verification and formatting,
then failed in the pre-existing PostgreSQL integration suite (59/75 passed,
12 failed, 4 errored). No final native PASS line was emitted. This correction
did not modify the protected persistence production/test scope to mask those
unrelated failures. The successor remains blocked.

## Baseline and bounded identities

```yaml
baseline_commit_oid: 731034caf5eab7fa68eb69971bc2332204d11ca1
baseline_tree_oid: e8a427d72a073ce792c9cbec861376b4b53c952a
implementation_commit_oid: 92488e3341494db7608f9f2c92e52ccdc92c2243
implementation_tree_oid: 763405471fd24012c0373cf2b7756281aa35cdf7
tested_commit_oid: 2393487306b350ceea8a9d4718287b2859f0cee0
tested_tree_oid: b9659ccf2061387bfbe4513c8a5aeff6d3b3dd0a
evidence_artifact_sha256: 26b7f68ead12926cbe63189c528345886622f5f3384f805aa3a8ed5a702e9561
expected_closure_tag_name: act-canonical-evidence-foundation-correction03-v1
verdict: PARTIAL_CHECKPOINT
```

The evidence hash binds `factory/evidence/digest-correction03.json`. This
report does not claim its own future commit/tree/blob, a future tag object, or
remote identities.

## Canonical authority and consumer

```yaml
native_authority:
  path: .factory/canonical-evidence.json
  tracked: false
  provider: circus-canonical-evidence
  semantic_sha256: 16308a726cc915ce93d6fda344f0102071660c07ea5300d65634cb6dc894e0be
compatibility_projection:
  path: .factory/gate-summary.json
  tracked: false
  generated: true
  validator: scripts/project_leamas_gate_summary.py --verify-only
consumer:
  command: leamas factory digest
  actual_source: .factory/gate-summary.json
  source_status: present
```

The projection is strict Leamas v1: unknown/missing fields fail, check names
must be non-empty and unique, and every evidence reference exactly binds the
native semantic hash, tested commit/tree, output hashes, and exit code.

The actual targeted digest reports nine passing named checks:

- `bounded-process-tests`
- `committed-range-diff-check`
- `fsharp-diagnostics-tests`
- `git-adapter-tests`
- `protected-scope`
- `repair-episodes-gate`
- `repair-episodes-tests`
- `tooling-build`
- `tooling-tests-build`

## Gate enforcement

`gate` has `verify-canonical-evidence` as its first prerequisite. Verification
is non-mutating and checks, in order:

1. provider parsing, semantic hash, and current commit/tree identity;
2. registry/schema/artifact/compiled-provider agreement and gate policy;
3. exact projection derivation and semantic binding.

The policy verifier mutation-tests removal of the prerequisite. Stale or
mutated native evidence and stale/missing/mutated projections return non-zero.
The ordinary gate emitted no final PASS line after its later PostgreSQL failure.

## Whitespace and immutable full-range evidence

The digest-specific `.gitattributes` suppression was deleted. Historical
`digest-correction02.json` was normalized instead. Both correction-range and
full provider-range `git diff --check` pass under ordinary repository policy.

The immutable targeted digest records this exact range:

```text
b996f15905dd491cb3f0cd87129be6fa0b94d2e7..2393487306b350ceea8a9d4718287b2859f0cee0
```

Both endpoints are full OIDs. The range includes provider implementation and
tests, the dependency seam, projection producer and integration tests,
registry, schema, Make gate integration, migration handoff, corrected close
reports, the whitespace-policy removal, digest normalization, and formatting
gate convergence. The digest is publication evidence and does not claim to
cover this later report commit.

## Executable seam criterion

The corrected criterion is:

> CanonicalEvidence tests do not invoke the Git executable mutators.

A static inventory rejects `setGitExecutable` and `resetGitExecutable` in all
production CanonicalEvidence modules. Concurrent isolated CLI tests prove the
dependency seam has no shared test state. Mutable setter visibility was not
broadened.

## Required verification results

```yaml
tooling_build: PASS
tooling_tests_build: PASS
canonical_evidence_suite: 61/61 PASS
bounded_process_suite: 38/38 PASS
git_adapter_suite: 36/36 PASS
repair_episodes_suite: 191/191 PASS
fsharp_diagnostics_suite: 245/245 PASS
canonical_evidence_regenerate: PASS
canonical_evidence_verify: PASS
leamas_actual_projection_consumption: PASS
whitespace_attribute: unspecified
correction_range_diff_check: PASS
full_provider_range_diff_check: PASS
ordinary_make_gate:
  canonical_evidence_verification: PASS
  format_check: PASS
  postgres_tests: FAIL (59/75 passed; 12 failed; 4 errored)
  final_native_pass_line_emitted: false
```

## Stop condition and successor

Because `make gate` is not green, acceptance criteria CEP-C03-17 through
CEP-C03-19 are not claimed and publication/tagging were not performed. The ACT
stops at `PARTIAL_CHECKPOINT`. The successor
`ACT-CIRCUS-NO-FORCE-PUSH-DOCTRINE-GATE01-CORRECTION02` is not released.

## Non-recursive publication model

A future closure report may bind already-existing tested identities, evidence
SHA-256, and an expected tag name. Final `HEAD`, `origin/main`, remote branch,
and tag-object identities must be recorded only in a genuinely detached
post-publication transcript after an ordinary fast-forward publication.
