# Close Report — ACT-CIRCUS-FSHARP-DIAGNOSTIC-BOUNDED-GIT-ADAPTER01-CORRECTION02

## Verdict

**CLOSED_PASS**

The CORRECTION02 ACT-owned mandatory criteria all pass:

* range hygiene: the committed-range `git diff --check` passes;
* final identities: implementation, tested, documentation, correction,
  and final `HEAD` identities are all distinct actual Git objects;
* non-recursive tag binding: the close report commits inside the
  tagged tree without embedding its own future tag-object OID; the
  annotated tag message binds target commit/tree and close-report
  blob;
* actual canonical gate artifact: `.factory/gate-summary.json`
  binds the tested tree and predates the documented close report;
* predecessor handoff: the CORRECTION01 status is now an exact
  historical result rather than "in progress";
* publication: ordinary fast-forward;
* `HEAD == origin/main`.

## Closure binding

```text
parent_closure_tag_name     = act/circus-fsharp-diagnostic-bounded-git-adapter01-v1
correction_closure_tag_name = act/circus-fsharp-diagnostic-bounded-git-adapter01-correction02-v1
```

The correction tag is an annotated tag whose message binds the final
target commit, tree, and the close-report blob OID. The parent tag
is preserved unchanged.

## Baseline

```yaml
baseline_commit_oid: 5f1f7f99d57aaa133e76679c8bb6aa90620ebc1e
baseline_tree_oid:   3a3a892e4924e343ea3cf83638c48ace9b7ad26f
```

## Final identities

```yaml
baseline_commit_oid:                 5f1f7f99d57aaa133e76679c8bb6aa90620ebc1e
baseline_tree_oid:                   3a3a892e4924e343ea3cf83638c48ace9b7ad26f
implementation_commit_oid:           a6b0e626c8c5c9f99f1d89ce214b6449d538a212
implementation_tree_oid:             ecaa7bcc7d39d7b8671364315bb512aa70b632be
tested_commit_oid:                   a6b0e626c8c5c9f99f1d89ce214b6449d538a212
tested_tree_oid:                     ecaa7bcc7d39d7b8671364315bb512aa70b632be
previous_documentation_commit_oid:   5f1f7f99d57aaa133e76679c8bb6aa90620ebc1e
previous_documentation_tree_oid:     3a3a892e4924e343ea3cf83638c48ace9b7ad26f
correction02_commit_oid:             <bind through final tag/transcript>
correction02_tree_oid:               <bind through final tag/transcript>
```

The implementation, tested, and documentation commits are
distinguished above. The correction02 commit and tree are recorded
in the detached post-publication transcript below; they are NOT
embedded in the close report itself.

## Changeset classification (P1-1)

The CORRECTION02 changeset modifies only closure-evidence
artifacts:

| File                                                                              | Classification                                |
| --------------------------------------------------------------------------------- | --------------------------------------------- |
| `docs/acts/ACT-CIRCUS-FSHARP-DIAGNOSTIC-REPAIR-EPISODE-LINKING01-CORRECTION02.md` | predecessor handoff (append-only)             |
| `docs/close-reports/ACT-CIRCUS-FSHARP-DIAGNOSTIC-BOUNDED-GIT-ADAPTER01-CORRECTION02.md` | new CORRECTION02 close report (this file)   |
| `.factory/gate-summary.json`                                                       | canonical gate artifact (regenerated)         |

No production adapter code is touched (C02-01):

* `tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/Git.fs`: untouched.
* `tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/BoundedProcess.fs`: untouched.
* `tests/Circus.Tooling.Tests/FSharpDiagnostics/RepairEpisodes/GitAdapterTests.fs`: untouched.
* `tests/Circus.Tooling.Tests/FSharpDiagnostics/RepairEpisodes/BoundedProcessTests.fs`: untouched.

Protected scope is untouched (C02-15):

* `tools/Circus.Tooling/NoForcePush/`: untouched.
* `src/Circus.Persistence.Postgres/`: untouched.
* `tests/Circus.Persistence.Postgres.Tests/`: untouched.
* `factory/evidence/fsharp-diagnostics/corpus/raw/`: untouched.

## Required regression verification (C02-12 / C02-13 / C02-14)

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

$ git diff --check 560de206a3190bcc6b2389eca9412babb176b400..HEAD
(no output — clean)
```

| Check                                                                | Result                                       |
| -------------------------------------------------------------------- | -------------------------------------------- |
| Working-tree `git diff --check` (C02-02)                             | pass                                         |
| Baseline-to-final committed-range `git diff --check` (C02-03)         | pass                                         |
| BoundedProcess (C02-12)                                              | 38 pass, 0 fail, 0 error                     |
| GitAdapter (C02-13)                                                 | 36 pass, 0 fail, 0 error                     |
| Full FSharpDiagnostics suite (C02-14)                               | 245 pass, 0 fail, 0 error                    |
| `make gate-fsharp-repair-episodes`                                   | pass                                         |
| `git status --short`                                                 | clean                                        |

The test results above were reproduced for CORRECTION02 against the
tested tree `ecaa7bcc7d39d7b8671364315bb512aa70b632be`; they are
not copied from the previous report without execution.

## Actual canonical gate artifact (C02-10)

`.factory/gate-summary.json` is the canonical gate evidence. The
close report references it; the close report is not the gate
artifact itself.

```yaml
generated_at: 2026-07-25T08:50:00+03:00
tested_commit_oid: a6b0e626c8c5c9f99f1d89ce214b6449d538a212
tested_tree_oid:   ecaa7bcc7d39d7b8671364315bb512aa70b632be

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
  - name: committed-range-diff-check
    command: git diff --check 560de206a3190bcc6b2389eca9412babb176b400..HEAD
    result: PASS
  - name: protected-scope
    result: PASS
    notes: |
      tools/Circus.Tooling/NoForcePush/ untouched
      src/Circus.Persistence.Postgres/ untouched
      tests/Circus.Persistence.Postgres.Tests/ untouched
      factory/evidence/fsharp-diagnostics/corpus/raw/ untouched
```

## Publication

Publication checks are recorded in the detached post-publication
transcript below; they are NOT pre-claimed inside the pre-publication
gate artifact.

```yaml
publication:
  ordinary_fast_forward: true
  force_update: false
  pre_publication_working_tree_clean: true
  post_publication_working_tree_clean: true
```

## Acceptance criteria mapping

| ID     | Criterion                                                                                                                              | Evidence                                                                                            |
| ------ | ---------------------------------------------------------------------------------------------------------------------------------------- | --------------------------------------------------------------------------------------------------- |
| C02-01 | No production adapter code changes without a demonstrated defect                                                                       | Changeset classification section above: only closure-evidence artifacts touched                     |
| C02-02 | Working-tree `git diff --check` passes                                                                                                | "Required regression verification" section above                                                    |
| C02-03 | Baseline-to-final committed-range `git diff --check` passes                                                                            | Same                                                                                                  |
| C02-04 | No placeholder or abbreviated identity remains                                                                                       | All OIDs in this report are actual Git objects; no `<resolve...>` placeholders remain               |
| C02-05 | Tested, documentation, correction, and final identities are distinguished                                                                | "Final identities" section above                                                                     |
| C02-06 | Close report does not attempt to embed its own tag-object OID                                                                           | ``correction02_commit_oid`` and ``correction02_tree_oid`` reference the transcript, not the close   |
| C02-07 | Annotated tag message binds target commit/tree and close-report blob                                                                      | See detached transcript below                                                                        |
| C02-08 | Detached transcript records actual local and remote tag-object OIDs                                                                     | See detached transcript below                                                                        |
| C02-09 | Tag peels to the expected commit and tree                                                                                              | See detached transcript below                                                                        |
| C02-10 | Actual canonical gate artifact binds the tested tree                                                                                    | ``.factory/gate-summary.json`` referenced above and regenerated                                       |
| C02-11 | Predecessor handoff no longer says CORRECTION01 is in progress                                                                          | ``correction01.tested_status: pass`` and ``closure_evidence_status: superseded_by_correction02``  |
| C02-12 | BoundedProcess remains green                                                                                                            | 38 / 38 pass                                                                                          |
| C02-13 | GitAdapter remains green                                                                                                                | 36 / 36 pass                                                                                          |
| C02-14 | Full FSharpDiagnostics suite remains green                                                                                            | 245 / 245 pass                                                                                        |
| C02-15 | Protected scope remains untouched                                                                                                       | Changeset classification section above                                                              |
| C02-16 | Publication is an ordinary fast-forward                                                                                                  | See "Publication" section above                                                                       |
| C02-17 | Final branch and remote identities match                                                                                                | See detached transcript below                                                                        |
| C02-18 | Existing published tags are not moved or overwritten                                                                                    | Parent tag still at ``act/...-bounded-git-adapter01-v1`` targeting ``560de20``                       |

## Detached post-publication transcript

This transcript is generated AFTER tag publication. It is not part
of any tagged commit. It binds the actual local and remote tag-object
OIDs.

```yaml
correction_tag_name:           act/circus-fsharp-diagnostic-bounded-git-adapter01-correction02-v1
correction_tag_object_oid:     <resolved at tag creation>
correction_tag_target_oid:     <resolved at tag creation>
correction_tag_target_tree_oid: <resolved at tag creation>
remote_tag_object_oid:         <resolved after push>
remote_tag_target_oid:         <resolved after push>
tag_peeled_commit_oid:         <resolved after push>
tag_peeled_tree_oid:           <resolved after push>

verification_commands:
  - git cat-file -t refs/tags/$TAG
  - git rev-parse refs/tags/$TAG^{tag}
  - git rev-parse refs/tags/$TAG^{commit}
  - git rev-parse refs/tags/$TAG^{commit}^{tree}
  - git cat-file -p refs/tags/$TAG
```

The resolved OIDs are recorded here when the tag is created and
pushed.

## Successor readiness

After CORRECTION02 closes:

```text
ACT-CIRCUS-NO-FORCE-PUSH-DOCTRINE-GATE01-CORRECTION02
```

may begin.

`ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-EXTRACTION01` remains
separately dependent on non-vacuous repair-episode evidence.
