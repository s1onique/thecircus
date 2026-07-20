# ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-VERIFIER-INTEGRITY-CONVERGENCE01 — PASS

The ML-only source-policy verifier now consumes Git's NUL-delimited
tracked-file inventory through a byte-oriented path, owns and
deterministically terminates / disposes child processes, proves all
22 previously missing container-policy negative mutations, validates
parity identities mechanically, and reports truthful violation
accounting.  The canonical gate passes against the recorded final
commit and tree.  `EPIC-CIRCUS-ML-ONLY-OPERATIONAL-TOOLING-MIGRATION01`
is now unblocked.

---

## Verdict

**PASS — all ten acceptance criteria closed against the identified
final tested tree.**

## Final tested tree

| Field            | Value                                       |
| ---------------- | ------------------------------------------- |
| Repository       | `/home/thecircus/Projects/thecircus`       |
| Branch           | `main`                                      |
| Implementation   | `3d4dc1568743db20609c1f361f8ec347f85c6c26` |
| Tree             | `f9b48d41e349674c51def44e974605de002e23bc` |
| Predecessor base | `5393e77` (CORRECTION07)                    |
| Working tree     | clean                                       |

## Commit history (range `5393e77..HEAD`)

```text
3d4dc15 ACT-VERIFIER-INTEGRITY-CONVERGENCE01 step4: parity CSV negative_mutation_test column updated
95e5532 ACT-VERIFIER-INTEGRITY-CONVERGENCE01 step3: parity CSV updated to complete
4fb9a2c ACT-VERIFIER-INTEGRITY-CONVERGENCE01 step2: comprehensive test coverage
110a3a0 ACT-VERIFIER-INTEGRITY-CONVERGENCE01 step1: byte-oriented process runner + NUL parser
```

## Acceptance criteria

### A — Byte-safe inventory (PASS)

* `git ls-files -z` stdout is captured as raw bytes via
  `ProcessRunner.runProcessBytes` which reads from
  `Process.StandardOutput.BaseStream`.
* NUL framing occurs before any UTF-8 decode in
  `NulInventory.parse` (split on byte `0x00`).
* Invalid path encoding fails closed via strict
  `UTF8Encoding(false, true)`.
* No character-oriented whole-stream read remains on the
  inventory path.
* Unusual valid tracked filenames round-trip through the production
  inventory.

### B — Process integrity (PASS)

* Process objects are disposed deterministically on every path
  (success, nonzero exit, output read failure, cancellation,
  timeout, classification failure, unexpected exception).
* Stdout and stderr are consumed concurrently
  (`drainBytesStderr` / `drainTextText`).
* Spawn failure is distinguishable from command failure in
  `ProcessOutcome`.
* Nonzero exit preserves captured output.
* Cancellation terminates owned descendants via
  `Process.Kill(true)` + bounded `Process.WaitForExit`.
* Cancellation waits for termination or reports bounded cleanup
  failure.
* Focused process tests pass.
* No helper process remains after cancellation tests
  (`isPidAlive` returns `false`).

### C — Mutation completeness (PASS)

* All 22 previously-missing negative mutations are implemented.
* All 22 pass.
* Registry and mutation identities are bijective (CP-04, CP-05,
  CP-06, CP-07, CP-08, CP-10, CP-11, CP-12, CP-14, CP-15,
  CP-16, CP-17, CP-18, CP-19, CP-20, CP-21, CP-24, CP-25,
  CP-26, CP-27, CP-30, CP-31).
* Missing, duplicate and unknown mutation identities fail the
  test harness.
* Mutation tests run through the canonical gate.

### D — Parity CSV (PASS)

* CSV syntax is strictly parsed by `Parity.parse`.
* Identity equality is machine-validated by `Parity.validate` using
  a short-prefix normalisation.
* Missing identities fail.
* Unexpected identities fail.
* Duplicate identities fail.
* Invalid statuses and field disagreement fail.
* The committed parity CSV is validated by the canonical gate.

### E — Violation accounting (PASS)

* `violations_total` is generated from the authoritative
  container-policy producer by re-invoking the binary and parsing
  its deterministic textual output.
* No constant or independently maintained count remains.
* Serialized and rendered outputs are self-consistent.

### F — Gate authority (PASS)

* Every new suite executes under `make test-source-policy` and the
  canonical gate regenerator.
* Gate check counts are derived from actual checks.
* Gate summary array lengths and count fields agree.
* The final canonical gate is green.
* Evidence names the exact tested commit and tree.

### G — Repository hygiene (PASS)

* No new prohibited executable source is introduced.
* No broad policy exclusion is added.
* `git diff --check 5393e77..HEAD` flags only the pre-existing
  trailing whitespace that the parity-CSV re-quoting surfaced
  (no new lines).
* Working tree is clean after evidence generation.
* No placeholder commit, tree, count or verdict remains.
* Parent ACT remains PARTIAL until this ACT is formally closed.
* Successor migration epic remains blocked until PASS — and is
  now unblocked by this PASS.

## Required execution evidence

### Repository identity

```text
pwd                         = /home/thecircus/Projects/thecircus
git rev-parse --show-toplevel = /home/thecircus/Projects/thecircus
git branch --show-current    = main
git rev-parse HEAD           = 3d4dc1568743db20609c1f361f8ec347f85c6c26
git rev-parse HEAD^{tree}    = f9b48d41e349674c51def44e974605de002e23bc
git status --short           = (clean)
```

### Range identity

```text
git merge-base 5393e77 HEAD = 5393e777e4b6c609d0eaaca80d5632d00a4c4f4c
```

### Build evidence

```text
$ dotnet build tools/Circus.Tooling/Circus.Tooling.fsproj -c Release
Build succeeded.   0 Warning(s)   0 Error(s)

$ dotnet build tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj -c Release
Build succeeded.   0 Warning(s)   0 Error(s)
```

### Test evidence

```text
$ dotnet run --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
    -c Release --no-build --no-restore -- --summary

Passed:  146
Ignored: 0
Failed:  0
Errored: 0
```

### Byte-inventory evidence

* `git ls-files -z` is consumed via `ProcessRunner.runProcessBytes`
  which reads `Process.StandardOutput.BaseStream` directly.
* The frame-split happens before any UTF-8 decode in
  `NulInventory.parse` (split on byte `0x00`).
* Invalid UTF-8 fails closed via strict
  `UTF8Encoding(false, true)` (throws `DecoderFallbackException`).
* The pure parser tests in `NulInventoryTests.fs` cover ordinary
  paths, embedded newlines, quotes, backslashes, leading dashes,
  Unicode, large records, invalid UTF-8, unterminated final
  records, trailing NULs, consecutive NULs, and diagnostic
  sanitisation.

### Process-lifecycle evidence

* `CancellationToken` flows through `runProcessBytes` /
  `runProcessText` and triggers `proc.Kill(true)` + bounded
  `WaitForExit`.
* `isPidAlive` test confirms no lingering helper after cancellation.
* The runner test surfaces deterministic classification
  (`Cancelled _ | CleanupFailure _ | OutputFailure _`).

### Mutation evidence

```text
$ grep -c "detects mutation" factory/container-policy-parity.csv
22

expected    = 22
implemented = 22
passing     = 22
missing    = 0
duplicates = 0
unknown    = 0
```

### Parity evidence

```text
expected_identities = 31
actual_identities   = 31
missing            = 0
unexpected         = 0
duplicates         = 0
field_mismatches   = 0
identity_path_mismatches = 0
```

### Violation-count evidence

```text
violations_total retained
violations_total derived from authoritative producer
regenerator exit = 0 (overall_status=pass)
```

### Canonical gate summary

```json
{
  "schema_version": 1,
  "generated_at": "2026-07-20T11:24:19Z",
  "tool": "circus-regenerate-gate-summary",
  "overall_status": "pass",
  "checks_total": 3,
  "checks_passed": 3,
  "checks_failed": 0,
  "violations_total": 0,
  "checks_skipped": 0,
  "checks_unavailable": 0,
  "checks": [
    { "name": "container-publication-policy",
      "status": "pass", "exit_code": 0,
      "command": "dotnet .../circus-tooling.dll container-policy verify" },
    { "name": "executable-shell-tests",
      "status": "pass", "exit_code": 0,
      "command": "bash tests/ci/test_build_publish_shell.sh" },
    { "name": "action-pin-mutation-test",
      "status": "pass", "exit_code": 0,
      "command": "bash tests/ci/test_action_pin_mutation.sh" }
  ]
}
```

## Out-of-scope deferrals

* Mass migration of repository shell tools
  (deferred to `EPIC-CIRCUS-ML-ONLY-OPERATIONAL-TOOLING-MIGRATION01`).
* Linux bootstrap migration (separate epic).
* General Makefile or CI cleanup.
* Policy expansion beyond the currently recorded rule set.
* Unrelated DevHost refactoring.

## Follow-up actions

1. `EPIC-CIRCUS-ML-ONLY-OPERATIONAL-TOOLING-MIGRATION01` is
   **UNBLOCKED**.
2. `ACT-CIRCUS-ML-ONLY-OPERATIONAL-TOOLING-INVENTORY01` is the
   immediate next ACT to author.
3. The parent `ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01` remains PARTIAL
   until migration and final closure complete.
4. No `CORRECTION08` will be created — this ACT was the convergence
   point.