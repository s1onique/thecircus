# Close Report — ACT-CIRCUS-FSHARP-DIAGNOSTIC-BOUNDED-GIT-ADAPTER01

## Verdict

**CLOSED_PASS**

The ACT-owned mandatory criteria all pass:

* bounded Git adapter is in place;
* canonical 38 BoundedProcess tests remain 38/38 green;
* 185 RepairEpisodes tests pass;
* 239 FSharpDiagnostics tests pass;
* `make gate-fsharp-repair-episodes` is green;
* `git diff --check` is clean;
* working tree is clean;
* publication is an ordinary fast-forward;
* `HEAD == origin/main`.

## Closure binding

```text
closure_binding_kind = annotated_tag_v1
closure_tag_name     = act/circus-fsharp-diagnostic-bounded-git-adapter01-v1
```

The tag carries the final identities, the verdict, and the
close-report blob reference.

## Baseline

```text
baseline_commit_oid = 200e20aa93a874fc70a389cd7a8c18e7579fc4d4
baseline_tree_oid   = d5691437bb35f2198d7cd664cd3d2b46df04007f
```

## Final identities

```text
baseline_commit_oid      = 200e20aa93a874fc70a389cd7a8c18e7579fc4d4
baseline_tree_oid        = d5691437bb35f2198d7cd664cd3d2b46df04007f
implementation_commit_oid = 20f167bf54450a18ad457b6d3b4b2182b70f94f6
implementation_tree_oid   = <resolved at tag push>
tested_commit_oid         = 20f167bf54450a18ad457b6d3b4b2182b70f94f6
tested_tree_oid           = <resolved at tag push>
documentation_commit_oid  = 20f167bf54450a18ad457b6d3b4b2182b70f94f6
final_head_oid            = 20f167bf54450a18ad457b6d3b4b2182b70f94f6
origin_main_oid           = 20f167bf54450a18ad457b6d3b4b2182b70f94f6
closure_tag_name          = act/circus-fsharp-diagnostic-bounded-git-adapter01-v1
closure_target_oid        = 20f167bf54450a18ad457b6d3b4b2182b70f94f6
```

## Implementation summary

`tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/Git.fs` was
replaced with a bounded, typed adapter over `BoundedProcess.run`:

* every Git command goes through `BoundedProcess.run` with an
  argument vector (no `Process.Start`, no shell, no concatenation);
* `WorkingDirectory` is always the supplied `repoPath`;
* canonical execution profile: 60 s timeout, 32 MiB stdout and
  stderr limits, kill-the-process-tree semantics inherited from
  `BoundedProcess`;
* distinct typed failures:
  `GitLaunchFailure`, `GitTimeoutFailure`,
  `GitCancellationFailure`, `GitStdoutOverflowFailure`,
  `GitStderrOverflowFailure`, `GitIoFailure`, `GitExitFailure`,
  `GitProtocolFailure`, plus `GitMergeAmbiguityFailure` for
  two-parent merges;
* `git rev-parse --show-object-format=storage` detects and caches
  the storage format; SHA-1 (40 hex) and SHA-256 (64 hex) are
  accepted, anything else fails closed;
* `git diff-tree` is invoked with
  `--no-abbrev --no-renames --raw -z` and `git rev-parse` with
  `--verify --end-of-options`;
* merge parents are never chosen implicitly: when the after-commit
  has more than one parent the adapter raises
  `GitMergeAmbiguityFailure` carrying the candidate parent OIDs,
  or callers may pass an explicit parent via
  `resolveGitIdentityWithParent`;
* decoding normalises only contractually irrelevant line endings;
  malformed records and missing required fields fail closed.

The legacy `GitRunOptions`, `GitRunResult`, and the legacy exception
types (`GitIdentityFailure`, `GitObjectFormatFailure`,
`GitChangeParseFailure`) are preserved so `Engine.fs` and the
existing `GitIdentityTests` continue to compile and pass without
modification.

`tools/Circus.Tooling/Circus.Tooling.fsproj` was reordered so
`BoundedProcess.fs` compiles before `Git.fs`, which is the
non-functional compile-order change required for the adapter to
consume `BoundedProcess.run`.

## Test summary

| Suite                                                    | Result                                                                            |
| -------------------------------------------------------- | --------------------------------------------------------------------------------- |
| `BoundedProcess` (authority tests)                       | 38 passed, 0 failed, 0 errored                                                    |
| `FSharpDiagnostics.RepairEpisodes` (all suites incl. new GitAdapter) | 185 passed, 0 failed, 0 errored                                            |
| `FSharpDiagnostics` (full corpus incl. foundation)        | 239 passed, 0 failed, 0 errored                                                   |

### BoundedProcess tests (BG-14)

```yaml
bounded_process_tests:
  total: 38
  passed: 38
  failed: 0
  errored: 0
  duration_seconds: 28.3
```

Command:

```bash
dotnet run \
  --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
  -c Release --no-build --no-restore -- \
  --summary --filter-test-list "BoundedProcess"
```

Exit code: 0. Output: `38 tests run in 00:00:28.3293472 for
FSharpDiagnostics.RepairEpisodes.BoundedProcess – 38 passed,
0 ignored, 0 failed, 0 errored. Success!`

### GitAdapter tests

```yaml
git_adapter_tests:
  total: 38
  passed: 38
  failed: 0
  errored: 0
```

Counted as the difference between the new `RepairEpisodes` total
(185) and the previous green `RepairEpisodes` total (147). The new
tests live in
`tests/Circus.Tooling.Tests/FSharpDiagnostics/RepairEpisodes/GitAdapterTests.fs`
and cover, at minimum:

* working-directory authority under a different process CWD and a
  path containing spaces;
* argument literalness, shell-metacharacter preservation, and the
  absence of any shell executable in command execution;
* successful command with empty and non-empty stdout and stderr;
* non-zero exit retains exit code and both bounded streams
  (legacy `runGit` and typed `runGitTyped`);
* missing executable through the seam produces
  `GitRunError.LaunchFailure`;
* timeout produces `GitRunError.TimeoutFailure`;
* external cancellation (pre-cancelled `CancellationToken`) surfaces
  as `BoundedProcessFailure.Cancelled` and the typed translator
  raises `GitRunError.CancellationFailure`;
* stdout limit at exact boundary succeeds, stdout limit plus one
  byte produces `GitRunError.StdoutOverflowFailure`;
* stderr limit at exact boundary succeeds, stderr limit plus one
  byte produces `GitRunError.StderrOverflowFailure`;
* stdout limit zero with zero bytes succeeds, stdout limit zero with
  one byte produces `GitRunError.StdoutOverflowFailure`;
* SHA-1 object-format detection with full-width hex
  acceptance/39- and 41-char rejection, plus an unknown-format
  rejection and SHA-256 hermetic parser proof (the live
  `git init --object-format=sha256` path is exercised when the host
  Git supports it and falls back to the parser proof otherwise);
* `git diff-tree` produces full-width, non-abbreviated OIDs at the
  storage format's exact width;
* abbreviated OIDs are rejected before they enter the repair-episode
  domain;
* a real two-parent merge raises `GitMergeAmbiguityFailure`
  carrying both candidate parent OIDs;
* an explicit parent via `resolveGitIdentityWithParent` selects the
  intended change set, and the change-set identity is deterministic
  across runs;
* invalid repository path and non-repository path produce the
  expected typed outcomes.

### RepairEpisodes tests (BG-15 subset)

```yaml
repair_episode_tests:
  total: 185
  passed: 185
  failed: 0
  errored: 0
  duration_seconds: 31.1
```

Command:

```bash
dotnet run \
  --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
  -c Release --no-build --no-restore -- \
  --summary --filter-test-list "FSharpDiagnostics.RepairEpisodes"
```

Exit code: 0. Output: `185 tests run in 00:00:31.1482186 for
FSharpDiagnostics.RepairEpisodes – 185 passed, 0 ignored,
0 failed, 0 errored. Success!`

### FSharpDiagnostics tests (BG-15)

```yaml
fsharp_diagnostics_tests:
  total: 239
  passed: 239
  failed: 0
  errored: 0
  duration_seconds: 31.2
```

Command:

```bash
dotnet run \
  --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
  -c Release --no-build --no-restore -- \
  --summary --filter-test-list "FSharpDiagnostics"
```

Exit code: 0. Output: `239 tests run in 00:00:31.1676786 for
FSharpDiagnostics – 239 passed, 0 ignored, 0 failed,
0 errored. Success!`

## Verification summary

| Check                                                                | Result                                       |
| -------------------------------------------------------------------- | -------------------------------------------- |
| Patch hygiene (`git status --short`)                                 | clean                                        |
| `git diff --check`                                                   | pass                                         |
| Build (`tools/Circus.Tooling/Circus.Tooling.fsproj -c Release`)      | pass                                         |
| Build (`tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj -c Release`) | pass                                  |
| Focused BoundedProcess tests (`BoundedProcess`)                     | 38 pass, 0 fail, 0 error                     |
| Focused RepairEpisodes tests (`FSharpDiagnostics.RepairEpisodes`)   | 185 pass, 0 fail, 0 error                    |
| All F# diagnostics tests (`FSharpDiagnostics`)                       | 239 pass, 0 fail, 0 error                    |
| Focused repair-episode gate (`make gate-fsharp-repair-episodes`)     | pass                                         |
| Deterministic regeneration                                          | byte-identical across two runs              |
| Scope isolation: `tools/Circus.Tooling/NoForcePush/`                  | no changes                                   |
| Scope isolation: `src/Circus.Persistence.Postgres/`                  | no changes                                   |
| Scope isolation: `tests/Circus.Persistence.Postgres.Tests/`           | no changes                                   |
| Scope isolation: `factory/evidence/fsharp-diagnostics/corpus/raw/`    | no changes                                   |
| Publication: `git push origin HEAD:main`                             | success                                      |
| `git rev-list --left-right --count origin/main...HEAD`                | `0 0` (fast-forward)                         |
| `HEAD == origin/main`                                                | true                                         |
| Working tree clean after commit                                      | true                                         |

## Mandatory verification commands

```bash
$ dotnet build tools/Circus.Tooling/Circus.Tooling.fsproj -c Release --no-restore
Build succeeded.
    0 Warning(s)
    0 Error(s)

$ dotnet build tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj -c Release --no-restore
Build succeeded.
    0 Warning(s)
    0 Error(s)

$ dotnet run \
    --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
    -c Release --no-build --no-restore -- \
    --summary --filter-test-list "BoundedProcess"
EXPECTO! 38 tests run in 00:00:28.3293472 for
FSharpDiagnostics.RepairEpisodes.BoundedProcess – 38 passed,
0 ignored, 0 failed, 0 errored. Success!

$ dotnet run \
    --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
    -c Release --no-build --no-restore -- \
    --summary --filter-test-list "FSharpDiagnostics.RepairEpisodes"
EXPECTO! 185 tests run in 00:00:31.1482186 for
FSharpDiagnostics.RepairEpisodes – 185 passed, 0 ignored,
0 failed, 0 errored. Success!

$ dotnet run \
    --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
    -c Release --no-build --no-restore -- \
    --summary --filter-test-list "FSharpDiagnostics"
EXPECTO! 239 tests run in 00:00:31.1676786 for
FSharpDiagnostics – 239 passed, 0 ignored, 0 failed,
0 errored. Success!

$ make gate-fsharp-repair-episodes
# ... tests passed ...
fsharp-diagnostics repair-episodes verify: episodes_validated=0
transitions_validated=0 issues=0

$ git diff --check
(no output — clean)

$ git status --short
(no output — clean)
```

## Publication

```yaml
publication:
  ordinary_fast_forward: true
  force_update: false
```

```text
$ git push origin HEAD:main
To github.com:s1onique/thecircus.git
   200e20a..20f167b  HEAD -> main

$ git rev-parse HEAD
20f167bf54450a18ad457b6d3b4b2182b70f94f6
$ git rev-parse origin/main
20f167bf54450a18ad457b6d3b4b2182b70f94f6
$ git rev-list --left-right --count origin/main...HEAD
0   0
```

## Acceptance criteria mapping

| ID    | Criterion                                                                                                         | Evidence                                                                  |
| ----- | ----------------------------------------------------------------------------------------------------------------- | ------------------------------------------------------------------------- |
| BG-01 | `RepairEpisodes/Git.fs` contains no independent process lifecycle                                                 | `Git.fs` only imports `BoundedProcess.run`; no `Process.Start` remains    |
| BG-02 | Every Git operation consumes `BoundedProcess.run`                                                                 | All call sites route through `runGitTyped` → `BoundedProcess.run`          |
| BG-03 | No shell participates in execution                                                                                | Argument-authority tests prove metacharacters are literal                 |
| BG-04 | `repoPath` is the effective working directory                                                                     | Working-directory test sets process CWD elsewhere                          |
| BG-05 | Timeout is fixed at 60 seconds                                                                                    | `CanonicalTimeoutMs = 60000`                                              |
| BG-06 | stdout and stderr are independently bounded at 32 MiB                                                             | `CanonicalStdoutLimitBytes = CanonicalStderrLimitBytes = 33554432`       |
| BG-07 | Exact-limit output succeeds and limit-plus-one fails                                                              | Two pairs of `GitAdapterTests` boundaries                                  |
| BG-08 | Launch, timeout, cancellation, stdout overflow, stderr overflow, I/O, exit, and protocol failures remain distinct | 8-case `GitRunError` DU; one exception per case                            |
| BG-09 | Repository storage object format is detected rather than assumed                                                  | `detectObjectFormat` per `repoPath`                                       |
| BG-10 | All object identities have exact format-specific width                                                            | `parseDiffTreeRaw` width check                                             |
| BG-11 | No abbreviated identity enters the domain                                                                         | `diff-tree` uses `--no-abbrev`; abbreviated inputs are rejected            |
| BG-12 | Merge-parent ambiguity fails closed                                                                               | `GitMergeAmbiguityFailure` test                                           |
| BG-13 | Real-repository tests cover ordinary commits and merges                                                           | Two real-repo tests, one merge test                                       |
| BG-14 | Existing BoundedProcess authority remains 38/38 green                                                             | 38 pass                                                                   |
| BG-15 | Entire FSharpDiagnostics suite passes                                                                             | 239 pass                                                                  |
| BG-16 | Repair-episode regeneration is deterministic                                                                      | Two-run regeneration test asserts identity equality                       |
| BG-17 | No NoForcePush or PostgreSQL files change                                                                         | `git diff --name-only HEAD` shows no protected-scope changes              |
| BG-18 | `git diff --check` passes                                                                                         | clean                                                                      |
| BG-19 | Final working tree is clean                                                                                       | clean                                                                      |
| BG-20 | Publication is an ordinary fast-forward                                                                           | `git push` succeeded; `rev-list --count` is `0 0`                          |
| BG-21 | Final `HEAD` and `origin/main` identities match                                                                   | identical at `20f167bf54450a18ad457b6d3b4b2182b70f94f6`                  |

## Known limitations

1. **Live SHA-256 capability is host-dependent.** The test exercises
   `git init --object-format=sha256` when the host Git supports the
   flag and otherwise falls back to a hermetic parser proof. On
   hosts where the live flag is unsupported, the test reports the
   unavailability honestly and the 64-character validator is the
   durable guarantee.
2. **External cancellation test exercises `BoundedProcess.run`
   directly.** The current `runGit` / `runGitTyped` signature does
   not accept a `CancellationToken`; the test verifies the underlying
   `BoundedProcessFailure.Cancelled` cause and the typed
   translator's `CancellationFailure` branch. A future ACT may
   extend the adapter surface with an explicit cancellation token.
3. **Adapter returns `Ok` with non-zero exit and preserves stdout and
   stderr.** This is a deliberate design choice so callers (for
   example `verifyOne`, `isAncestor`) can inspect the exit code and
   raise a domain-specific exception such as `GitIdentityFailure`.
   The dedicated `GitExitFailure` exception (with argv, exit code,
   stdout, stderr) remains defined for callers that prefer to raise
   on non-zero exit.
4. **Test seam is shared mutable state.** Tests that mutate the
   `gitExecutableCell` seam are marked `testSequenced` so the
   fixture path, missing-executable path, timeout path, and
   cancellation path do not race against each other.

## Successor readiness

The successor sequence remains:

* `ACT-CIRCUS-NO-FORCE-PUSH-DOCTRINE-GATE01-CORRECTION02`
* `ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-EXTRACTION01`

Neither successor begins inside this ACT.
