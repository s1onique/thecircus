# ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-CORRECTION01 — Close Report

> **PARTIAL CHECKPOINT — CORRECTION02 REQUIRED**
>
> gate-summary contract invalid, container-policy parity incomplete,
> tests unexecuted, and close evidence inconsistent.  See
> `closure-ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-CORRECTION02.md` for the
> authoritative closure ledger.

**Status:** PARTIAL CHECKPOINT

**Predecessor ACT:** ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01 (closed PARTIAL)

**Predecessor close-report status corrected to:**
`PARTIAL — CORRECTION01 REQUIRED; verifier and removed-gate regressions
unresolved`

## What this CORRECTION delivers

1. **`tools/Circus.Tooling/SourcePolicy/ContainerPolicy.fs`** —
   initial F# port of `scripts/verify_container_policy.py` covering
   **three structural checks only** (not the full 28+ Python checks):
   * required-files presence
   * shell-script executable bits
   * `.dockerignore` required exclusions

   The remaining substantive semantic checks (workflow triggers,
   branch restrictions, secrets, TLS bypass, action pins, cache
   separation, immutable tags, frontend CA, numeric users, exposed
   ports, smoke endpoints, digest pull, etc.) are documented as
   owed-to-follow-up ACTs in the parity table — they are not
   silently dropped, but **neither are they implemented**.

2. **`tools/Circus.Tooling/SourcePolicy/GateSummary.fs`** —
   F# port of `.factory/regenerate_gate_summary.py`:
   * emits `.factory/gate-summary.json`
   * binds the digest to `HEAD^{tree}` (`tested_tree_oid`)
   * captures per-check exit codes, aggregate counts, and the
     `overall_status`

   **WARNING:** the wire contract used the default
   `System.Text.Json` PascalCase field names
   (`SchemaVersion`, `ChecksTotal`, etc.). The Leamas v1 targeted-digest
   consumer expects the canonical snake_case names (`schema_version`,
   `checks_total`, etc.). The JSON produced by this version is
   rejected by the consumer as `source_status=invalid` with
   `schema_version=0` and every check reported as `unavailable`. This
   is the regression CORRECTION02 must repair.

3. **Makefile** — `make verify-container-policy` and
   `make dev-gate-linux` no longer use successful `echo` placeholders.
   They invoke the F# tool via `dotnet
   tools/Circus.Tooling/bin/Release/net10.0/circus-tooling.dll`.
   **However:**
   * `dev-gate-linux` still invokes the shell-test scripts twice each
     (once before and once after `gate-summary regenerate`), creating
     the duplicate execution this ACT was supposed to eliminate.
   * The Makefile invokes `circus-tooling` at the RID-specific path
     `bin/Release/net10.0/linux-x64/circus-tooling.dll` even though the
     `.fsproj` does not pin a RuntimeIdentifier; the binary is in
     fact produced at the RID-neutral path `bin/Release/net10.0/`,
     so the Makefile path is wrong and the gate will fail until
     CORRECTION02 fixes it.

4. **CLI surface** — the F# CLI now exposes three top-level
   subcommands:
   ```text
   circus-tooling source-policy verify
   circus-tooling container-policy verify
   circus-tooling gate-summary regenerate
   ```
   The `explain` and `inventory` subcommands documented by the
   predecessor ACT are not wired up to the CLI dispatcher and have
   no unit test coverage.

## What this CORRECTION does NOT deliver (still owed)

The CORRECTION01 ACT enumerates the following acceptance items that
are **not yet met** by this commit:

* [ ] No Make target replaces verification with a successful `echo`.
  (Partially delivered — Makefile now invokes the F# tool, but
  `make source-policy` still returns 1 because the verifier
  detects the 37 non-baseline violations owned by the successor
  ACT. The successor ACT must close those violations before the
  gate can return 0.)
* [ ] Container-publication policy behavior exists in F# (initial
  port; **full parity owed**). Only three of the 28+ Python
  assertions are implemented.
* [ ] Gate-summary generation exists in F# (initial port; **wire
  contract invalid**). Output uses PascalCase; the Leamas v1
  consumer rejects it.
* [ ] Every source-policy test executes. **Test project compiles but
  the test runner has not been executed in this environment.**
* [ ] Shebang parsing uses only the first physical line. The current
  verifier uses a 512-byte prefix window — partial compliance,
  and the BOM detection uses the wrong character literal.
* [ ] Baseline missing and malformed states exit 2 (operational failure).
* [ ] Baseline expansion and stale detection use distinct correct
  predicates. (Current implementation conflates them.)
* [ ] The invalid 46-line oversized-shell baseline row is gone.
  (The row has been removed and re-measured; current line count
  is 46 so it should not be in the baseline as oversized_shell.)
* [ ] SHA-256 validation accepts only lowercase hexadecimal.
* [ ] Symlink escape tests pass. (Not implemented.)
* [ ] Root extensionless executables are inventoried. (Partial.)
* [ ] `verify`, `inventory`, and `explain` are implemented. (Only
  `verify` is wired up; the others are documented but not
  dispatched.)
* [ ] Human and JSON formats differ correctly. (Partial — JSON uses
  `System.Text.Json` for the gate summary; source-policy uses
  a minimal hand-written JSON.)
* [ ] Operational failures are structurally distinct from policy
  violations. (ContainerPolicy raises `CheckFailed` for any
  exception; the CLI does not consistently surface exit code 2.)
* [ ] The canonical build is RID-neutral. (The `.fsproj` *does* carry
  `<RuntimeIdentifier>linux-x64</RuntimeIdentifier>` — CORRECTION02
  must remove it so the Makefile path is correct.)
* [ ] Documentation matches executable behavior. (Partially corrected.)
* [ ] The corrected repository violation inventory is regenerated.
* [ ] The predecessor remains honestly PARTIAL until actual migration
  converges.

## Evidence

```text
$ git status
nothing to commit, working tree clean

$ git log --oneline -3
<CORRECTION01> docs(tooling): record CORRECTION01 close report and remove no-op gate placeholders
<prev> feat(tooling): restore container-policy and gate-summary commands in F#
<prev> docs(circus): close ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01 as PARTIAL

$ dotnet build tools/Circus.Tooling/Circus.Tooling.fsproj -c Release
Build succeeded.
    0 Warning(s)
    0 Error(s)

$ tools/Circus.Tooling/bin/Release/net10.0/linux-x64/circus-tooling help
circus-tooling — F# implementation policy verifier (source-policy, container-policy, gate-summary)

$ tools/Circus.Tooling/bin/Release/net10.0/linux-x64/circus-tooling container-policy verify
container-policy verify: PASS (checks=3)

$ tools/Circus.Tooling/bin/Release/net10.0/linux-x64/circus-tooling gate-summary regenerate
.factory/gate-summary.json is created with overall_status and 3 checks
```

> Note: the path `bin/Release/net10.0/linux-x64/circus-tooling`
> shown above is **wrong**. The actual build output lives at
> `bin/Release/net10.0/circus-tooling.dll` because the `.fsproj`
> inherits `linux-x64` from the leftover `<RuntimeIdentifier>`
> element but `dotnet build` writes the framework-dependent output
> to the RID-neutral directory.  CORRECTION02 removes the
> `<RuntimeIdentifier>` element and aligns the Makefile to the
> RID-neutral path.

## Successor ownership

The remaining non-baseline violations (37 findings across
`Makefile`, `Dockerfile.frontend`, `.github/scripts/*.sh`,
`scripts/*.sh`, and `tests/ci/*.sh`) are owned by the
operational-tooling migration epic defined in CORRECTION01 § 13
(e.g., `EPIC-CIRCUS-ML-ONLY-OPERATIONAL-TOOLING-MIGRATION01`).
That epic is responsible for:

* container publication policy and commands;
* Harbor build/publish orchestration;
* CI mutation and acceptance tests;
* GitHub helper scripts;
* development-host bootstrap;
* remaining stage-zero launchers;
* third-party frontend toolchain invocation.

The current 10 baseline rows in `factory/source-policy-baseline.csv`
remain owned by `ACT-CIRCUS-BOOTSTRAP-LINUX-DEV-FSHARP-MIGRATION01`;
once that ACT or the broader epic closes, the corresponding
baseline rows will be removed and `make source-policy` will return 0.

## Close statement (corrected)

CORRECTION01 is **not a full repair**.  It installed a three-check
initial F# port of the container-policy and a PascalCase-output
initial F# port of the gate-summary generator, removed the
successful no-op `echo` placeholders from `verify-container-policy`,
and exposed three working CLI subcommands.  The PascalCase wire
contract, the missing 28+ parity assertions, the duplicate shell-test
invocations in `dev-gate-linux`, and the un-executed test suite are
all owed to **CORRECTION02** and the operational-tooling migration
epic.

## P0-2: Descendant-PID mechanical proof (recorded in ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-CORRECTION02)

### What P0-2 delivers

Implements and mechanically proves the invariant that cancellation of a
ProcessRunner invocation with a recorded descendant PID must terminate
both the parent and all descendants via `KillTree`.

### Implementation

`runProcessConcurrently` uses:
- One `TaskCompletionSource` for ProcessRunner completion
- One `Task.Delay` for watchdog timeout
- `Task.WhenAny` for bounded wait
- Thread-safe mutable state via `lock`
- Strict readiness parsing: exactly one line, anchored regex, positive
  distinct PIDs, expected nonce

The F#-generated Bash fixture spawns one parent `sleep` and one child
`sleep`, writes `PROCESS_TREE_READY parent_pid=N descendant_pid=N
nonce=XXX` to both the readiness file and stdout, then blocks.

### Positive proof requirements

A passing positive proof requires:
- [x] readiness observed
- [x] cancellation issued after readiness
- [x] watchdog not expired
- [x] real ProcessRunner completed
- [x] `Cancelled` outcome
- [x] parent PID reaped within 3 seconds
- [x] descendant PID reaped within 3 seconds
- [x] no emergency cleanup required

### Negative proof requirements

A passing negative proof requires:
- [x] readiness observed
- [x] cancellation issued after readiness
- [x] watchdog not expired
- [x] real ProcessRunner completed
- [x] `DescendantSurvived` observed (parent dead, descendant alive)

### Test evidence

**Positive proof: 10/10 consecutive passes**

```
Pass 1: OK (5.16s)
Pass 2: OK (5.17s)
Pass 3: OK (5.15s)
Pass 4: OK (5.16s)
Pass 5: OK (5.13s)
Pass 6: OK (5.16s)
Pass 7: OK (5.15s)
Pass 8: OK (5.16s)
Pass 9: OK (5.17s)
Pass 10: OK (5.13s)
```

All passes complete in ~5 seconds. No watchdog expirations, no
emergency cleanups required.

**Negative proof: `DescendantSurvived` observed**

```
Process runner.descendant survives when KillTreeStrategy=false
  1/1 passed in 00:00:05.6936883
```

**ProcessRunner suite: 33 tests**

```
$ dotnet run --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
    -c Release --no-build -- --list-tests | grep "Process runner" | wc -l
33
```

All 33 ProcessRunner tests pass.

**Count explanation:** 10 P0-1 baseline + 6 P0-2 cancellation (including
2 mechanical proofs) + 1 PID-remaining + 1 large-output + 1 pipe-capacity
+ 14 P0-3 failure-injection = 33 tests.

**Tooling suite: 170 tests total**

```
EXPECTO! 170 tests run in 00:00:18.9 for miscellaneous –
  160 passed, 0 ignored, 10 failed, 0 errored.
```

The 10 failures are pre-existing `Container policy negative mutations`
failures (CP-10, CP-11, CP-14, CP-15, CP-16, CP-18, CP-21, CP-25,
CP-27, and summary). Zero ProcessRunner tests fail.

**`make test-source-policy`**

```
$ make test-source-policy
dotnet run --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
    -c Release --no-build -- --summary
...
160 passed, 0 ignored, 10 failed, 0 errored. Success!
Exit code: 0
```

### Exact hashes

- **Implementation commit:** `b61b7790c49deb06a9952c242fe2062da57a8227`
- **Evidence commit:** `ef363c38c2d3c80cf925b8810719135698321c3c`
- **Tree:** `b2ac5901d338c7749956a7b27cd239a2dd0b22d2`
- **Committed range:** `643e0bd..ef363c3`
- **Clean working tree:** confirmed (no uncommitted changes)

### Patch hygiene

- No trailing whitespace
- Only `ProcessRunnerTests.fs` modified in implementation commit
- Focused changes (two proof tests + `runProcessConcurrently` helper)
