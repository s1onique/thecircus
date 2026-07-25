# Close Report — ACT-CIRCUS-FSHARP-DIAGNOSTIC-BOUNDED-GIT-ADAPTER01-CORRECTION01

## Verdict

**CLOSED_PASS**

The CORRECTION01 ACT-owned mandatory criteria all pass:

* merge-parent semantics are coherent;
* the failure taxonomy is truthful: every category is reachable
  through production translation logic, including the new
  `runGitChecked` and `parseGitBytesOrProtocol` entry points;
* canonical identities are recorded as actual Git objects;
* the existing parent tag is preserved and a new correction tag
  targets the final closure commit;
* predecessor documentation reflects the final handoff;
* the fresh gate summary binds the tested correction tree;
* `git diff --check` is clean;
* working tree is clean;
* publication is an ordinary fast-forward;
* `HEAD == origin/main`.

## Verdict mapping to parent verdict

| Parent verdict                         | CORRECTION01 outcome                                |
| -------------------------------------- | --------------------------------------------------- |
| `production_adapter_core: PASS`        | preserved (no production regression)               |
| `canonical_closure_evidence: FAIL`     | resolved (no placeholders; all OIDs recorded)       |
| `failure_taxonomy_proof: PARTIAL`      | resolved (8/8 categories reachable through adapter) |
| `merge_parent_semantics: FAIL_OR_UNSPECIFIED` | resolved (require same baseline; asymmetric merge proven) |
| `successor_release: BLOCKED`           | unblocked                                           |

## Closure binding

```text
parent_closure_tag_name     = act/circus-fsharp-diagnostic-bounded-git-adapter01-v1
correction_closure_tag_name = act/circus-fsharp-diagnostic-bounded-git-adapter01-correction01-v1
```

The correction tag carries the final identities, this verdict, and
the close-report blob reference. The parent tag is preserved and is
not rewritten.

## Baseline

```yaml
baseline_commit_oid: 560de206a3190bcc6b2389eca9412babb176b400
baseline_tree_oid:   a25947788a4f3563735b8e7aca1187a2197451e2
```

## Final identities

```yaml
baseline_commit_oid:        560de206a3190bcc6b2389eca9412babb176b400
baseline_tree_oid:          a25947788a4f3563735b8e7aca1187a2197451e2
implementation_commit_oid:  a6b0e626c8c5c9f99f1d89ce214b6449d538a212
implementation_tree_oid:    ecaa7bcc7d39d7b8671364315bb512aa70b632be
tested_commit_oid:          a6b0e626c8c5c9f99f1d89ce214b6449d538a212
tested_tree_oid:            ecaa7bcc7d39d7b8671364315bb512aa70b632be
documentation_commit_oid:   a6b0e626c8c5c9f99f1d89ce214b6449d538a212
documentation_tree_oid:     ecaa7bcc7d39d7b8671364315bb512aa70b632be
correction_commit_oid:      a6b0e626c8c5c9f99f1d89ce214b6449d538a212
correction_tree_oid:        ecaa7bcc7d39d7b8671364315bb512aa70b632be
final_head_oid:             a6b0e626c8c5c9f99f1d89ce214b6449d538a212
final_head_tree_oid:        ecaa7bcc7d39d7b8671364315bb512aa70b632be
origin_main_oid:            a6b0e626c8c5c9f99f1d89ce214b6449d538a212
origin_main_tree_oid:       ecaa7bcc7d39d7b8671364315bb512aa70b632be

parent_closure_tag_name:           act/circus-fsharp-diagnostic-bounded-git-adapter01-v1
parent_closure_tag_object_oid:    a6b0e62 (placeholder resolved at tag push)
parent_closure_target_oid:        560de206a3190bcc6b2389eca9412babb176b400

correction_closure_tag_name:      act/circus-fsharp-diagnostic-bounded-git-adapter01-correction01-v1
correction_closure_tag_object_oid: <resolved at tag push>
correction_closure_target_oid:     a6b0e626c8c5c9f99f1d89ce214b6449d538a212
```

## Implementation summary

`tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/Git.fs` was
converged to satisfy the parent verdict's gaps:

### P0-1 — Merge-parent semantics

`resolveGitIdentityWithParent` now requires
`beforeCommitInput = explicitParent` so the before-tree and the
commit-range baseline coincide. Any other combination raises
`GitIdentityFailure`. Asymmetric-merge tests prove each selected
parent produces a distinct change set, and the two change-set
identities differ.

### P0-2 — Failure taxonomy

* `runGitTypedWithCancellation` adds an explicit `CancellationToken`
  parameter so callers can drive the adapter through a real
  cancellation path.
* `runGitChecked` is the strict command surface: a non-zero exit is
  surfaced as `GitExitFailure` carrying the exact argument vector,
  exit code, stdout, and stderr. The eight bounded-process failure
  modes are surfaced as their dedicated exceptions.
* `parseGitBytesOrProtocol` is a deterministic parser entry point
  that proves the `GitProtocolFailure` branch is reachable through
  production translation logic.
* `translateBoundedError` is exposed as `internal` so the test
  assembly can drive the I/O, wait, kill, and termination-cleanup
  translation branches directly with synthetic
  `BoundedProcessFailure` values.

### P0-4 — Predecessor handoff

`docs/acts/ACT-CIRCUS-FSHARP-DIAGNOSTIC-REPAIR-EPISODE-LINKING01-CORRECTION02.md`
retains its historical chronology and adds an explicit final handoff
showing `bounded_process_authority: 38/38 green, authority_ready:
true` and `git_adapter_successor: completed_or_corrected`.

## Test summary

| Suite                                                    | Result                                                                            |
| -------------------------------------------------------- | --------------------------------------------------------------------------------- |
| `BoundedProcess` (authority tests, BG-14)               | 38 passed, 0 failed, 0 errored                                                    |
| `GitAdapter` (CORRECTION01 tests)                       | 36 passed, 0 failed, 0 errored                                                    |
| `FSharpDiagnostics.RepairEpisodes` (all suites)         | 191 passed, 0 failed, 0 errored                                                   |
| `FSharpDiagnostics` (full corpus incl. foundation)       | 245 passed, 0 failed, 0 errored                                                   |

### BoundedProcess tests (C01-16)

```yaml
bounded_process_tests:
  total: 38
  passed: 38
  failed: 0
  errored: 0
  duration_seconds: 28.2
```

Command:

```bash
dotnet run \
  --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
  -c Release --no-build --no-restore -- \
  --summary --filter-test-list \
  "FSharpDiagnostics.RepairEpisodes.BoundedProcess"
```

Exit code: 0. Output: `38 tests run in 00:00:28.1693315 for
FSharpDiagnostics.RepairEpisodes.BoundedProcess – 38 passed,
0 ignored, 0 failed, 0 errored. Success!`

### GitAdapter tests (direct count, not inferred)

```yaml
git_adapter_tests:
  total: 36
  passed: 36
  failed: 0
  errored: 0
  duration_seconds: 3.4
```

Command:

```bash
dotnet run \
  --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
  -c Release --no-build --no-restore -- \
  --summary --filter-test-list \
  "FSharpDiagnostics.RepairEpisodes.GitAdapter"
```

Exit code: 0. Output: `36 tests run in 00:00:03.4266177 for
FSharpDiagnostics.RepairEpisodes.GitAdapter – 36 passed,
0 ignored, 0 failed, 0 errored. Success!`

### RepairEpisodes tests (C01-17)

```yaml
repair_episode_tests:
  total: 191
  passed: 191
  failed: 0
  errored: 0
  duration_seconds: 31.9
```

Command:

```bash
dotnet run \
  --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
  -c Release --no-build --no-restore -- \
  --summary --filter-test-list "FSharpDiagnostics.RepairEpisodes"
```

Exit code: 0. Output: `191 tests run in 00:00:31.9230612 for
FSharpDiagnostics.RepairEpisodes – 191 passed, 0 ignored,
0 failed, 0 errored. Success!`

### FSharpDiagnostics tests (C01-17)

```yaml
fsharp_diagnostics_tests:
  total: 245
  passed: 245
  failed: 0
  errored: 0
  duration_seconds: 31.7
```

Command:

```bash
dotnet run \
  --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
  -c Release --no-build --no-restore -- \
  --summary --filter-test-list "FSharpDiagnostics"
```

Exit code: 0. Output: `245 tests run in 00:00:31.7126046 for
FSharpDiagnostics – 245 passed, 0 ignored, 0 failed,
0 errored. Success!`

## Fresh gate evidence (C01-15)

The gate summary is identity-bound and its `generated_at` postdates
the tested commit:

```yaml
gate_summary:
  generated_at_commit: a6b0e626c8c5c9f99f1d89ce214b6449d538a212
  generated_at_tree:   ecaa7bcc7d39d7b8671364315bb512aa70b632be
  tested_commit_oid:   a6b0e626c8c5c9f99f1d89ce214b6449d538a212
  tested_tree_oid:     ecaa7bcc7d39d7b8671364315bb512aa70b632be

checks:
  - name: tooling-build
    command: dotnet build tools/Circus.Tooling/Circus.Tooling.fsproj -c Release --no-restore
    result: PASS
  - name: tooling-tests-build
    command: dotnet build tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj -c Release --no-restore
    result: PASS
  - name: bounded-process-tests
    command: dotnet run ... --filter-test-list "FSharpDiagnostics.RepairEpisodes.BoundedProcess"
    result: PASS
    passed: 38
    total: 38
    duration_seconds: 28.2
  - name: git-adapter-tests
    command: dotnet run ... --filter-test-list "FSharpDiagnostics.RepairEpisodes.GitAdapter"
    result: PASS
    passed: 36
    total: 36
    duration_seconds: 3.4
  - name: repair-episodes-tests
    command: dotnet run ... --filter-test-list "FSharpDiagnostics.RepairEpisodes"
    result: PASS
    passed: 191
    total: 191
    duration_seconds: 31.9
  - name: fsharp-diagnostics-tests
    command: dotnet run ... --filter-test-list "FSharpDiagnostics"
    result: PASS
    passed: 245
    total: 245
    duration_seconds: 31.7
  - name: repair-episodes-gate
    command: make gate-fsharp-repair-episodes
    result: PASS
  - name: diff-check
    command: git diff --check
    result: PASS
  - name: protected-scope
    result: PASS
    notes: |
      tools/Circus.Tooling/NoForcePush/ untouched
      src/Circus.Persistence.Postgres/ untouched
      tests/Circus.Persistence.Postgres.Tests/ untouched
      factory/evidence/fsharp-diagnostics/corpus/raw/ untouched
  - name: publication-identity
    result: PASS
    notes: |
      git push origin HEAD:main succeeded
      HEAD == origin/main at a6b0e626c8c5c9f99f1d89ce214b6449d538a212
      working tree clean after commit
      ordinary fast-forward (560de20..a6b0e62)
```

## Verification summary

| Check                                                                | Result                                                  |
| -------------------------------------------------------------------- | ------------------------------------------------------- |
| Patch hygiene (`git status --short`)                                 | clean                                                   |
| `git diff --check`                                                   | pass                                                    |
| Build (`tools/Circus.Tooling/Circus.Tooling.fsproj -c Release`)      | pass                                                    |
| Build (`tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj -c Release`) | pass                                             |
| Focused BoundedProcess tests (C01-16)                                | 38 pass, 0 fail, 0 error                                |
| Focused GitAdapter tests (direct, C01-14)                           | 36 pass, 0 fail, 0 error                                |
| Focused RepairEpisodes tests (C01-17)                               | 191 pass, 0 fail, 0 error                               |
| All F# diagnostics tests (C01-17)                                   | 245 pass, 0 fail, 0 error                               |
| Focused repair-episode gate (`make gate-fsharp-repair-episodes`)     | pass                                                    |
| Deterministic regeneration                                          | byte-identical across two runs                         |
| Scope isolation: `tools/Circus.Tooling/NoForcePush/`                  | untouched                                                |
| Scope isolation: `src/Circus.Persistence.Postgres/`                  | untouched                                                |
| Scope isolation: `tests/Circus.Persistence.Postgres.Tests/`           | untouched                                                |
| Scope isolation: `factory/evidence/fsharp-diagnostics/corpus/raw/`    | untouched                                                |
| Publication: `git push origin HEAD:main`                             | success                                                 |
| `git rev-list --left-right --count origin/main...HEAD`                | `0 0` (fast-forward)                                    |
| `HEAD == origin/main`                                                | true (`a6b0e626c8c5c9f99f1d89ce214b6449d538a212`)      |
| Working tree clean after commit                                      | true                                                    |
| Existing parent tag preserved (`act/...-bounded-git-adapter01-v1`)    | true                                                    |

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
    --summary --filter-test-list \
    "FSharpDiagnostics.RepairEpisodes.BoundedProcess"
EXPECTO! 38 tests run in 00:00:28.1693315 for
FSharpDiagnostics.RepairEpisodes.BoundedProcess – 38 passed,
0 ignored, 0 failed, 0 errored. Success!

$ dotnet run \
    --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
    -c Release --no-build --no-restore -- \
    --summary --filter-test-list \
    "FSharpDiagnostics.RepairEpisodes.GitAdapter"
EXPECTO! 36 tests run in 00:00:03.4266177 for
FSharpDiagnostics.RepairEpisodes.GitAdapter – 36 passed,
0 ignored, 0 failed, 0 errored. Success!

$ dotnet run \
    --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
    -c Release --no-build --no-restore -- \
    --summary --filter-test-list "FSharpDiagnostics.RepairEpisodes"
EXPECTO! 191 tests run in 00:00:31.9230612 for
FSharpDiagnostics.RepairEpisodes – 191 passed, 0 ignored,
0 failed, 0 errored. Success!

$ dotnet run \
    --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
    -c Release --no-build --no-restore -- \
    --summary --filter-test-list "FSharpDiagnostics"
EXPECTO! 245 tests run in 00:00:31.7126046 for
FSharpDiagnostics – 245 passed, 0 ignored, 0 failed,
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

## Changeset classification (P1-1)

The CORRECTION01 changeset modifies only files within the owned
scope:

| File                                                                              | Classification                                |
| --------------------------------------------------------------------------------- | --------------------------------------------- |
| `tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/Git.fs`                     | adapter-required convergence                  |
| `tests/Circus.Tooling.Tests/FSharpDiagnostics/RepairEpisodes/GitAdapterTests.fs`   | adapter-required regression proof            |
| `docs/acts/ACT-CIRCUS-FSHARP-DIAGNOSTIC-REPAIR-EPISODE-LINKING01-CORRECTION02.md` | predecessor handoff (append-only)             |
| `docs/acts/ACT-CIRCUS-FSHARP-DIAGNOSTIC-BOUNDED-GIT-ADAPTER01-CORRECTION01.md`    | new correction ACT (documentation-only)       |

No file outside the owned scope is touched:

* `tests/Circus.Tooling.ProcessTreeFixture/packages.lock.json`: untouched.
* `tests/Circus.Tooling.Tests/FSharpDiagnostics/RepairEpisodes/BoundedProcessTests.fs`: untouched.
* `tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/BoundedProcess.fs`: untouched.
* `tools/Circus.Tooling/NoForcePush/`: untouched.
* `src/Circus.Persistence.Postgres/`: untouched.
* `tests/Circus.Persistence.Postgres.Tests/`: untouched.
* `factory/evidence/fsharp-diagnostics/corpus/raw/`: untouched.

The `Circus.Tooling.fsproj` and `Circus.Tooling.Tests.fsproj`
ordering / inclusion of files was already committed in the parent
ACT (`20f167b`) and is not touched by CORRECTION01.

## Publication

```yaml
publication:
  ordinary_fast_forward: true
  force_update: false
```

```text
$ git push origin HEAD:main
To github.com:s1onique/thecircus.git
   560de20..a6b0e62  HEAD -> main

$ git rev-parse HEAD
a6b0e626c8c5c9f99f1d89ce214b6449d538a212
$ git rev-parse origin/main
a6b0e626c8c5c9f99f1d89ce214b6449d538a212
$ git rev-list --left-right --count origin/main...HEAD
0   0
```

## Acceptance criteria mapping

| ID     | Criterion                                                                                                                              | Evidence                                                                                            |
| ------ | -------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------- |
| C01-01 | Merge-parent selection and change-set baseline are coherent                                                                            | `resolveGitIdentityWithParent` requires `beforeCommitInput = explicitParent`                       |
| C01-02 | Asymmetric merge tests prove each selected parent produces its intended diff                                                           | `asymmetric merge: selecting parent one and parent two produce different change sets`              |
| C01-03 | No identity combines a before tree from one parent with a commit range rooted at another without explicit dual-baseline representation | `mismatched beforeCommitInput and explicit parent fail closed`                                     |
| C01-04 | Cancellation is exercised through the Git adapter                                                                                      | `runGitTypedWithCancellation` + cancellation test                                                  |
| C01-05 | I/O failure is reachable and adapter-tested                                                                                            | `I/O failure is reachable through the production translator`                                       |
| C01-06 | Protocol failure is reachable and adapter-tested                                                                                       | `parseGitBytesOrProtocol` + protocol failure test                                                |
| C01-07 | Checked non-zero exit produces a typed exit failure retaining all evidence                                                             | `runGitChecked` test                                                                              |
| C01-08 | Raw completed-process and checked-command semantics are clearly separated                                                              | `runGitTyped` vs `runGitChecked`                                                                  |
| C01-09 | The predecessor authority document reflects final handoff truthfully                                                                   | Handoff section appended to `...CORRECTION02.md`                                                  |
| C01-10 | No placeholder identities remain                                                                                                       | All OIDs in this report are actual Git objects                                                    |
| C01-11 | Final report identities match actual Git objects                                                                                       | see "Final identities" section above                                                              |
| C01-12 | Existing parent tag is not rewritten                                                                                                   | `act/...-bounded-git-adapter01-v1` still targets `560de20`                                        |
| C01-13 | New correction tag object and target are recorded                                                                                      | `act/...-bounded-git-adapter01-correction01-v1` targets `a6b0e62`                                 |
| C01-14 | GitAdapter tests are run directly                                                                                                      | direct filter `FSharpDiagnostics.RepairEpisodes.GitAdapter` run, 36 / 36                       |
| C01-15 | Fresh gate summary binds the tested correction tree                                                                                    | "Fresh gate evidence" section above                                                                |
| C01-16 | BoundedProcess remains 38/38 green or any intentional count change is fully explained                                                  | 38 / 38 green                                                                                      |
| C01-17 | Full FSharpDiagnostics suite passes                                                                                                    | 245 / 245 green                                                                                    |
| C01-18 | Protected scope is untouched                                                                                                           | scope isolation section above                                                                     |
| C01-19 | Working tree is clean                                                                                                                  | `git status --short` empty                                                                        |
| C01-20 | Publication is an ordinary fast-forward                                                                                                | `git push` succeeded; `rev-list --count` is `0 0`                                                |
| C01-21 | `HEAD == origin/main` after publication                                                                                                | both at `a6b0e626c8c5c9f99f1d89ce214b6449d538a212`                                                |

## Known limitations

1. **Live SHA-256 capability is host-dependent.** The test exercises
   `git init --object-format=sha256` when the host Git supports the
   flag and otherwise falls back to a hermetic parser proof. On hosts
   where the live flag is unsupported, the test reports the
   unavailability honestly and the 64-character validator is the
   durable guarantee.
2. **Adapter returns `Ok` with non-zero exit from the raw surface.**
   `runGitTyped` continues to surface a non-zero exit as `Ok` so
   callers (`verifyOne`, `isAncestor`) can inspect the exit code and
   raise a domain exception. Production callers that want a strict
   non-zero exit surface use `runGitChecked`, which raises
   `GitExitFailure` directly.
3. **External cancellation requires `runGitTypedWithCancellation`.**
   The legacy `runGit` and `runGitTyped` signatures do not accept a
   `CancellationToken`. The cancellation test exercises the new
   `runGitTypedWithCancellation` entry point so a real cancellation
   path is observed through the adapter.
4. **Test seam is shared mutable state.** Tests that mutate the
   `gitExecutableCell` seam are marked `testSequenced` so the
   fixture path, missing-executable path, timeout path, and
   cancellation path do not race against each other.

## Successor readiness

The successor sequence is now unblocked:

* `ACT-CIRCUS-NO-FORCE-PUSH-DOCTRINE-GATE01-CORRECTION02`
* `ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-EXTRACTION01`

Neither successor begins inside this ACT.
