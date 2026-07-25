# ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER-FOUNDATION01

## Status

**READY — P0**

## Classification

**P0 — repository-wide canonical evidence authority**

## Parent epic

`EPIC-CIRCUS-CANONICAL-EVIDENCE-AUTHORITY`

## Objective

Establish the first repository-owned canonical evidence provider.

The provider must generate deterministic, machine-readable,
identity-bound evidence from actual command executions. It must
replace manually asserted "canonical gate" sections and ungoverned
`.factory/gate-summary.json` files with a defined executable
authority.

This ACT also migrates the bounded Git adapter closure evidence to
the new provider without modifying its production implementation.

## Current state

```yaml
canonical_evidence_provider:
  present: false

existing_gate_summary:
  path: .factory/gate-summary.json
  classification: ad_hoc_supporting_evidence
  canonical: false

bounded_git_adapter:
  implementation_status: pass
  canonical_closure_status: partial
```

## Principles

1. Canonicality comes from a declared provider, not from a filename.
2. Evidence is generated, never hand-authored.
3. The provider fails closed when a required check cannot run.
4. Test results are bound to an exact commit and tree.
5. Pre-publication evidence and post-publication evidence remain
   separate.
6. A committed artifact never claims its own future commit, tree,
   blob, or tag-object identity.
7. Annotated tags or detached transcripts perform post-commit
   binding.
8. Existing historical tags are immutable.

## Owned scope

```text
tools/Circus.Tooling/CanonicalEvidence/
tests/Circus.Tooling.Tests/CanonicalEvidence/
.factory/evidence-provider-schema.json
.factory/gate-summary.json
docs/evidence/
docs/acts/ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER-FOUNDATION01.md
docs/close-reports/ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER-FOUNDATION01.md
Makefile
```

## Protected scope

Do not modify unless a newly demonstrated production failure
requires it:

```text
tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/Git.fs
tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/BoundedProcess.fs
tools/Circus.Tooling/NoForcePush/
src/Circus.Persistence.Postgres/
factory/evidence/fsharp-diagnostics/corpus/raw/
```

## Provider command

Add one stable command, for example:

```bash
dotnet run \
  --project tools/Circus.Tooling/Circus.Tooling.fsproj \
  -c Release --no-build -- \
  canonical-evidence regenerate \
  --repo-root . \
  --output .factory/gate-summary.json
```

A Make target may wrap it:

```bash
make canonical-evidence
```

The executable command—not the Make wrapper—is the canonical
generation authority.

## Required schema

The generated evidence must include:

```yaml
schema_version:
provider:
  name:
  version:
  implementation_commit_oid:
  implementation_tree_oid:

generation:
  generated_at:
  repository_root:
  working_tree_clean:
  generator_exit_code:

subject:
  tested_commit_oid:
  tested_tree_oid:
  object_format:

checks:
  - id:
    command_argv:
    working_directory:
    started_at:
    duration_ms:
    exit_code:
    status:
    stdout_sha256:
    stderr_sha256:
    evidence_path:

summary:
  checks_total:
  checks_passed:
  checks_failed:
  checks_unavailable:
  overall_status:

artifact:
  content_sha256:
```

Do not store shell command strings as execution authority. Preserve
the executable and argument vector separately.

## Check semantics

Canonical statuses:

```text
pass
fail
unavailable
```

Rules:

* `pass` requires exit code zero and valid expected output.
* `fail` means the command ran and disproved the condition.
* `unavailable` means the provider could not execute the required
  check.
* Required `fail` or `unavailable` yields `overall_status=fail`.
* Missing output must never become an empty successful result.
* Unknown check IDs or schema versions fail validation.

## Identity authority

Before running checks, resolve:

```bash
git rev-parse --show-object-format=storage
git rev-parse --verify HEAD^{commit}
git rev-parse --verify HEAD^{tree}
git status --porcelain=v1
```

The provider must reject:

* abbreviated OIDs;
* unknown object formats;
* dirty trees unless the declared mode explicitly permits them;
* a tested commit/tree mismatch;
* identity values supplied only by the caller without independent
  Git resolution.

## Deterministic generation

For identical:

```text
provider version
tested commit
tested tree
check definitions
check results
```

the normalized semantic evidence must be identical.

Exclude volatile values such as timestamps from the semantic
evidence hash, or place them outside the normalized hash scope.

Generate through:

1. temporary sibling file;
2. complete validation;
3. flush and close;
4. atomic replacement.

A failure must preserve the previous valid artifact byte-identically.

## Provider registry

Create a tracked authority document defining:

```yaml
canonical_provider:
  command:
  implementation_path:
  schema_path:
  output_path:
  validator_command:
  ownership:
  required_gate:
```

This is the project-level declaration that makes the generated
artifact canonical.

Without this registry entry, an evidence file remains non-canonical.

## Migration of bounded Git adapter evidence

Reclassify the existing `.factory/gate-summary.json` used by:

```text
ACT-CIRCUS-FSHARP-DIAGNOSTIC-BOUNDED-GIT-ADAPTER01-CORRECTION02
```

as:

```yaml
historical_classification: ad_hoc_supporting_evidence
canonical_at_creation: false
```

Correct its historical handoff to state:

```yaml
correction02:
  implementation_status: pass
  act_local_evidence_status: pass
  canonical_evidence_status: migrated_by_provider_foundation
```

## Required provider tests

1. Deterministic normalized output.
2. Successful complete generation.
3. Required command failure.
4. Required command unavailable.
5. Invalid repository path.
6. Dirty worktree rejection.
7. Wrong tested commit.
8. Wrong tested tree.
9. Abbreviated OID rejection.
10. Unknown object format rejection.
11. Malformed previous artifact.
12. Unknown schema version.
13. Unknown check ID.
14. stdout capture failure.
15. stderr capture failure.
16. output overflow.
17. timeout.
18. cancellation.
19. temporary-file write failure.
20. atomic replacement failure.
21. previous artifact survives failed regeneration.
22. manual artifact mutation is detected.
23. stale artifact is detected.
24. timestamp does not affect semantic hash.
25. argument vectors containing spaces and metacharacters remain
    literal.
26. no shell participates in check execution.
27. post-publication fields are rejected from pre-publication
    evidence.
28. self-referential identity fields are rejected from tagged reports.

## Acceptance criteria

| ID     | Criterion                                                                                                                              |
| ------ | -------------------------------------------------------------------------------------------------------------------------------------- |
| EP-01 | One executable provider is registered as canonical                                                                                  |
| EP-02 | Schema and output path are tracked                                                                                                  |
| EP-03 | Generation uses bounded, no-shell execution                                                                                         |
| EP-04 | Every check retains argv, directory, exit status, and output hashes                                                                 |
| EP-05 | Required unavailable checks fail closed                                                                                             |
| EP-06 | Tested commit and tree are independently resolved                                                                                   |
| EP-07 | Abbreviated and malformed identities fail                                                                                           |
| EP-08 | Semantic generation is deterministic                                                                                                |
| EP-09 | Publication is atomic                                                                                                               |
| EP-10 | Failed regeneration preserves previous evidence                                                                                     |
| EP-11 | Manual mutation is detected                                                                                                         |
| EP-12 | Stale evidence is rejected                                                                                                          |
| EP-13 | Canonical gate consumes and validates the provider artifact                                                                         |
| EP-14 | Tagged reports contain no self-referential identities                                                                               |
| EP-15 | Annotated tag messages bind target and evidence blobs                                                                               |
| EP-16 | Post-publication evidence remains detached                                                                                        |
| EP-17 | Bounded Git adapter evidence is migrated without production changes                                                                 |
| EP-18 | Existing tags remain unchanged                                                                                                      |
| EP-19 | Full provider test suite passes                                                                                                     |
| EP-20 | Existing FSharpDiagnostics suites remain green                                                                                      |
| EP-21 | `git diff --check` passes for the complete ACT range                                                                                |
| EP-22 | Publication uses an ordinary fast-forward                                                                                           |
| EP-23 | Final `HEAD == origin/main`                                                                                                         |

## Stop conditions

Stop with `PARTIAL_CHECKPOINT` when:

* the provider is only a script or file with no registered authority;
* `.factory/gate-summary.json` remains manually writable without
  detection;
* command results can be copied into the artifact without execution;
* unavailable checks can produce an overall pass;
* tested identities are caller assertions rather than Git-resolved
  facts;
* an artifact tries to contain its own blob, tree, commit, or future
  tag-object OID;
* a supposedly detached transcript is embedded in the tagged report;
* migration requires rewriting an existing tag;
* publication would require a force update.

## Successor

After this ACT closes:

```text
ACT-CIRCUS-NO-FORCE-PUSH-DOCTRINE-GATE01-CORRECTION02
```

becomes the immediate successor and must consume the canonical
provider for its local and remote enforcement evidence.

`ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-EXTRACTION01` remains
separately dependent on non-vacuous repair-episode evidence.
