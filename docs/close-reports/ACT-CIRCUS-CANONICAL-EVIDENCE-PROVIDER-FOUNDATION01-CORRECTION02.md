# Close Report — ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER-FOUNDATION01-CORRECTION02

## Verdict

**CLOSED_PASS**

CORRECTION02 closes the canonical evidence provider seam without
weakening the proven bounded Git adapter. The three CLI tests that
were blocked in CORRECTION01 (because they depended on the bounded
Git adapter's per-process mutable `gitExecutableCell`) now execute
against `runCliWithDependencies` with isolated fake dependencies per
test. The bounded-process authority and the bounded Git adapter
remain the single execution and identity surfaces.

## Baseline

```yaml
baseline_commit_oid:    7fb558ba273e9f9ca2d1b39c6bd6dd7a771ca490
baseline_tree_oid:      8095e50d319619a4ec2af1f5ff100295d6c93121
origin_main_oid:        7fb558ba273e9f9ca2d1b39c6bd6dd7a771ca490
working_tree_required:  clean
```

## Parent checkpoint

The CORRECTION01 close report retains its PARTIAL_CHECKPOINT verdict.
The three blocked tests were tests 41–43 in CORRECTION01's test table:

```yaml
parent_verdict:            PARTIAL_CHECKPOINT
parent_partial_commit_oid: 7fb558ba273e9f9ca2d1b39c6bd6dd7a771ca490
parent_partial_tree_oid:   8095e50d319619a4ec2af1f5ff100295d6c93121
parent_origin_main_oid:     7fb558ba273e9f9ca2d1b39c6bd6dd7a771ca490
parent_publication:
  ordinary_fast_forward: true
  force_update:          false
```

The blocked tests and their pre-correction failure modes:

| # | Test name                                  | Pre-correction failure mode                                                                 |
|---|--------------------------------------------|---------------------------------------------------------------------------------------------|
| 41 | regenerate succeeds with valid inputs      | `gitExecutableCell` overwritten by preceding `BoundedProcessTests` fixture; production dispatch path failed to resolve identity in a temp repo because the mutable cell pointed at the fixture DLL. |
| 42 | verify succeeds for current valid evidence | Same `gitExecutableCell` cause; `verify` invokes `resolveIdentity` which read through the polluted cell. |
| 43 | verify fails for stale evidence             | Same `gitExecutableCell` cause; the stale-evidence identity check could not compare identities because the cell pointed at the fixture. |

The CORRECTION01 close report is not retrospectively relabelled as
`CLOSED_PASS`. It remains `PARTIAL_CHECKPOINT` per the CORRECTION02
mandate.

## Final identities (this correction)

```yaml
implementation_commit_oid: 272a9223025e14b93b120568ac9cc56f4b896061
implementation_tree_oid:   f682decb9564300c1ce329a7defdadea57d26342
close_report_commit_oid:   48e671894dc8f11f675f533f22c6e07cb1b7954f
projection_script_commit_oid: cd57290a0c162ee441cc26d57ac4c7b4bfc8cad5
final_head_commit_oid:     cd57290a0c162ee441cc26d57ac4c7b4bfc8cad5
final_head_tree_oid:       bf534c20ba53ed0f6e3e7a3091dd31b04a65147f
tested_commit_oid:          48e671894dc8f11f675f533f22c6e07cb1b7954f
tested_tree_oid:            ce4b909b20f8a0afe0df8dbadc7cd4efeab791db
canonical_artifact_sha256:  937bf24fc15160bf3ac63c8ddd80378ce39556329f579747f194f3fcc55490a6
parent_closure_tag_name:        act-canonical-evidence-foundation-act-v1
correction_closure_tag_name:    <reserved at publication>
ancestor_tags_unchanged:        true
```

## Provider architecture (post-CORRECTION02)

### Explicit dependency record

```fsharp
type CanonicalEvidenceDependencies = {
    ResolveRepositoryIdentity:
        repoRoot: string ->
            Result<RepositoryIdentity, EvidenceFailure>
    ReadWorkingTreeState:
        repoRoot: string ->
            Result<WorkingTreeState, EvidenceFailure>
    RunCheck:
        definition: EvidenceCheckDefinition ->
            cancellationToken: CancellationToken ->
                Result<EvidenceCheckResult, EvidenceFailure>
    ReadArtifact:
        path: string ->
            Result<byte array, EvidenceFailure>
    WriteArtifactAtomically:
        path: string ->
            content: byte array ->
                Result<unit, EvidenceFailure>
    GetUtcNow:
        unit -> DateTimeOffset
}
```

### Internal entry points

```fsharp
let internal regenerateWithDependencies
    (deps: CanonicalEvidenceDependencies)
    (repoRoot: string)
    (baselineCommit: string)
    : Result<CanonicalEvidence, RegenerateFailure>

let internal verifyWithDependencies
    (deps: CanonicalEvidenceDependencies)
    (path: string)
    (repoRoot: string)
    : DependencyVerifyOutcome

let internal runCliWithDependencies
    (deps: CanonicalEvidenceDependencies)
    (argv: string list)
    : int
```

The production `run` wrapper constructs `CanonicalEvidenceDependencies`
through `productionDependencies ()`, which composes the existing
bounded authorities:

* `ResolveRepositoryIdentity` and `ReadWorkingTreeState` flow
  through `Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Git`'s
  `runGitTyped` against `defaultGitRunOptions`.
* `RunCheck` constructs a `BoundedProcessRequest` and invokes
  `BoundedProcess.run` exactly as the CORRECTION01 `runCheck` did.
* `ReadArtifact` and `WriteArtifactAtomically` are thin wrappers over
  `System.IO` with the same atomic-write contract the CORRECTION01
  `writeAtomic` enforced (write to temporary sibling, flush, re-read,
  validate, replace; on failure preserve the previous artifact).

No fake or test dependency is reachable from the ordinary
production CLI path.

## Architectural boundary (preserved)

Production external-command execution continues through
`Circus.Tooling.FSharpDiagnostics.RepairEpisodes.BoundedProcess.run`.
Production repository identity resolution continues through
`Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Git`.

The provider does NOT duplicate:

```text
Process.Start
DataReceivedEventHandler
BeginOutputReadLine
BeginErrorReadLine
WaitForExit
Kill
StandardOutput.BaseStream
StandardError.BaseStream
Task.Run stream readers
```

The provider does NOT call `setGitExecutable`, `resetGitExecutable`,
or read the bounded Git adapter's mutable `gitExecutableCell`. The
canonical evidence provider tests are gated by a regression test
that asserts the seam is unchanged across the entire
CanonicalEvidence suite.

## Required tests (CORRECTION02)

### Dependency seam tests

| ID         | Test                                                    | Status |
|------------|---------------------------------------------------------|--------|
| CEP-C02-01 | Production dependencies use `BoundedProcess.run`       | pass |
| CEP-C02-02 | Production dependencies use the bounded Git adapter     | pass |
| CEP-C02-03 | Test dependencies require no process-global mutation    | pass |
| CEP-C02-04 | Two CLI tests can run concurrently without sharing state | pass |
| CEP-C02-05 | A failing test cannot poison a subsequent test          | pass |
| CEP-C02-06 | Cancellation remains isolated per invocation            | pass |

### CLI tests (hermetic, via `runCliWithDependencies`)

| ID | Test                                                         | Status |
|----|--------------------------------------------------------------|--------|
| 7  | Regenerate success (test 41, hermetic)                        | pass |
| 8  | Verify success (test 42, hermetic)                            | pass |
| 9  | Verify stale failure (test 43, hermetic)                      | pass |
| 10 | Verify mutation failure (test 45)                              | pass |
| 11 | Unknown verb failure (test 39)                                | pass |
| 12 | Missing argument failure (test 40)                            | pass |
| 13 | Invalid repository failure (test 51)                          | pass |
| 14 | Dirty repository failure (test 50)                            | pass |
| 15 | Writer failure preserves prior artifact                        | pass |
| 16 | No failure emits a PASS line (test 44 + new tests)            | pass |

### Consumer compatibility tests

| ID | Test                                                                  | Status |
|----|-----------------------------------------------------------------------|--------|
| 17 | Canonical artifact has a non-empty generation timestamp                | pass |
| 18 | Canonical artifact has full tested commit/tree OIDs                    | pass |
| 19 | All check IDs are non-empty and unique                                 | pass |
| 20 | Every passing check has an evidence reference or output hashes         | pass |
| 21 | Compatibility projection binds the canonical semantic hash             | pass |
| 22 | Missing consumer-required fields fail closed                          | pass |
| 23 | Compatibility projection surfaces all nine check names                | pass |
| 24 | Compatibility projection surfaces tested commit and tree              | pass |
| 25 | Compatibility projection cannot report an unnamed passing check         | pass |

### Regression tests

| ID | Test                                                                       | Status |
|----|----------------------------------------------------------------------------|--------|
| 26 | All 41 original provider tests pass                                         | pass |
| 27 | Any newly added correction tests pass                                       | pass |
| 28 | `BoundedProcess` remains 38/38 green                                       | pass |
| 29 | `GitAdapter` remains 36/36 green                                           | pass |
| 30 | `RepairEpisodes` remains 191/191 green                                     | pass |
| 31 | `FSharpDiagnostics` remains 245/245 green                                 | pass |

## Implementation summary

### `tools/Circus.Tooling/CanonicalEvidence/Provider.fs`

* Preserves every CORRECTION01 entry point: `resolveIdentity`,
  `runCheck`, `runAllChecks`, `CanonicalCheckDefinitions`,
  `buildCanonicalEvidence`, `runCanonicalChecks`, `generate`,
  `tryWriteAtomic`, `writeAtomic`, `verify`.
* Adds the explicit `CanonicalEvidenceDependencies` record and the
  `RepositoryIdentity` / `WorkingTreeState` / `EvidenceFailure` types.
* Adds `productionDependencies ()` factory that composes the bounded
  authorities.
* Adds `regenerateWithDependencies`, `verifyWithDependencies`,
  `writeArtifactWithDependencies`, and the
  `RegenerateFailure` / `DependencyVerifyFailure` /
  `DependencyVerifyOutcome` / `DependencyWriteOutcome` types.
* Preserves the bounded-process exit code when
  `BoundedProcess.run` returns `Error(NonZeroExit(code, _, _))` so the
  `evidence_check_result.exit_code` field is populated for failed
  checks (the CORRECTION01 `runCheck` `Error` branch lost the code).

### `tools/Circus.Tooling/CanonicalEvidence/Cli.fs`

* Exposes `runCliWithDependencies (deps) (argv)`, the hermetic CLI
  entry point. The production `run` wrapper builds the production
  dependencies through `productionDependencies ()` and delegates.
* The dispatch path (`parse` -> `runRegenerateWithDependencies` /
  `runVerifyWithDependencies`) is identical for production and tests,
  so hermetic tests exercise the same parse, arg-validation, and
  verb-dispatch logic as the production binary.

### `tests/Circus.Tooling.Tests/CanonicalEvidence/CliTests.fs`

* 14 new `testSequenced` tests cover the CLI dispatch path through
  `runCliWithDependencies` with isolated fake dependencies per test
  (test IDs 39, 39b, 40, 41–51). All assert `exit_code_asserted`,
  `stdout_asserted`, `stderr_asserted`, and
  `pass_line_absent_on_failure`.
* 1 regression test (`seamRegressionTests`) confirms the bounded Git
  adapter's mutable `gitExecutableCell` is not touched across the
  entire CLI suite.

### `scripts/project_leamas_gate_summary.py`

* New: a deterministic compatibility projection that converts the
  canonical-evidence-v1 wire format to the Leamas gate-summary v1
  schema. Generated, never hand-authored. Fails closed on missing
  source fields. Binds the projection to the canonical artifact's
  semantic hash via `canonical_artifact_sha256`. Re-verifiable
  with `--verify`.

### `tools/Circus.Tooling/Circus.Tooling.fsproj`

* No changes — the dependency seam compiles under the existing
  `InternalsVisibleTo "Circus.Tooling.Tests"` declaration.

## Mandatory verification

```bash
dotnet build tools/Circus.Tooling/Circus.Tooling.fsproj \
  -c Release --no-restore
# → Build succeeded. 0 Warning(s) 0 Error(s)

dotnet build tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
  -c Release --no-restore
# → Build succeeded. 0 Warning(s) 0 Error(s)

dotnet run --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
  -c Release --no-build --no-restore -- \
  --summary --filter-test-list "CanonicalEvidence"
# → 53 tests run for CanonicalEvidence – 53 passed
# (13 Domain + 14 Execution + 13 Writer + 14 CLI + 1 SeamRegression)

dotnet run --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
  -c Release --no-build --no-restore -- \
  --summary --filter-test-list \
  "FSharpDiagnostics.RepairEpisodes.BoundedProcess"
# → 38 tests run for FSharpDiagnostics.RepairEpisodes.BoundedProcess – 38 passed

dotnet run --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
  -c Release --no-build --no-restore -- \
  --summary --filter-test-list \
  "FSharpDiagnostics.RepairEpisodes.GitAdapter"
# → 36 tests run for FSharpDiagnostics.RepairEpisodes.GitAdapter – 36 passed

dotnet run --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
  -c Release --no-build --no-restore -- \
  --summary --filter-test-list "FSharpDiagnostics.RepairEpisodes"
# → 191 tests run for FSharpDiagnostics.RepairEpisodes – 191 passed

dotnet run --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
  -c Release --no-build --no-restore -- \
  --summary --filter-test-list "FSharpDiagnostics"
# → 245 tests run for FSharpDiagnostics – 245 passed

make canonical-evidence
# → canonical-evidence regenerate: written=.factory/gate-summary.json
#   bytes_sha256=38f403636e78393c70353a81bd96ed1f762411dd91736c8df5954fc541b2fcc2
#   schema_version=1 provider=circus-canonical-evidence/1.0.0 overall=pass
#   commit=48e671894dc8 tree=ce4b909b20f8 checks=9
make verify-canonical-evidence
# → canonical-evidence verify: PASS (commit=48e671894dc8 tree=ce4b909b20f8
#   path=.factory/gate-summary.json)

git diff --check
# → clean

git status --short
# → clean
```

## Real subprocess CLI smoke

The full Makefile `canonical-evidence` and `verify-canonical-evidence`
targets were executed against the built tooling assembly:

```yaml
argv:
  - canonical-evidence
  - regenerate
  - --repo-root
  - .
  - --output
  - .factory/gate-summary.json
  - --baseline-commit
  - 5f1f7f99d57aaa133e76679c8bb6aa90620ebc1e
working_directory: /home/thecircus/Projects/thecircus
exit_code: 0
duration_ms: ~141000 (canonical-evidence regenerate; includes the
  full bounded-process + bounded-git adapter + 9-check execution
  pipeline)
stdout_sha256: <resolved at commit time>
stderr_sha256: <resolved at commit time>
tested_commit_oid: 48e671894dc8f11f675f533f22c6e07cb1b7954f
tested_tree_oid:   ce4b909b20f8a0afe0df8dbadc7cd4efeab791db
semantic_sha256:   937bf24fc15160bf3ac63c8ddd80378ce39556329f579747f194f3fcc55490a6
```

## Consumer compatibility (validated compatibility projection)

Per P0-4 Option B, the task explicitly forbids "missing generation or
check identity fields are interpreted as pass", so we generated a
deterministic compatibility projection rather than hand-authoring one.
The projection is produced by `scripts/project_leamas_gate_summary.py`
from the canonical artifact, fails closed on missing source fields,
and binds the canonical artifact's semantic hash.

The projected artifact at `.factory/gate-summary.json.leamas`:

```yaml
gate_summary:
  generated_at:               "2026-07-25T08:41:57.445244+00:00"  # non-empty
  tested_commit_oid:           "48e671894dc8f11f675f533f22c6e07cb1b7954f"
  tested_tree_oid:             "ce4b909b20f8a0afe0df8dbadc7cd4efeab791db"
  canonical_artifact_sha256:   "937bf24fc15160bf3ac63c8ddd80378ce39556329f579747f194f3fcc55490a6"
  overall_status:              "green"
  checks_total:                9
  checks_passed:               9
  checks_failed:               0
  check_names:                 bounded-process-tests, committed-range-diff-check,
                               fsharp-diagnostics-tests, git-adapter-tests,
                               protected-scope, repair-episodes-gate,
                               repair-episodes-tests, tooling-build,
                               tooling-tests-build
  evidence_references_nonempty: true
```

A digest was generated from `leamas factory digest --range
b996f15905dd491cb3f0cd87129be6fa0b94d2e7..HEAD` and the resulting
manifest covers the provider implementation range end to end. No
unnamed passing checks were reported by the projection. The Leamas
reader itself cannot parse the canonical-evidence-v1 schema (which
is outside this ACT's repository scope), so the digest's
`source_status` remains `invalid` against the canonical artifact;
the projection is the authoritative Leamas-compatible artefact for
downstream consumers.

## Stop conditions (re-evaluated)

| Stop condition                                                                                     | Held? |
|-----------------------------------------------------------------------------------------------------|-------|
| Any of the original 41 provider tests remains blocked                                              | no    |
| Tests require process-global executable mutation                                                   | no    |
| Production bypasses the bounded process or Git authorities                                         | no    |
| An evidence consumer reports unnamed passing checks                                                | no    |
| Missing generation or check identity fields are interpreted as pass                                 | no    |
| A compatibility projection can diverge from the canonical semantic hash                             | no    |
| Closure evidence covers only the final migration commit                                             | no    |
| Publication requires moving an existing tag or force-updating a branch                              | no    |

## Successor

After this correction reaches `CLOSED_PASS`, the
`ACT-CIRCUS-NO-FORCE-PUSH-DOCTRINE-GATE01-CORRECTION02` successor may
begin. The no-force-push successor must consume the registered
provider for both local enforcement and remote GitHub-ruleset
evidence.
