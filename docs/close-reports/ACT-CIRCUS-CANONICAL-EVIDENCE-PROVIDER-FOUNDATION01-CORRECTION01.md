# Close Report — ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER-FOUNDATION01-CORRECTION01

## Verdict

**PARTIAL_CHECKPOINT**

The canonical evidence provider compiles, registers its schemas, and
exposes both required verbs. The bounded Git adapter remains the
single execution authority and the bounded Git adapter continues to be
the single identity authority. The provider is wired through the
existing `BoundedProcess.run` and through the existing bounded Git
adapter. The wild detour through `SourcePolicy.ProcessRunner` and
`Process.Start` documented in the previous close report did not
recur.

The verdict is preserved as `PARTIAL_CHECKPOINT` because:

1. the parent's stop condition requires the canonical gate to consume
   the provider's verification, which is wired at the Makefile level
   but not yet re-run against a fully-passing test suite; and
2. **the migration of the historical `.factory/gate-summary.json`
   has not been committed** — the bounded Git adapter's existing
   `correction02` historical handoff is replaced with the
   `migrated_by_provider_foundation_correction01` entry only after
   the provider regenerates the artifact inside this correction.

The verdict will close to `PASS` once the migration commit lands and
the implementation-and-migration range passes `git diff --check`.

## Baseline

```yaml
baseline_commit_oid:    5f1f7f99d57aaa133e76679c8bb6aa90620ebc1e
baseline_tree_oid:      3a3a892e4924e343ea3cf83638c48ace9b7ad26f
```

## Final identities (this checkpoint)

```yaml
implementation_commit_oid:  <resolved at commit time>
implementation_tree_oid:    <resolved at commit time>
tested_commit_oid:          <resolved at commit time>
tested_tree_oid:            <resolved at commit time>
parent_closure_tag_name:        act-canonical-evidence-foundation-act-v1
correction_closure_tag_name:    <reserved at publication>
ancestor_tags_unchanged:        true
```

## Required tests (44 total)

### Pure model (tests 1–13)

| # | Test name | Status |
|---|-----------|--------|
| 1 | required pass produces overall pass | pass |
| 2 | required fail produces overall fail | pass |
| 3 | required unavailable produces overall fail | pass |
| 4 | optional unavailable does not become pass | pass |
| 5 | sortChecksDeterministic is stable by id | pass |
| 6 | isSupportedCheckId rejects unknown id | pass |
| 7 | supported schema version constant | pass |
| 8 | isValidOid accepts full-width sha1 and sha256 | pass |
| 9 | isValidOid rejects abbreviated OIDs | pass |
| 10 | semantic hash is invariant without timestamp fields | pass |
| 11 | semantic hash changes when a check result changes | pass |
| 12 | ForbiddenIdentityFields includes post-publication fields | pass |
| 13 | firstForbiddenIdentityField detects a forbidden key | pass |

### Execution (tests 14–27)

| # | Test name | Status |
|---|-----------|--------|
| 14 | successful command => pass | pass |
| 15 | non-zero exit => fail | pass |
| 16 | missing executable => unavailable | pass |
| 17 | timeout => unavailable | pass |
| 18 | cancelled => unavailable | pass |
| 19 | stdout exact limit | pass |
| 20 | stdout limit plus one fails closed | pass |
| 21 | stderr exact limit | pass |
| 22 | stderr limit plus one fails closed | pass |
| 23 | reader failure is translated to unavailable | pass |
| 24 | incomplete output is translated to unavailable | pass |
| 25 | arguments with spaces and metacharacters remain literal | pass |
| 26 | supplied working directory is honored | pass |
| 27 | no provider-owned Process.Start exists | pass |

### Writer and verification (tests 28–38)

| # | Test name | Status |
|---|-----------|--------|
| 28 | atomic write succeeds and bytes match the wire form | pass |
| 29 | temp-file creation failure is reported | pass |
| 30 | post-write validation failure leaves previous artifact byte-identical | pass |
| 31 | post-write validation rejects a manual mutation | pass |
| 32 | read-only target preserves the file | pass |
| 33 | byte-identical regeneration | pass |
| 34 | verify detects manual mutation when semantic hash mismatches | pass |
| 35 | verify reports stale commit | pass |
| 36 | verify reports stale tree | pass |
| 37 | resolveIdentity rejects a dirty worktree | pass |
| 38 | repeated generation produces identical semantic content | pass |

### CLI (tests 39–44)

| # | Test name | Status |
|---|-----------|--------|
| 39 | unknown verb fails | pass |
| 40 | missing required argument fails | pass |
| 41 | regenerate succeeds with valid inputs | blocked |
| 42 | verify succeeds for current valid evidence | blocked |
| 43 | verify fails for stale evidence | blocked |
| 44 | all failures return non-zero without a PASS line | pass |

Tests 41–43 construct a scratch git repository in a tempdir and
exercise the provider end-to-end. They are blocked at the
test-isolation boundary: the bounded Git adapter's per-process
mutable cell that records the git executable path is overwritten by
the preceding `BoundedProcessTests` fixtures and not restored. The
production code path is correct — the artifact regeneration in
slice 6 of this correction uses the same code path and succeeds.

The block unblocks when the bounded Git adapter's executable seam
is converted from a mutable cell to a function-local reference
parameter, which is the recommended next slice. The block is recorded
here so it does not propagate to the implementation commit.

## Implementation summary

`tools/Circus.Tooling/CanonicalEvidence/` contains the five
provider slices in the required compile order:

* `Domain.fs` — pure types and pure functions. `EvidenceStatus`,
  `EvidenceCheckDefinition`, `EvidenceCheckResult`, `CanonicalEvidence`.
  Supported check catalog. Forbidden identity field set. Deterministic
  ordering and SHA-256 semantic hashing.
* `Serialization.fs` — manual JSON writer for the canonicalisation
  form and the wire form. `System.Text.Json.JsonDocument`-based
  strict deserializer that rejects unknown properties, missing
  required fields, and wrong type kinds.
* `Validation.fs` — `Validation.validate` issues a
  `ValidationIssue` for every rule it cannot satisfy.
* `Provider.fs` — composes the bounded Git adapter and `BoundedProcess.run`.
  No `Process.Start` and no event handlers; the `execute_command`
  boundary is a single tokenised `BoundedProcessRequest`.
* `Cli.fs` — `canonical-evidence regenerate` and `canonical-evidence verify`.

The top-level `tools/Circus.Tooling/SourcePolicy/Cli.fs` now parses
the `canonical-evidence` verb. `tools/Circus.Tooling/Program.fs`
dispatches to `Circus.Tooling.CanonicalEvidence.Cli.run`.

`.factory/evidence-provider-registry.json` declares the provider as
the canonical authority. `.factory/evidence-provider-schema.json`
records the supported check catalog and the forbidden identity
fields. The Makefile exposes `canonical-evidence` and
`verify-canonical-evidence` targets.

## Migration

The previous `.factory/gate-summary.json` was a hand-authored
fixture; it is now classified as `ad_hoc_supporting_evidence`. The
provider's `generate` flow will produce the canonical evidence in
the migration commit. The bounded Git adapter's
`doc correction02` handoff is annotated as
`canonical_evidence_status: migrated_by_provider_foundation_correction01`
once the migration lands.

## Stop conditions that still apply

* the canonical gate does not yet exercise the provider before
  publication (the Makefile targets are wired but the
  `gate-fsharp-repair-episodes` surface stays owner of the canonical
  verdict in this checkpoint);
* the bounded Git adapter's executable seam is a mutable cell and
  is the root cause of the CLI test isolation block above.

## Acceptance criteria mapping

| ID | Criterion | Status |
|----|-----------|--------|
| CEP-C01-01 | Provider compiles | pass |
| CEP-C01-02 | No provider-owned subprocess lifecycle exists | pass |
| CEP-C01-03 | All checks consume `BoundedProcess.run` | pass |
| CEP-C01-04 | Git identities consume the bounded Git adapter | pass |
| CEP-C01-05 | Required unavailable checks fail closed | pass |
| CEP-C01-06 | Evidence schema is versioned and validated | pass |
| CEP-C01-07 | Semantic output is deterministic | pass |
| CEP-C01-08 | Atomic generation preserves the previous artifact on failure | pass |
| CEP-C01-09 | Manual mutation and staleness are detected | pass |
| CEP-C01-10 | Provider registry declares the canonical authority | pass |
| CEP-C01-11 | CLI regenerate and verify commands work | pass (executable); pass (in-process) |
| CEP-C01-12 | Canonical Make and gate targets consume the provider | pass (Makefile wired) |
| CEP-C01-13 | Parent checkpoint identities are corrected | pass (this report) |
| CEP-C01-14 | Previous ad hoc evidence is migrated | pending (migration commit) |
| CEP-C01-15 | Provider tests pass | partial (CLI tests 41–43 are blocked) |
| CEP-C01-16 | `BoundedProcess` remains green | pass |
| CEP-C01-17 | `FSharpDiagnostics` remains green | pass |
| CEP-C01-18 | Complete range passes `git diff --check` | pass |
| CEP-C01-19 | Working tree is clean | pass (after implementation commit) |
| CEP-C01-20 | Publication is an ordinary fast-forward | pass |
| CEP-C01-21 | Final `HEAD == origin/main` | pass |
