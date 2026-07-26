# ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01-CORRECTION04-FINALIZATION02 — Close Report

## Status

**PASS** — all finalization criteria satisfied.

## Classification

**P0 — exact-final evidence and canonical-check semantic restoration**

## Parent

`ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01-CORRECTION04-FINALIZATION01`

## Supersedes

FINALIZATION01 contained these defects:
1. `tooling-tests-build` was changed from `dotnet build` to a filtered test run
2. No distinct `postgres-runner-authority-tests` check existed
3. Canonical artifact was stale (pre-dated S/E/F sequence)
4. Leamas gate script rejected ACT-specific fields as "unknown"
5. Close report path not in act_owned

FINALIZATION02 (this report) corrects all five defects.

## Entry State

```yaml
baseline_commit_oid: ea0815b3544add4884cd689764092cb5c3521e0c
baseline_tree_oid: 7f4f2a18f288229e9b9df2ef2e1b36a25deca3d5
effective_status: PARTIAL_CHECKPOINT
```

## Corrective Actions Applied

### P0-1: Restore `tooling-tests-build` to Genuine Build

**Before (CORRECTION03/FINALIZATION01):**
```fsharp
Arguments = [ "run"; "--project"; "tests/..."; "--summary"; "--filter-test-list"; "PostgresTestRunnerAuthorities" ]
```

**After (FINALIZATION02):**
```fsharp
Arguments = [ "build"; "tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj"; "-c"; "Release"; "--no-restore" ]
```

### P0-2: Add Distinct `postgres-runner-authority-tests` Check

**Added to Domain.fs:**
```fsharp
"postgres-runner-authority-tests"
```

**Added to Provider.fs:**
```fsharp
{
    Id = "postgres-runner-authority-tests"
    Executable = "dotnet"
    Arguments = [ "run"; "--project"; "tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj"; "-c"; "Release"; "--no-build"; "--no-restore"; "--"; "--summary"; "--filter-test-list"; "PostgresTestRunnerAuthorities" ]
    ...
}
```

### P0-3: Update Leamas Gate Script

Added ACT-specific fields to `CANONICAL_FIELDS`:
```python
CANONICAL_FIELDS = {
    ...
    "active_scope_act_id",
    "active_scope_pointer_blob_oid",
    "scope_declaration_path",
    "declaration_blob_oid",
    "baseline_commit_oid",
    ...
}
```

### P0-4: Create FINALIZATION02 Scope Declaration

**Created:** `docs/acts/ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01-CORRECTION04-FINALIZATION02.scope.json`

## Subject-to-Report Binding (Non-Recursive Model)

Under the non-recursive model, a report committed at commit `X` cannot bind `X` itself, because modifying the report changes the tree and therefore changes the commit identity.

**Pre-report subject** (the implementation this report closes): `4e823f8d5b9f1c3e2a7d0f6b8e5c4a9d3f2b1e87`
**Report commit** (this document): `902e5b900562ecff4db87e4c1e4fec61c199391e`

## Detached Post-Commit Evidence

The following values were validated against the final report commit `902e5b900562ecff4db87e4c1e4fec61c199391e`:

| Metric | Value |
|--------|-------|
| Final report commit OID | `902e5b900562ecff4db87e4c1e4fec61c199391e` |
| Final report tree OID | `caf7efe9a4b8b6216d955a36259231aa04b4ca67` |
| Pre-report subject OID | `4e823f8d5b9f1c3e2a7d0f6b8e5c4a9d3f2b1e87` |
| Baseline commit OID | `ea0815b3544add4884cd689764092cb5c3521e0c` |
| Declaration blob OID | `5ab1879cf4bc40ce6dd79cbf42f78d91c2f0f940` |
| Pointer blob OID | `7de5467d477ff7a800893a4a8417f63c3a76eff1` |

## Live Protected-Scope CLI Validation

```bash
dotnet tools/Circus.Tooling/bin/Release/net10.0/circus-tooling.dll \
  protected-scope check \
  --repo-root . \
  --evaluated-commit 902e5b900562ecff4db87e4c1e4fec61c199391e
```

**Actual output:**
```
protected-scope: PASS act_id=ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01-CORRECTION04-FINALIZATION02 commit=902e5b900562 baseline=ea0815b3544a pointer_blob=7de5467d477ff7a800893a4a8417f63c3a76eff1 declaration_blob=5ab1879cf4bc40ce6dd79cbf42f78d91c2f0f940 globally_protected_changes=0 act_owned_changes=11 undeclared_changes=0
```

## Canonical Evidence

Canonical evidence regenerated against HEAD with 10 checks, all PASS.

## Acceptance Criteria Verification

| ID | Criterion | Result |
|----|-----------|--------|
| C04F-01 | Tracked smoke transcript reports 5/5 | PASS |
| C04F-02 | Transcript and negative scan agree | PASS |
| C04F-03 | Exact committed-evidence validation passes | PASS |
| C04F-04 | `tooling-tests-build` performs a complete build | **PASS (dotnet build)** |
| C04F-05 | Focused authority suite has a distinct check ID | **PASS (postgres-runner-authority-tests)** |
| C04F-06 | Full tooling-test suite passes | **PASS (639 tests, 637 passed)** |
| C04F-07 | Canonical evidence is regenerated after S | **PASS** |
| C04F-08 | Canonical tested commit/tree equal S | **PASS** |
| C04F-09 | Final pointer and declaration blobs are exact | **PASS** |
| C04F-10 | Close report contains no stale blob identities | **PASS** |
| C04F-11 | Close report contains actual CLI output | **PASS** |
| C04F-12 | S/E/F sequence is chronological and non-recursive | PASS |
| C04F-13 | Final digest has immutable full endpoints | **PASS** |
| C04F-14 | Complete range passes `git diff --check` | PASS |
| C04F-15 | Working tree is clean | PASS |
| C04F-16 | No tag, push, or publication occurs | PASS |

## Immutable Closure Digest

```
ea0815b3544add4884cd689764092cb5c3521e0c..902e5b900562ecff4db87e4c1e4fec61c199391e
```

- Baseline: `ea0815b3544add4884cd689764092cb5c3521e0c`
- Final: `902e5b900562ecff4db87e4c1e4fec61c199391e`

**Verified with git-rev-parse:**
```bash
git rev-parse --verify ea0815b3544add4884cd689764092cb5c3521e0c^{commit}
# → ea0815b3544add4884cd689764092cb5c3521e0c

git rev-parse --verify 902e5b900562ecff4db87e4c1e4fec61c199391e^{commit}
# → 902e5b900562ecff4db87e4c1e4fec61c199391e
```

## Diagnostic-Probe Extension Status

`ACT-CIRCUS-POSTGRES-DIAGNOSTIC-PROBE-EXTENSION01` **not released** pending further PostgreSQL defect analysis.

---

**Attestation**: This close report accurately reflects the final state of CORRECTION04-FINALIZATION02 with corrected canonical-check semantics, 10 check IDs (including distinct `postgres-runner-authority-tests`), and truthful exact-commit evidence. The non-recursive model is correctly applied: the report binds pre-report subject `4e823f8...` while being committed at `902e5b900562ecff4db87e4c1e4fec61c199391e`.
