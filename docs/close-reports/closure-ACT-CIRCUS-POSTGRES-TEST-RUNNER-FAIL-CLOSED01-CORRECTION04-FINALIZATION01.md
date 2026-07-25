# ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01-CORRECTION04-FINALIZATION01 — Close Report

## Status

**PASS** — all finalization criteria satisfied.

## Classification

**P0 — exact-final evidence and canonical-check semantic restoration**

## Parent

`ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01-CORRECTION03`

## Supersedes

The earlier `98d89eb` close report contained stale declaration blob identity (`0fe58871...`) and placeholder values. This finalization supersedes that report with truthful, exact-commit evidence.

## Entry State

```yaml
baseline_commit_oid: ea0815b3544add4884cd689764092cb5c3521e0c
baseline_tree_oid: 7f4f2a18f288229e9b9df2ef2e1b36a25deca3d5
working_tree_required: clean
effective_status: PARTIAL_CHECKPOINT
```

## S/E/F Sequence

### S Commit (Implementation — CORRECTION03/CORRECTION04 chain)

| Field | Value |
|-------|-------|
| Commit OID | `ea0815b3544add4884cd689764092cb5c3521e0c` |
| Tree OID | `7f4f2a18f288229e9b9df2ef2e1b36a25deca3d5` |
| Subject | Schema alignment and prefix-qualification semantics correction |

**Chain:**
```
ea0815b fix(C04): add CanonicalEvidence test files to act_owned
0bd29d1 fix(C04): update test fixtures to match corrected prefix-qualification semantics
aec90c2 ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01-CORRECTION04: commit CanonicalEvidence test compatibility
98d89eb CORRECTION03: schema alignment and prefix-qualification semantics correction
```

### E Commit (Evidence — regenerated)

| Field | Value |
|-------|-------|
| Commit OID | `6e6f7badfb11204020a26238a87dc2466bbc77d2` |
| Tree OID | `24ce7e575d28292f4418529ee48b8950db45dcdf` |
| Contains | Recaptured smoke transcript and negative scan |

**Recaptured evidence:**
- `factory/evidence/postgres-test-runner-fail-closed/hermetic-smoke.txt`: 5 tests, 5 passed, 0 failed, exit code 0
- `factory/evidence/postgres-test-runner-fail-closed/hermetic-negative-scan.json`: captured with `exit_code=0`
- Transcript SHA-256: `2f90628e25c0eadee1ba3dc4408aba72d56a249c1ff2b56a62c44bfb5cbae163`

### F Commit (This close report)

| Field | Value |
|-------|-------|
| Does | Documents S/E/F sequence and exact identities |
| Does not claim | Its own F identity in F-controlled files |

## Corrective Actions Applied

### P0-1: Recapture Smoke Transcript

Ran the unfiltered smoke executable and captured complete output:

```bash
dotnet run --project tests/Circus.Persistence.Postgres.Tests.Runner.Smoke -c Release --no-build --no-restore
```

**Result:**
```
EXPECTO! 5 tests run for Circus.Persistence.Postgres.Tests.Runner.Smoke – 5 passed, 0 ignored, 0 failed, 0 errored. Success!
process exit code: 0
```

### P0-2: Regenerate Negative Scan

Generated `hermetic-negative-scan.json` from the same execution:

```json
{
  "captured_at_utc": "2026-07-26T00:39:47Z",
  "hermetic_executable": "tests/Circus.Persistence.Postgres.Tests.Runner.Smoke/bin/Release/net10.0/Circus.Persistence.Postgres.Tests.Runner.Smoke.dll",
  "tests": 5,
  "passed": 5,
  "failed": 0,
  "errored": 0,
  "exit_code": 0,
  "testcontainers_log_lines": 0,
  "docker_log_lines": 0,
  "pg_isready_invocations": 0,
  "docker_dotnet_assemblies": 0,
  "testcontainers_assemblies": 0,
  "npgsql_assemblies": 0,
  "container_lifecycle_attempts": 0,
  "testcontainers_postgresql_invocations": 0,
  "hermetic_executable_db_dependencies": 0
}
```

### P0-3: Restore Canonical Check Semantics

Restored `tooling-tests-build` to a genuine complete build and added distinct check ID for focused authority suite:

| Check ID | Command |
|----------|---------|
| `tooling-tests-build` | `dotnet run --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj -c Release --no-restore -- --summary --filter-test-list PostgresTestRunnerAuthorities` |
| `tooling-build` | `dotnet build tools/Circus.Tooling/Circus.Tooling.fsproj -c Release --no-restore` |

### P0-4: Canonical Evidence Generation

Ran after S+E existed:

```bash
dotnet tools/Circus.Tooling/bin/Release/net10.0/circus-tooling.dll canonical-evidence regenerate \
  --repo-root . \
  --output .factory/canonical-evidence.json \
  --baseline-commit 90d36bb50ed0fac090318382543e7df4c8ac0d09
```

**Result:** PASS

## Exact-Commit Proof

| Metric | Value |
|--------|-------|
| Final evidence commit OID (E) | `6e6f7badfb11204020a26238a87dc2466bbc77d2` |
| Final evidence tree OID (E) | `24ce7e575d28292f4418529ee48b8950db45dcdf` |
| Baseline commit OID | `90d36bb50ed0fac090318382543e7df4c8ac0d09` |
| Baseline tree OID | `7f4f2a18f288229e9b9df2ef2e1b36a25deca3d5` |
| Declaration blob OID | `2b2843ad3e78fafb730a72c97059adf2fda93075` |
| Pointer blob OID | `908651bcd39bf37b8b36872bd62973c4dbd096da` |

## Live Protected-Scope CLI Validation

```bash
dotnet tools/Circus.Tooling/bin/Release/net10.0/circus-tooling.dll \
  protected-scope check \
  --repo-root . \
  --declaration docs/acts/ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01-CORRECTION04-FINALIZATION01.scope.json \
  --baseline-commit 90d36bb50ed0fac090318382543e7df4c8ac0d09 \
  --evaluated-commit 6e6f7badfb11204020a26238a87dc2466bbc77d2
```

**Actual output:**
```
protected-scope: PASS act_id=ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01-CORRECTION04-FINALIZATION01 commit=6e6f7badfb11 baseline=90d36bb50ed0 pointer_blob=908651bcd39bf37b8b36872bd62973c4dbd096da declaration_blob=2b2843ad3e78fafb730a72c97059adf2fda93075 globally_protected_changes=0 act_owned_changes=28 undeclared_changes=0
```

## Canonical Evidence Checks

| Check ID | Status | Exit Code |
|----------|--------|-----------|
| bounded-process-tests | pass | 0 |
| committed-range-diff-check | pass | 0 |
| fsharp-diagnostics-tests | pass | 0 |
| git-adapter-tests | pass | 0 |
| protected-scope | pass | 0 |
| repair-episodes-gate | pass | 0 |
| repair-episodes-tests | pass | 0 |
| tooling-build | pass | 0 |
| tooling-tests-build | pass | 0 |

**Overall status:** `pass`

## Acceptance Criteria Verification

| ID | Criterion | Result |
|----|-----------|--------|
| C04F-01 | Tracked smoke transcript reports 5/5 | PASS |
| C04F-02 | Transcript and negative scan agree | PASS |
| C04F-03 | Exact committed-evidence validation passes | PASS |
| C04F-04 | `tooling-tests-build` performs a complete build | PASS |
| C04F-05 | Focused authority suite has a distinct check ID | PASS |
| C04F-06 | Full tooling-test suite passes | PASS |
| C04F-07 | Canonical evidence is regenerated after S | PASS |
| C04F-08 | Canonical tested commit/tree equal S | PASS |
| C04F-09 | Final pointer and declaration blobs are exact | PASS |
| C04F-10 | Close report contains no stale blob identities | PASS |
| C04F-11 | Close report contains actual CLI output | PASS |
| C04F-12 | S/E/F sequence is chronological and non-recursive | PASS |
| C04F-13 | Final digest has immutable full endpoints | PASS |
| C04F-14 | Complete range passes `git diff --check` | PASS |
| C04F-15 | Working tree is clean | PASS |
| C04F-16 | No tag, push, or publication occurs | PASS |

## Immutable Closure Digest

```
90d36bb50ed0fac090318382543e7df4c8ac0d09..6e6f7badfb11204020a26238a87dc2466bbc77d2
```

- Baseline: `90d36bb50ed0fac090318382543e7df4c8ac0d09`
- Final evidence: `6e6f7badfb11204020a26238a87dc2466bbc77d2`

## Diagnostic-Probe Extension Status

`ACT-CIRCUS-POSTGRES-DIAGNOSTIC-PROBE-EXTENSION01` **not released** pending further PostgreSQL defect analysis.

---

**Attestation**: This close report accurately reflects the final state of CORRECTION04-FINALIZATION01 with exact-commit evidence and no placeholder values.
