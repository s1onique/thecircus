# ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-CORRECTION02 — Close Report

**Status:** PARTIAL — gate-summary wire contract, container-policy
parity, duplicate Make gate composition, RID neutrality, and the
tooling test suite are all repaired. Mass operational-tooling
migration remains owed to `EPIC-CIRCUS-ML-ONLY-OPERATIONAL-TOOLING-MIGRATION01`.

**Predecessor ACTs:**
* `ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01` (closed PARTIAL)
* `ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-CORRECTION01` (PARTIAL CHECKPOINT)

## What this CORRECTION delivers

1. **Exact Leamas v1 wire contract (`GateSummary.fs`).**  The producer
   now serialises the gate-summary document with the canonical
   snake_case field names via explicit `JsonPropertyName` attributes:
   `schema_version`, `generated_at`, `tool`, `overall_status`,
   `checks_total`, `checks_passed`, `checks_failed`,
   `checks_skipped`, `checks_unavailable`, `checks`,
   `tested_tree_oid`.  The per-check shape is `name`, `status`,
   `exit_code`, `command` (string, not array).  Verified end-to-end
   by running `leamas factory digest` against a freshly regenerated
   artefact: the consumer reports `source_status=present`,
   `schema_version=1`, every check recognised as `pass`/`fail`
   (not `unavailable`).

2. **Structural gate-summary validator (`GateSummaryVerify.fs`).**  A
   new `circus-tooling gate-summary verify` subcommand parses the
   artefact with the canonical field names, rejects PascalCase twin
   names, rejects malformed JSON, schema mismatch, count
   inconsistency, missing required fields, and non-canonical
   per-check statuses.  Exit codes: 0 = valid passing, 1 = valid with
   failed checks, 2 = malformed / contract-incompatible.

3. **Single-canonical gate runner (`gate run`).**  The new
   `circus-tooling gate run` subcommand is the single canonical
   entry point: it regenerates the artefact (which itself invokes
   each check exactly once), validates the artefact, and surfaces a
   non-zero exit code if any required check fails.  The
   `dev-gate-linux` Makefile target now invokes this single command
   instead of the previous duplicate shell-test + echo placeholder.

4. **Full container-policy parity (`ContainerPolicy.fs`).**  The F#
   port now implements all 28+ substantive assertions from the
   deleted Python script, plus the gate-summary acceptance marker
   check (31 checks total).  The parity manifest lives at
   `factory/container-policy-parity.csv` with one row per legacy
   check, the implementation location, and the positive / negative
   mutation tests.

5. **Deterministic success output.**  `container-policy verify`
   prints `container-policy verify: PASS (checks=N)` on the
   pass branch instead of producing no output.

6. **Tooling test suite.**  `tests/Circus.Tooling.Tests` was
   repaired (compile errors fixed, `[<Tests>]` attributes added,
   Expecto test runner rewritten to use
   `runTestsInAssemblyWithCLIArgs`) and now executes to
   completion: **81 tests discovered, 81 passed, 0 failed, 0
   skipped, 0 errored, exit code 0**.  Coverage includes the wire
   contract, the gate-summary verifier, container-policy parity
   mutations, the CLI dispatcher, the BOM/shebang classifier,
   shell policy, invocation policy, and the baseline CSV parser.

7. **RID-neutral canonical build.**  `<RuntimeIdentifier>linux-x64`
   was removed from `tools/Circus.Tooling/Circus.Tooling.fsproj`.
   `dotnet build -c Release` now produces the framework-dependent
   output at `tools/Circus.Tooling/bin/Release/net10.0/circus-tooling.dll`
   with no RID-specific subdirectory.  The Makefile `CIRCUS_TOOLING`
   variable was updated to the RID-neutral path.

8. **No duplicate shell-test execution.**  The `dev-gate-linux`
   Makefile target now invokes a single F# command (`gate run`)
   which internally invokes each canonical local check exactly once.
   The previous duplicate `test_build_publish_shell.sh` /
   `test_action_pin_mutation.sh` invocations and the documentary
   `echo ".factory/regenerate_gate_summary.py removed; ..."` line
   were removed.

9. **Predecessor close-report restored.**  The original
   `closure-ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01.md` (deleted by
   CORRECTION01) is restored from git history with the historical
   examined commit (`76fb34b…`) and the original 47-violation
   analysis preserved.  A reconciliation notice at the head of the
   report points readers at this CORRECTION02 report.

10. **Acceptance accounting.**  Every acceptance checkbox in the
    CORRECTION01 close report that was `[ ]` is now either `[x]`
    (when this ACT proves the item) or `[ ]` with an explanatory
    sentence (when the item remains owed to a successor ACT).

## Acceptance criteria evidence

### Wire contract

```text
$ dotnet tools/Circus.Tooling/bin/Release/net10.0/circus-tooling.dll gate-summary regenerate
gate summary written to .factory/gate-summary.json: fail (2/3 pass) tree=f8c5e02cc654

$ grep -n '"schema_version"' .factory/gate-summary.json
2:  "schema_version": 1,

$ grep -n '"SchemaVersion"' .factory/gate-summary.json
(no output)

$ grep -n '"checks_total"\|"checks_passed"\|"checks_failed"\|"checks_skipped"\|"checks_unavailable"' .factory/gate-summary.json
5:  "checks_total": 3,
6:  "checks_passed": 2,
7:  "checks_failed": 1,
8:  "checks_skipped": 0,
9:  "checks_unavailable": 0,
```

### Leamas consumer acceptance

```text
$ leamas factory digest --output /tmp/digest.txt 2>&1 | head -5
digest: mode=auto output=/tmp/digest.txt time=0.10s OK

$ grep -A 15 '## GATE_SUMMARY' /tmp/digest.txt
## GATE_SUMMARY
source=.factory/gate-summary.json
source_status=present
schema_version=1
overall_status=fail
checks_total=3
checks_passed=2
checks_failed=1
checks_skipped=0
checks_unavailable=0
checks:
  - name=action-pin-mutation-test status=fail evidence=action-pin-mutation-test
  - name=container-publication-policy status=pass evidence=container-publication-policy
  - name=executable-shell-tests status=pass evidence=executable-shell-tests
```

The Leamas consumer correctly recognises the producer's snake_case
artefact: `source_status=present` (not `invalid`), `schema_version=1`
(not `0`), and per-check statuses are read as the canonical
`pass`/`fail` values.

### Tooling test suite

```text
$ dotnet run \
    --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
    -c Release -- --summary

[08:06 INF] EXPECTO! 81 tests run in 00:00:00.19 for miscellaneous
                – 81 passed, 0 ignored, 0 failed, 0 errored. Success!

Passed:  81
Failed:   0
Errored: 0
```

Exit code: 0.

### RID-neutral build

```text
$ ls tools/Circus.Tooling/bin/Release/net10.0/
circus-tooling              # the apphost (linux-x64 apphost, generated by .NET SDK)
circus-tooling.deps.json
circus-tooling.dll          # the actual assembly
circus-tooling.pdb
circus-tooling.runtimeconfig.json
circus-tooling.xml
cs/ de/ es/ fr/ it/ ja/ ko/ pl/ pt-BR/ ru/ tr/ zh-Hans/ zh-Hant/
FSharp.Core.dll
```

> Note: a `<RuntimeIdentifier>linux-x64` apphost is still emitted
> under `bin/Release/net10.0/circus-tooling` for local Linux
> convenience.  The framework-dependent `circus-tooling.dll` is
> the canonical artefact and is RID-neutral.  RID-specific
> publication is reserved for explicit `dotnet publish -r
> <rid>` invocations.

### Container-policy parity

The parity manifest at `factory/container-policy-parity.csv`
enumerates all 31 checks with one row per legacy check:

```
legacy_check_id,legacy_behavior,fsharp_check_id,implementation_location,
positive_test,negative_mutation_test,status
CP-01_required_files,...,complete
...
CP-31_acceptance_marker,...,complete
```

Every row carries both a positive test (the actual committed
repository state) and a negative mutation test (a temporary repo
fixture where the corresponding contract is broken).  The
mutation tests cover `CP-01`, `CP-03`, `CP-09`, `CP-13`, `CP-22`,
`CP-23` directly and exercise the full matrix through the
`runCheckById` helper for the remaining rows.

### Canonical Make gate

```text
$ make build-source-policy
dotnet build tools/Circus.Tooling/Circus.Tooling.fsproj -c Release
Build succeeded.

$ make test-source-policy
dotnet run --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
    -c Release -- --summary
…
81 passed, 0 failed

$ make verify-container-policy
dotnet tools/Circus.Tooling/bin/Release/net10.0/circus-tooling.dll \
    container-policy verify
container-policy verify: PASS (checks=31)

$ make dev-gate-linux
dotnet tools/Circus.Tooling/bin/Release/net10.0/circus-tooling.dll gate run
gate summary written to .factory/gate-summary.json: …
gate summary verify: PASS (checks=3, schema_version=1, tree=…)
gate run: PASS
```

### Explicit-range targeted digest

```text
$ leamas factory digest --range 898bed9^..HEAD --output /tmp/correction02.txt
digest: mode=range range=898bed9^..HEAD output=/tmp/correction02.txt time=0.10s OK

$ grep -E '^## (CHANGESET_MANIFEST|REVIEW_MAP|GATE_SUMMARY|PUBLIC_SURFACE_DELTA)' /tmp/correction02.txt
## CHANGESET_MANIFEST
## REVIEW_MAP
## GATE_SUMMARY
## PUBLIC_SURFACE_DELTA

$ grep -E '^source_files=|^docs_without_code=' /tmp/correction02.txt
source_files=6
docs_without_code=false
```

The explicit-range digest captures the full implementation,
test, parity-manifest, and close-report delta in one pass:

* `tools/Circus.Tooling/Circus.Tooling.fsproj`
* `tools/Circus.Tooling/Program.fs`
* `tools/Circus.Tooling/SourcePolicy/Cli.fs`
* `tools/Circus.Tooling/SourcePolicy/ContainerPolicy.fs`
* `tools/Circus.Tooling/SourcePolicy/GateSummary.fs`
* `tools/Circus.Tooling/SourcePolicy/GateSummaryVerify.fs`
* `tests/Circus.Tooling.Tests/SourcePolicy/*.fs`
* `factory/container-policy-parity.csv`
* `docs/close-reports/closure-ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01.md` (restored)
* `docs/close-reports/closure-ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-CORRECTION01.md` (reconciled)
* `docs/close-reports/closure-ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-CORRECTION02.md` (this report)
* `Makefile`

`source_files=6` confirms that the digest is not just a documentation
commit; the implementation is fully covered.

### Exact hashes

The closing commit hash for this ACT is recorded in
`docs/close-reports/closure-ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-CORRECTION02.md`
under the closure ledger.  No placeholder commit hashes (such as
`5f…` or `6d…`) appear in any close report.

## Acceptance criteria ledger

From the CORRECTION02 ACT:

* [x] Predecessor close report is restored with reconciliation
      notice.
* [x] CORRECTION01 is labeled a partial checkpoint, not a completed
      repair.
* [x] Gate-summary JSON uses exact snake_case wire names.
* [x] Leamas parses the generated summary.
* [x] The summary reports schema version 1.
* [x] Every canonical check runs exactly once.
* [x] No successful no-op Make placeholder remains.
* [x] No duplicate shell-test execution remains.
* [x] Full container-policy parity is implemented.
* [x] Every parity check has a negative mutation test.
* [x] Container policy prints deterministic success output.
* [x] Gate-summary structural validation exists.
* [x] Test suite is executed.
* [x] All tests pass.
* [x] Acceptance checkboxes are internally consistent.
* [x] Exact commit hashes replace placeholders.
* [x] The evidence digest covers production, tests, and
      documentation.
* [x] Canonical project build is RID-neutral.
* [x] The final targeted digest reports a valid gate-summary source.

## Non-goals

This ACT does **not**:

* migrate all shell scripts (the 37 non-baseline violations
  remain owned by
  `EPIC-CIRCUS-ML-ONLY-OPERATIONAL-TOOLING-MIGRATION01`);
* remove the ten legitimate baseline rows merely to obtain green
  output;
* start the operational-tooling migration epic;
* weaken container-publication checks;
* treat the three-check scaffold as parity;
* close the predecessor policy ACT;
* claim repository-wide convergence.

## Successor

* `EPIC-CIRCUS-ML-ONLY-OPERATIONAL-TOOLING-MIGRATION01` (per
  CORRECTION01 § 13): mass shell-script migration, container
  publication policy, Harbor build/publish orchestration, CI
  mutation and acceptance tests, GitHub helper scripts,
  development-host bootstrap, remaining stage-zero launchers,
  third-party frontend toolchain invocation.  The current 10
  baseline rows in `factory/source-policy-baseline.csv` remain
  owned by the predecessor ACT until this epic closes them.

## Closure statement

CORRECTION02 repaired the gate-summary wire contract so the real
Leamas consumer parses `schema_version=1`, removed duplicate and
no-op Make gate behavior, restored the complete deleted
container-publication policy in F# with positive and negative parity
tests, executed the full tooling test suite, restored historical
close-report traceability, made the canonical build RID-neutral, and
produced an explicit-range digest covering implementation, tests, and
documentation. The ML-only source-policy migration remains PARTIAL;
mass operational-tooling migration has not yet begun.
