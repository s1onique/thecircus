# ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-CORRECTION03 — Close Report

**Status:** CLOSED — PARTIAL CHECKPOINT; canonical container gate green,
source-policy convergence still PARTIAL.  Mass operational-tooling
migration remains owed to
`EPIC-CIRCUS-ML-ONLY-OPERATIONAL-TOOLING-MIGRATION01`.

**Predecessor ACTs:**
* `ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01` (closed PARTIAL)
* `ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-CORRECTION01` (PARTIAL CHECKPOINT)
* `ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-CORRECTION02` (closed PARTIAL but
  claimed PASS while the gate was actually red — see below)

## What this CORRECTION delivers

P0 blockers (per the review):

1. **Test exit propagation.** `tests/Circus.Tooling.Tests/Program.fs`
   now returns the actual Expecto runner exit code (`0` on full pass,
   `1` on any failure). `make test-source-policy` is fail-closed.
2. **Repair the failing action-pin mutation test.**
   `tests/ci/test_action_pin_mutation.sh` now invokes the F# tooling
   directly (not the deleted Python script), runs from the sandbox
   git working directory, and reproduces the SHA-to-tag mutation
   against `actions/checkout`. Exits 0 on the committed tree.
3. **`gate run` validates every artefact.** The CLI runner in
   `Cli.fs::runGate` now invokes both the regeneration and the
   verification unconditionally, combines the three verdicts
   (regeneration operational result, structural validation,
   per-check verdict), and reports the most severe code per Unix
   severity ordering (`2 > 1 > 0`).

Additional correctness repairs:

4. **CP-29 fails closed when git is unavailable.** `runVerify` returns
   exit 2 (`OperationalFailures` is non-empty) when `git ls-files`
   cannot enumerate the tracked tree, instead of silently passing.
5. **CP-29 mutation test now initialises git first.** The
   `CP-29 detects a tracked secret-like file (negative mutation)`
   test calls `tryInitGit root` before invoking `gitTrackedFiles
   root`, so the assertion is reproducible against any host with a
   working `git` binary.
6. **`ChecksFailed` vs. `ViolationsTotal`.** The producer now
   separates `ChecksFailed` (count of failed *checks*) from
   `ViolationsTotal` (raw violation count); the
   `ContainerPolicyReport` type distinguishes the two. The validator
   requires `passed + failed + skipped + unavailable == total`.
7. **Unavailable checks no longer double-counted.** `failedChecks`
   counts only checks whose status is `"fail"`; checks whose
   status is `"unavailable"` are recorded separately. The validator
   requires the four counts to sum to `checks_total`.
8. **Process-launch failure → `"unavailable"`.** `runCheck`
   classifies `ExitCode = -1` (returned by `runProcess` when the
   child cannot be launched) as `"unavailable"` rather than `"fail"`.
9. **NUL-safe inventory preserves legitimate whitespace.**
   `splitNulInventory` no longer `Trim()`s the output of
   `git ls-files -z`; filenames with leading/trailing whitespace are
   preserved verbatim.
10. **Raw stream reading for byte-preserving NUL output.**
    `runProcess` (and its async variant `runProcessAsync`) reads
    child stdout/stderr via `StreamReader.ReadToEndAsync()` on the
    raw `BaseStream`, **not** through the line-oriented
    `OutputDataReceived` / `BeginOutputReadLine` API. Filenames
    containing embedded newlines survive byte-for-byte.
11. **RID-neutral Makefile.** `CIRCUS_TOOLING_DLL :=
    tools/Circus.Tooling/bin/Release/net10.0/circus-tooling.dll` and
    `CIRCUS_TOOLING := $(DOTNET) $(CIRCUS_TOOLING_DLL)` so every
    invocation runs the canonical framework-dependent artefact
    through `dotnet`. `verify-container-policy` and
    `dev-gate-linux` both consume the canonical variable.
12. **Parity CSV bound to real tests, honest statuses.** Every row
    of `factory/container-policy-parity.csv` references the
    corresponding Expecto test in
    `ContainerPolicyTests.fs` (the canonical positive
    `verify on the actual committed tree passes`). Rows whose
    dedicated negative mutation test was delivered in this
    checkpoint are marked `complete` (CP-01, CP-02, CP-03, CP-09,
    CP-13, CP-22, CP-23, CP-28, CP-29). Rows whose only coverage
    is the canonical positive test against the committed tree are
    marked `partial — positive only` so the parity manifest is no
    longer misleadingly marked `complete` for every row.

## Acceptance criteria evidence

### Gate-run is green against the committed tree

Regenerated against `HEAD^{tree}` after the closure commit:

```text
$ dotnet tools/Circus.Tooling/bin/Release/net10.0/circus-tooling.dll gate run
gate summary written to .factory/gate-summary.json: pass (3/3 pass) tree=3bc422051f7e

gate-summary verify: PASS (checks=3, schema_version=1, tree=3bc422051f7e)
gate run: PASS
$ echo $?
0
```

### `.factory/gate-summary.json` (detached evidence bound to `HEAD^{tree}`)

```json
{
  "schema_version": 1,
  "generated_at": "2026-07-20T08:43:54Z",
  "tool": "circus-regenerate-gate-summary",
  "overall_status": "pass",
  "checks_total": 3,
  "checks_passed": 3,
  "checks_failed": 0,
  "checks_unavailable": 0,
  "violations_total": 0,
  "checks_skipped": 0,
  "checks": [
    {"name": "container-publication-policy", "status": "pass", "exit_code": 0,
     "command": "dotnet ... circus-tooling.dll container-policy verify"},
    {"name": "executable-shell-tests", "status": "pass", "exit_code": 0,
     "command": "bash tests/ci/test_build_publish_shell.sh"},
    {"name": "action-pin-mutation-test", "status": "pass", "exit_code": 0,
     "command": "bash tests/ci/test_action_pin_mutation.sh"}
  ],
  "tested_tree_oid": "3bc422051f7e56aa2a3dbd74998dea696ddf36d8"
}
```

Note: this artefact is **detached evidence**, not a tracked file in
the closure commit.  The committed code emits it on demand; the
canonical `.factory/gate-summary.json` shown above was regenerated
from `HEAD^{tree}` after the commit was created.  See
`docs/architecture/ml-only-source-policy.md` for the canonical
consumer pattern.

### Tooling test suite (closure commit)

```text
$ dotnet run --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj -c Release -- --summary
...
Passed:  81
Failed:  0
Errored: 0
$ echo $?
0
```

The closure commit ships the original 81-test Expecto suite.  Earlier
attempts to land 111 dedicated mutation tests introduced file-encoding
errors and were reverted; the dedicated negative-mutation coverage that
*is* present is enumerated explicitly in the parity CSV (CP-01,
CP-02, CP-03, CP-09, CP-13, CP-22, CP-23, CP-28, CP-29).  Adding the
remaining 22 dedicated negative mutation tests is a future ACT
(EPIC-CIRCUS-ML-ONLY-OPERATIONAL-TOOLING-MIGRATION01).

### Action-pin mutation test passes on the committed tree

```text
$ bash tests/ci/test_action_pin_mutation.sh
OK: pristine policy check passes
OK: mutated @v6 policy check failed as expected (rc=1)
OK: original policy check still passes after the sandbox mutation
action-pin mutation test passed
$ echo $?
0
```

### RID-neutral Makefile uses `dotnet` for canonical invocation

```text
$ grep -E '^(CIRCUS_TOOLING|CIRCUS_TOOLING_DLL)' Makefile
CIRCUS_TOOLING_DLL := tools/Circus.Tooling/bin/Release/net10.0/circus-tooling.dll
CIRCUS_TOOLING     := $(DOTNET) $(CIRCUS_TOOLING_DLL)
```

### `git diff --check` is clean

```text
$ git diff --check HEAD~1 HEAD
$ echo $?
0
```

## What this CORRECTION does **not** deliver

* A complete 31-dedicated-mutation-test matrix.  The parity CSV marks
  only CP-01, CP-02, CP-03, CP-09, CP-13, CP-22, CP-23, CP-28, and
  CP-29 as `complete`; the remaining rows are honest `partial —
  positive only` markers.
* Machine-validated parity references.  The parity CSV cites real
  test names but no Expecto test loads the CSV and asserts every
  reference exists.  A future ACT should add such a validator.
* Truthful `violations_total`.  The producer still sets
  `ViolationsTotal = 0` because the child checks do not publish
  structured violation counts.  This is documented in the field
  contract.  A future ACT should either surface real counts (e.g. by
  having each child emit a sibling JSON artefact) or remove the
  field until it can be computed truthfully.
* Mass shell-script migration; the 10 baseline rows in
  `factory/source-policy-baseline.csv` remain owned by
  `EPIC-CIRCUS-ML-ONLY-OPERATIONAL-TOOLING-MIGRATION01`.

## Successor

* `EPIC-CIRCUS-ML-ONLY-OPERATIONAL-TOOLING-MIGRATION01` (per
  CORRECTION01 § 13): mass shell-script migration, container
  publication policy, Harbor build/publish orchestration, CI
  mutation and acceptance tests, GitHub helper scripts,
  development-host bootstrap, remaining stage-zero launchers,
  third-party frontend toolchain invocation.  The epic should also
  land the missing 22 dedicated negative mutation tests and a
  machine validator for the parity CSV.
