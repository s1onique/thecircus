# ACT-CIRCUS-FSHARP-DIAGNOSTIC-BOUNDED-GIT-ADAPTER01-CORRECTION01

## Status

**READY — P0**

## Classification

**P0 — closure-evidence and merge-parent semantic convergence**

## Parent

`ACT-CIRCUS-FSHARP-DIAGNOSTIC-BOUNDED-GIT-ADAPTER01`

## Baseline

```yaml
baseline_commit_oid: 560de206a3190bcc6b2389eca9412babb176b400
baseline_tree_oid:   <resolve from git>
origin_main_oid:     560de206a3190bcc6b2389eca9412babb176b400
working_tree_required: clean
```

Resolve and record the baseline tree before changing files.

## Parent verdict at correction entry

```yaml
parent_verdict: PARTIAL
production_adapter_core: PASS
canonical_closure_evidence: FAIL
failure_taxonomy_proof: PARTIAL
merge_parent_semantics: FAIL_OR_UNSPECIFIED
successor_release: BLOCKED
```

## Objective

Converge the bounded Git adapter's production contract, tests,
predecessor documentation, close report, gate evidence, and closure
identities.

This correction must not redesign the proven `BoundedProcess`
production implementation. It may change adapter-facing APIs, adapter
tests, bounded-process tests only where their ownership is
explicitly justified, and closure documentation.

## P0-1 — Reconcile merge-parent semantics

The original API permitted:

```text
beforeCommitInput != explicitParent
```

while deriving:

```text
BeforeTreeOid = tree(beforeCommitInput)
CommitRange   = explicitParent..afterCommit
ChangeSet     = tree(beforeCommitInput)..tree(afterCommit)
```

This produced an identity assembled from two different historical
baselines.

CORRECTION01 implements the preferred model: for merge commits, the
explicit parent is the effective before state, so the before-tree
and the commit-range baseline must be the same historical point.

```text
effective_before_commit = explicitParent
effective_before_tree   = tree(explicitParent)
commit_range            = explicitParent..afterCommit
change_set              = tree(explicitParent)..tree(afterCommit)
```

`resolveGitIdentityWithParent` now requires
`beforeCommitInput = explicitParent` and fails closed otherwise.

### Required merge tests

1. Two-parent merge without explicit selection fails closed.
2. Selecting parent one produces parent-one-tree → merge-tree
   changes.
3. Selecting parent two produces parent-two-tree → merge-tree
   changes.
4. The two resulting change sets differ for an asymmetric merge
   fixture.
5. Mismatched `beforeCommitInput` and selected parent fail closed.
6. Commit range, before commit, before tree, and change-set baseline
   are internally consistent.
7. Repeated resolution is byte-identical.

## P0-2 — Make the failure taxonomy truthful and reachable

CORRECTION01 separates the raw and checked command surfaces and
exposes deterministic parser/seam entry points so every typed
failure category is reachable through production translation logic.

### Raw completed-process surface

`runGitTyped` returns `Result<GitRunSuccess, GitRunError>`. A
completed child run is surfaced as `Ok` even when its exit code is
non-zero; the exit code, stdout, and stderr are part of the success
payload.

`runGitTypedWithCancellation` adds an explicit `cancellationToken`
parameter so callers can drive the adapter through a real
cancellation path.

### Checked-command surface

`runGitChecked` converts non-zero exits into `GitExitFailure`
retaining the exact argument vector, exit code, stdout, and stderr.
The eight bounded-process failure modes are surfaced as their
dedicated exceptions.

### Cancellation

`runGitTypedWithCancellation` is the cancellation-aware entry
point. The cancellation test cancels an actual adapter invocation
and asserts the typed `CancellationFailure` outcome.

### I/O and protocol failures

`translateBoundedError` is exposed as `internal` and is exercised
by the I/O test, which drives every I/O-related
`BoundedProcessFailure` shape and asserts each surfaces as
`IoFailure`.

`parseGitBytesOrProtocol` is the deterministic parser entry
point. The protocol-failure test drives a parser that returns
`Error` and asserts the adapter surfaces `ProtocolFailure`.

### Required taxonomy tests

1. launch failure
2. timeout failure
3. caller cancellation
4. stdout overflow
5. stderr overflow
6. I/O / read failure
7. checked-command non-zero exit failure
8. protocol / malformed-output failure

No acceptance row may claim a branch that is type-declared but
unreachable.

## P0-3 — Reconcile canonical identities

The CORRECTION01 close report contains no placeholders. It records
every OID as the actual Git object.

The existing published parent tag
(`act/circus-fsharp-diagnostic-bounded-git-adapter01-v1`) is not
moved or overwritten. It is preserved as historical evidence. A new
correction tag
(`act/circus-fsharp-diagnostic-bounded-git-adapter01-correction01-v1`)
targets the final correction closure commit.

## P0-4 — Reconcile predecessor documentation

`docs/acts/ACT-CIRCUS-FSHARP-DIAGNOSTIC-REPAIR-EPISODE-LINKING01-CORRECTION02.md`
retains its historical claims and adds an explicit final handoff:

```yaml
bounded_process_authority:
  tests: 38
  passed: 38
  authority_ready: true

git_adapter_successor:
  status: completed_or_corrected
```

Historical chronology is preserved; the handoff is appended rather
than rewriting earlier partial checkpoints.

## P1-1 — Account for the complete changeset

The CORRECTION01 changeset is documented in the close report's
"Changeset classification" section. Only files in the owned scope
(`tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/Git.fs`,
`tests/.../GitAdapterTests.fs`, the two `.fsproj` files, and the
ACT and close-report markdown files) are touched.

## P1-2 — Produce fresh gate evidence

The CORRECTION01 close report regenerates an identity-bound gate
summary with all required checks. `generated_at` postdates the
tested commit and the tested tree equals the recorded tested tree.

## Required direct test commands

```bash
dotnet build tools/Circus.Tooling/Circus.Tooling.fsproj \
  -c Release --no-restore

dotnet build tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
  -c Release --no-restore

dotnet run \
  --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
  -c Release --no-build --no-restore -- \
  --summary --filter-test-list \
  "FSharpDiagnostics.RepairEpisodes.BoundedProcess"

dotnet run \
  --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
  -c Release --no-build --no-restore -- \
  --summary --filter-test-list \
  "FSharpDiagnostics.RepairEpisodes.GitAdapter"

dotnet run \
  --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
  -c Release --no-build --no-restore -- \
  --summary --filter-test-list \
  "FSharpDiagnostics.RepairEpisodes"

dotnet run \
  --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
  -c Release --no-build --no-restore -- \
  --summary --filter-test-list \
  "FSharpDiagnostics"

make gate-fsharp-repair-episodes

git diff --check
git status --short
```

Direct GitAdapter test totals are recorded in the close report;
they are not inferred by subtracting suite totals.

## Protected scope

Do not modify:

```text
tools/Circus.Tooling/NoForcePush/
src/Circus.Persistence.Postgres/
tests/Circus.Persistence.Postgres.Tests/
factory/evidence/fsharp-diagnostics/corpus/raw/
```

Do not begin:

```text
ACT-CIRCUS-NO-FORCE-PUSH-DOCTRINE-GATE01-CORRECTION02
ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-EXTRACTION01
```

## Acceptance criteria

| ID     | Criterion                                                                                                                              |
| ------ | -------------------------------------------------------------------------------------------------------------------------------------- |
| C01-01 | Merge-parent selection and change-set baseline are coherent                                                                            |
| C01-02 | Asymmetric merge tests prove each selected parent produces its intended diff                                                           |
| C01-03 | No identity combines a before tree from one parent with a commit range rooted at another without explicit dual-baseline representation |
| C01-04 | Cancellation is exercised through the Git adapter                                                                                      |
| C01-05 | I/O failure is reachable and adapter-tested                                                                                            |
| C01-06 | Protocol failure is reachable and adapter-tested                                                                                       |
| C01-07 | Checked non-zero exit produces a typed exit failure retaining all evidence                                                             |
| C01-08 | Raw completed-process and checked-command semantics are clearly separated                                                              |
| C01-09 | The predecessor authority document reflects final handoff truthfully                                                                   |
| C01-10 | No placeholder identities remain                                                                                                       |
| C01-11 | Final report identities match actual Git objects                                                                                       |
| C01-12 | Existing parent tag is not rewritten                                                                                                   |
| C01-13 | New correction tag object and target are recorded                                                                                      |
| C01-14 | GitAdapter tests are run directly                                                                                                      |
| C01-15 | Fresh gate summary binds the tested correction tree                                                                                    |
| C01-16 | BoundedProcess remains 38/38 green or any intentional count change is fully explained                                                  |
| C01-17 | Full FSharpDiagnostics suite passes                                                                                                    |
| C01-18 | Protected scope is untouched                                                                                                           |
| C01-19 | Working tree is clean                                                                                                                  |
| C01-20 | Publication is an ordinary fast-forward                                                                                                |
| C01-21 | `HEAD == origin/main` after publication                                                                                                |

## Stop conditions

Stop with `PARTIAL_CHECKPOINT` when:

* merge-parent and change-set semantics remain contradictory;
* cancellation, I/O, protocol, or exit branches remain unreachable
  despite being claimed;
* any canonical identity remains a placeholder;
* the close report describes an older `HEAD` as the final `HEAD`;
* the correction would require moving the existing published tag;
* the gate summary predates the tested commit;
* a successor ACT begins before this correction closes.

## Successor release

Only after CORRECTION01 closes may the project start:

```text
ACT-CIRCUS-NO-FORCE-PUSH-DOCTRINE-GATE01-CORRECTION02
```

The diagnostic successor remains separately gated:

```text
ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-EXTRACTION01
```
