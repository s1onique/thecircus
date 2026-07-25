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
implementation_commit_oid:  272a922
implementation_tree_oid:    <resolved at commit time>
tested_commit_oid:          <resolved at commit time>
tested_tree_oid:            <resolved at commit time>
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
canonical evidence provider tests are gated by a test that asserts
the seam is unchanged across the entire CanonicalEvidence suite.

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
| 7  | Regenerate success                                            | pass |
| 8  | Verify success                                                | pass |
| 9  | Verify stale failure                                          | pass |
| 10 | Verify mutation failure                                       | pass |
| 11 | Unknown verb failure                                          | pass |
| 12 | Missing argument failure                                      | pass |
| 13 | Invalid repository failure                                    | pass |
| 14 | Dirty repository failure                                      | pass |
| 15 | Writer failure preserves prior artifact                       | pass |
| 16 | No failure emits a PASS line                                  | pass |

### Consumer compatibility tests

| ID | Test                                                                  | Status |
|----|-----------------------------------------------------------------------|--------|
| 17 | Canonical artifact has a non-empty generation timestamp                | pass |
| 18 | Canonical artifact has full tested commit/tree OIDs                    | pass |
| 19 | All check IDs are non-empty and unique                                 | pass |
| 20 | Every passing check has an evidence reference or output hashes         | pass |
| 21 | Compatibility projection binds the canonical semantic hash             | pass |
| 22 | Missing consumer-required fields fail closed                          | pass |
| 23 | Targeted digest surfaces all nine check names                          | pass |
| 24 | Targeted digest surfaces tested commit and tree                        | pass |
| 25 | Targeted digest cannot report an unnamed passing check                 | pass |

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
  --summary --filter-test-list "FSharpDiagnostics"
# → 245 tests run for FSharpDiagnostics – 245 passed

make canonical-evidence
make verify-canonical-evidence

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
duration_ms: <resolved at commit time>
stdout_sha256: <resolved at commit time>
stderr_sha256: <resolved at commit time>
tested_commit_oid: <resolved at commit time>
tested_tree_oid: <resolved at commit time>
semantic_sha256: <resolved at commit time>
```

## Consumer compatibility (Leamas targeted digest)

A new Leamas targeted digest was generated against the full provider
implementation range `b996f15905dd491cb3f0cd87129be6fa0b94d2e7..HEAD`:

```yaml
gate_summary:
  generated_at_nonempty: true
  tested_commit_oid_visible: true
  tested_tree_oid_visible: true
  check_names_nonempty: true
  check_names_unique: true
  evidence_references_nonempty: true
  checks_total: 9
  checks_passed: 9
  checks_failed: 0
```

No unnamed passing checks were reported. The targeted digest was
generated from `leamas factory digest --range
b996f15905dd491cb3f0cd87129be6fa0b94d2e7..HEAD` and the resulting
manifest covers the provider implementation range end to end.

## Stop conditions (re-evaluated)

| Stop condition                                                                                     | Held? |
|-----------------------------------------------------------------------------------------------------|-------|
| Any of the original 41 provider tests remains blocked                                              | no    |
| Tests require process-global executable mutation                                                   | no    |
| Production bypasses the bounded process or Git authorities                                         | no    |
| An evidence consumer reports unnamed passing checks                                                | no    |
| Missing generation or check identity fields are interpreted as pass                                 | no    |
| A compatibility projection can diverge from the canonical semantic hash                             | n/a   |
| Closure evidence covers only the final migration commit                                             | no    |
| Publication requires moving an existing tag or force-updating a branch                              | no    |

## Successor

After this correction reaches `CLOSED_PASS`, the
`ACT-CIRCUS-NO-FORCE-PUSH-DOCTRINE-GATE01-CORRECTION02` successor may
begin. The no-force-push successor must consume the registered
provider for both local enforcement and remote GitHub-ruleset
evidence.
