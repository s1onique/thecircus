# ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER-FOUNDATION01-CORRECTION01

## Status

**CHECKPOINT** — implementation compiled, partial-checkpoint executor in place.

## Classification

**P0 — repository-wide canonical evidence authority**

## Parent

`ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER-FOUNDATION01`

## Entry state

```yaml
baseline_commit_oid: 5f1f7f99d57aaa133e76679c8bb6aa90620ebc1e
partial_checkpoint_commit_oid: b996f15905dd491cb3f0cd87129be6fa0b94d2e7
partial_checkpoint_tree_oid:   a23e2470cc41ba4a61afe5805c2887b728cbfe57
origin_main_oid:               b996f15905dd491cb3f0cd87129be6fa0b94d2e7
working_tree_required:         clean
```

## Objective

Complete a minimal but real canonical evidence provider by composing
existing repository authorities rather than creating another subprocess
implementation.

The provider must:

1. execute checks through `BoundedProcess.run`;
2. resolve Git identities through the bounded Git adapter;
3. generate and validate deterministic evidence;
4. publish evidence atomically;
5. expose `canonical-evidence regenerate` and `canonical-evidence verify`;
6. register the provider as repository authority;
7. integrate provider verification into the canonical gate;
8. migrate the bounded Git adapter's historical evidence classification.

## Mandatory architectural boundary

### Reuse existing execution authority

Every external command must flow through:

```fsharp
Circus.Tooling.FSharpDiagnostics.RepairEpisodes.BoundedProcess.run
```

Do not add any provider-owned use of:

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

The provider owns evidence orchestration, not process lifecycle
management.

### Compile order

Ensure the F# project order is:

```text
BoundedProcess.fs
RepairEpisodes/Git.fs
CanonicalEvidence/Domain.fs
CanonicalEvidence/Serialization.fs
CanonicalEvidence/Validation.fs
CanonicalEvidence/Provider.fs
CanonicalEvidence/Cli.fs
top-level Cli.fs
Program.fs
```

Do not solve forward-reference failures using duplicated types or
reflection.

## Owned scope

```text
tools/Circus.Tooling/CanonicalEvidence/Domain.fs
tools/Circus.Tooling/CanonicalEvidence/Serialization.fs
tools/Circus.Tooling/CanonicalEvidence/Validation.fs
tools/Circus.Tooling/CanonicalEvidence/Provider.fs
tools/Circus.Tooling/CanonicalEvidence/Cli.fs
tests/Circus.Tooling.Tests/CanonicalEvidence/
tools/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj
tools/Circus.Tooling/Circus.Tooling.fsproj
tools/Circus.Tooling/Cli.fs
.factory/evidence-provider-registry.json
.factory/gate-summary.json
Makefile
docs/acts/ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER-FOUNDATION01-CORRECTION01.md
docs/close-reports/ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER-FOUNDATION01-CORRECTION01.md
docs/close-reports/ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER-FOUNDATION01.md
```

## Protected scope

Do not modify production behavior in:

```text
tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/BoundedProcess.fs
tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/Git.fs
tools/Circus.Tooling/NoForcePush/
src/Circus.Persistence.Postgres/
tests/Circus.Persistence.Postgres.Tests/
factory/evidence/fsharp-diagnostics/corpus/raw/
```

## Implementation sequence

### Slice 1 — Pure domain

The pure types and pure functions are implemented in `Domain.fs`. No
process execution, no filesystem IO, no subprocess management.
`ForbiddenIdentityFields` fails pre-publication validation of any
post-publication identity field.

### Slice 2 — Serialization and validation

Deterministic `System.Text.Json` serialization producing a wire form
with snake_case property names, fixed ordering, newline-terminated
UTF-8 (no BOM), and no volatile timestamp fields. Strict
deserialization rejects unknown properties.

### Slice 3 — Check execution adapter

Every external command flows through `BoundedProcess.run`. The bounded
Git adapter resolves commit, tree, object format, and the working tree
cleanliness status.

### Slice 4 — Atomic writer

Generation writes to a temporary sibling, flushes, re-reads, validates
the schema and semantic hash, and atomically replaces the target. A
failed regeneration preserves the previous artifact byte-identically.

### Slice 5 — CLI and registry

The `canonical-evidence regenerate` and `canonical-evidence verify`
verbs are wired into the top-level CLI dispatcher. The provider is
registered in `.factory/evidence-provider-registry.json` with the
required fields.

### Slice 6 — Make and gate integration

The `make canonical-evidence` and `make verify-canonical-evidence`
targets are registered. The canonical repository gate consumes the
provider's verification.

## Initial canonical check set

The initial nine checks are registered in this correction:

```text
tooling-build
tooling-tests-build
bounded-process-tests
git-adapter-tests
repair-episodes-tests
fsharp-diagnostics-tests
repair-episodes-gate
committed-range-diff-check
protected-scope
```

`committed-range-diff-check` and `protected-scope` are run via the
bounded Git adapter. No remote GitHub checks are added yet.

## Required tests

44 tests are required across the pure model, execution, writer/verify,
and CLI surfaces. Each test is enumerated in the close report.

## Mandatory verification

```bash
dotnet build tools/Circus.Tooling/Circus.Tooling.fsproj -c Release --no-restore
dotnet build tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj -c Release --no-restore
dotnet run --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
  -c Release --no-build --no-restore -- --summary --filter-test-list "CanonicalEvidence"
dotnet run --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
  -c Release --no-build --no-restore -- --summary --filter-test-list \
  "FSharpDiagnostics.RepairEpisodes.BoundedProcess"
dotnet run --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
  -c Release --no-build --no-restore -- --summary --filter-test-list \
  "FSharpDiagnostics.RepairEpisodes.GitAdapter"
dotnet run --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
  -c Release --no-build --no-restore -- --summary --filter-test-list "FSharpDiagnostics"
make canonical-evidence
make verify-canonical-evidence
git diff --check
git diff --check b996f15905dd491cb3f0cd87129be6fa0b94d2e7..HEAD
git status --short
```

## Migration requirement

Once provider generation and verification pass:

1. classify the previous `.factory/gate-summary.json` as historical ad
   hoc evidence;
2. regenerate it through the registered provider;
3. record the provider version and tested commit/tree;
4. update the bounded Git adapter historical handoff:
   ```yaml
   correction02:
     implementation_status: pass
     act_local_evidence_status: pass
     canonical_evidence_status: migrated_by_provider_foundation_correction01
   ```
   Do not alter existing tags.

## Stop conditions

Stop with `PARTIAL_CHECKPOINT` when:

* any provider source directly uses `System.Diagnostics.Process`;
* process execution is copied from `SourcePolicy.ProcessRunner`;
* F# event handlers are introduced for stdout or stderr;
* unavailable required checks can produce overall pass;
* identity values are accepted without Git resolution;
* a failed regeneration damages the previous artifact;
* the provider is not registered as canonical;
* the canonical gate does not consume provider verification;
* existing historical tags would need to move;
* publication would require a force update.

## Successor

Only after this correction closes may:

```text
ACT-CIRCUS-NO-FORCE-PUSH-DOCTRINE-GATE01-CORRECTION02
```

begin.
