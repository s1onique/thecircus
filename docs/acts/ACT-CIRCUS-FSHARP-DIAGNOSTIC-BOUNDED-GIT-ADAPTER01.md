# ACT-CIRCUS-FSHARP-DIAGNOSTIC-BOUNDED-GIT-ADAPTER01

## Status

**READY — P0**

## Parent epic

`EPIC-CIRCUS-FSHARP-DIAGNOSTIC-KNOWLEDGE-AND-HISTORY-SAFETY01`

## Baseline

```yaml
baseline_commit_oid: 200e20aa93a874fc70a389cd7a8c18e7579fc4d4
baseline_tree_oid: d5691437bb35f2198d7cd664cd3d2b46df04007f
working_tree_clean: true
origin_main_matches_head: true
```

## Predecessor authority

The predecessor BoundedProcess sequence is complete:

```yaml
tests_total: 38
tests_passed: 38
tests_failed: 0
tests_errored: 0
external_timeout_triggered: false
duration_seconds: 28.3
```

`BoundedProcess.run` is now approved for consumption by:

```text
tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/Git.fs
```

This ACT must not reopen or redesign the proven process runner unless an
adapter test demonstrates a production defect that cannot be corrected at
the adapter boundary.

## Objective

Replace the direct Git-process implementation in the RepairEpisodes
subsystem with a single bounded, typed adapter over `BoundedProcess.run`.

After this ACT:

* no RepairEpisodes Git operation launches a process independently;
* no shell participates in Git command execution;
* every command executes against the explicitly supplied repository;
* timeout, cancellation, output overflow, launch, I/O, and non-zero
  exit failures remain distinguishable;
* all returned Git object identities are validated against the
  repository's actual storage object format;
* downstream repair-episode logic receives deterministic, complete
  Git evidence.

## Owned scope

```text
tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/Git.fs
tests/Circus.Tooling.Tests/FSharpDiagnostics/RepairEpisodes/GitAdapterTests.fs
tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj
docs/acts/ACT-CIRCUS-FSHARP-DIAGNOSTIC-BOUNDED-GIT-ADAPTER01.md
docs/close-reports/ACT-CIRCUS-FSHARP-DIAGNOSTIC-BOUNDED-GIT-ADAPTER01.md
tools/Circus.Tooling/Circus.Tooling.fsproj
```

`Domain.fs` may change only when a typed adapter failure must be
represented there. Do not broaden that exception into unrelated domain
changes.

`Circus.Tooling.fsproj` compile order is adjusted so that
`BoundedProcess.fs` compiles before `Git.fs`. This is a non-functional
file-ordering change required for the Git adapter to consume
`BoundedProcess.run`.

## Protected scope

This ACT must not modify:

```text
tools/Circus.Tooling/NoForcePush/
src/Circus.Persistence.Postgres/
tests/Circus.Persistence.Postgres.Tests/
factory/evidence/fsharp-diagnostics/corpus/raw/
foundation extraction or normalization behavior
GitHub ruleset verification
```

It must not start:

* no-force-push CORRECTION03;
* rule-candidate extraction;
* causal-family clustering;
* repair-advice generation;
* FSB-0022 reconstruction.

## Required adapter contract

### 1. Single execution authority

All Git commands must flow through:

```fsharp
BoundedProcess.run
```

Delete or retire any independent `Process.Start`, stream-reader,
timeout, kill-tree, or output-limit implementation from
`RepairEpisodes/Git.fs`.

### 2. No shell

Invoke the Git executable directly.

Arguments must be transferred as an argument vector. Do not:

* concatenate a command line;
* invoke `/bin/sh`, `bash`, `cmd.exe`, or PowerShell;
* rely on shell quoting;
* interpolate repository-controlled data into executable text.

### 3. Repository-directory authority

Every command must execute with:

```text
WorkingDirectory = repoPath
```

The result must not depend on the process-wide current directory.

A nonexistent or non-repository `repoPath` must produce an explicit
typed failure.

### 4. Resource bounds

Use one canonical Git execution profile:

```yaml
timeout_ms: 60000
stdout_limit_bytes: 33554432
stderr_limit_bytes: 33554432
kill_process_tree: true
```

The exact byte limit is valid. Overflow begins only when output
exceeds the configured limit.

### 5. Failure taxonomy

Preserve distinct outcomes for:

```text
GitLaunchFailure
GitTimeoutFailure
GitCancellationFailure
GitStdoutOverflowFailure
GitStderrOverflowFailure
GitIoFailure
GitExitFailure
GitProtocolFailure
```

`GitExitFailure` must retain at least:

```yaml
argv: exact_argument_vector
exit_code: integer
stdout: bounded_text
stderr: bounded_text
```

Do not convert timeout, cancellation, overflow, or launch failures
into empty stdout/stderr or a generic non-zero exit.

### 6. Object-format authority

Determine the repository storage format using:

```text
git rev-parse --show-object-format=storage
```

Accepted formats:

```yaml
sha1:
  oid_hex_width: 40

sha256:
  oid_hex_width: 64
```

Unknown formats must fail closed.

Every commit, tree, parent, and diff identity consumed by
RepairEpisodes must be:

* full width;
* hexadecimal;
* validated against the detected format;
* untruncated.

No permissive SHA-1 fallback is allowed.

### 7. Non-abbreviated Git output

Commands that emit object identities must request complete identities.

In particular, preserve `--no-abbrev` where the existing repair-episode
contract requires it.

Never infer a full identity from an abbreviated value.

### 8. Merge ambiguity

For a merge commit, the adapter must not silently choose a parent.

It must either:

1. consume an explicit, declaration-bound parent selection; or
2. return a typed merge-ambiguity failure containing the candidate
   parent identities.

### 9. Deterministic decoding

Git output decoding must:

* normalize only contractually irrelevant line endings;
* preserve path bytes representable by the selected Git output
  protocol;
* reject malformed record widths;
* reject missing required fields;
* reject extra ambiguous records;
* avoid locale-sensitive parsing.

## Required tests

### Working-directory and argument tests

1. Run the adapter while the test process current directory is
   outside the fixture repository; prove the supplied `repoPath` is
   honored.
2. Use repository paths containing spaces.
3. Pass arguments containing spaces and shell metacharacters; prove
   they remain literal argument values.
4. Prove no shell executable is launched.

### Completion and failure tests

5. Successful command with empty stdout.
6. Successful command with non-empty stdout and stderr.
7. Non-zero exit retains exit code and both bounded streams.
8. Missing executable through an injected seam produces
   `GitLaunchFailure`.
9. Timeout produces `GitTimeoutFailure`.
10. External cancellation produces `GitCancellationFailure`.
11. Stdout limit plus zero bytes succeeds.
12. Stdout limit plus one byte produces `GitStdoutOverflowFailure`.
13. Stderr limit plus zero bytes succeeds.
14. Stderr limit plus one byte produces `GitStderrOverflowFailure`.
15. Stream/read failure remains distinct from process exit.

### Identity tests

16. Detect SHA-1 storage format and accept exactly 40 hexadecimal
    characters.
17. Reject 39- and 41-character SHA-1 identities.
18. Exercise a SHA-256 repository when supported by the installed Git;
    otherwise prove the 64-character validator with a hermetic parser
    test and record the unavailable live capability honestly.
19. Reject unknown object formats.
20. Prove `diff-tree` and related identity-producing commands return
    complete, non-abbreviated OIDs.
21. Reject malformed and abbreviated OIDs before they enter the
    repair-episode domain.

### Repository-topology tests

22. Read commit and tree identities from a real temporary repository.
23. Produce a real two-parent merge and prove implicit parent
    selection fails closed.
24. Provide explicit parent evidence and prove the intended
    before/after change set is selected.
25. Prove invalid repository paths and non-repositories return typed
    failures.
26. Prove paths and changed-file records are deterministic across
    repeated runs.

### Regression suites

27. Existing 38 BoundedProcess tests remain green.
28. All RepairEpisodes tests remain green.
29. All FSharpDiagnostics tests remain green.
30. Controlled repair-episode regeneration remains byte-identical
    across two runs.

## Acceptance criteria

| ID    | Criterion                                                                                                         |
| ----- | ----------------------------------------------------------------------------------------------------------------- |
| BG-01 | `RepairEpisodes/Git.fs` contains no independent process lifecycle                                                 |
| BG-02 | Every Git operation consumes `BoundedProcess.run`                                                                 |
| BG-03 | No shell participates in execution                                                                                |
| BG-04 | `repoPath` is the effective working directory                                                                     |
| BG-05 | Timeout is fixed at 60 seconds                                                                                    |
| BG-06 | stdout and stderr are independently bounded at 32 MiB                                                             |
| BG-07 | Exact-limit output succeeds and limit-plus-one fails                                                              |
| BG-08 | Launch, timeout, cancellation, stdout overflow, stderr overflow, I/O, exit, and protocol failures remain distinct |
| BG-09 | Repository storage object format is detected rather than assumed                                                  |
| BG-10 | All object identities have exact format-specific width                                                            |
| BG-11 | No abbreviated identity enters the domain                                                                         |
| BG-12 | Merge-parent ambiguity fails closed                                                                               |
| BG-13 | Real-repository tests cover ordinary commits and merges                                                           |
| BG-14 | Existing BoundedProcess authority remains 38/38 green                                                             |
| BG-15 | Entire FSharpDiagnostics suite passes                                                                             |
| BG-16 | Repair-episode regeneration is deterministic                                                                      |
| BG-17 | No NoForcePush or PostgreSQL files change                                                                         |
| BG-18 | `git diff --check` passes                                                                                         |
| BG-19 | Final working tree is clean                                                                                       |
| BG-20 | Publication is an ordinary fast-forward                                                                           |
| BG-21 | Final `HEAD` and `origin/main` identities match                                                                   |

## Mandatory verification

Run and capture exact commands, exit codes, duration, and tested
identity for:

```bash
dotnet build tools/Circus.Tooling/Circus.Tooling.fsproj -c Release --no-restore

dotnet build tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
  -c Release --no-restore

dotnet run \
  --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
  -c Release --no-build --no-restore -- \
  --summary --filter-test-list "BoundedProcess"

dotnet run \
  --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
  -c Release --no-build --no-restore -- \
  --summary --filter-test-list "FSharpDiagnostics.RepairEpisodes"

dotnet run \
  --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
  -c Release --no-build --no-restore -- \
  --summary --filter-test-list "FSharpDiagnostics"

make gate-fsharp-repair-episodes

git diff --check
git status --short
```

Do not claim a test result from an earlier commit or tree.

## Evidence requirements

The close report must bind:

```yaml
baseline_commit_oid:
baseline_tree_oid:
implementation_commit_oid:
implementation_tree_oid:
tested_commit_oid:
tested_tree_oid:
documentation_commit_oid:
final_head_oid:
origin_main_oid:

bounded_process_tests:
  total:
  passed:
  failed:
  errored:
  duration_seconds:

git_adapter_tests:
  total:
  passed:
  failed:
  errored:

repair_episode_tests:
  total:
  passed:
  failed:
  errored:

fsharp_diagnostics_tests:
  total:
  passed:
  failed:
  errored:

publication:
  ordinary_fast_forward:
  force_update:
```

A dirty-tree test run, stale gate summary, abbreviated identity, or
unbounded external command blocks closure.

## Stop conditions

Stop with `PARTIAL_CHECKPOINT` and do not publish a PASS claim when:

* the canonical BoundedProcess suite regresses;
* any Git command bypasses the bounded adapter;
* an output bound is not independently enforced;
* timeout and cancellation cannot be distinguished;
* object-format detection is absent;
* a merge parent is selected implicitly;
* SHA-256 behavior is claimed without either a live repository
  proof or an explicitly scoped parser proof;
* the tested tree differs from the reported implementation tree;
* publication would require a force update.

## Successor

On successful closure, resume:

```text
ACT-CIRCUS-NO-FORCE-PUSH-DOCTRINE-GATE01-CORRECTION02
```

The subsequent diagnostic-knowledge successor remains:

```text
ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-EXTRACTION01
```

Neither successor begins inside this ACT.
