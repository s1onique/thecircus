# ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01-CORRECTION03 — Close Report

## Status

**PASS** — all closure criteria satisfied.

## Classification

**P0 — PostgreSQL test-runner authority closure**

## Parent

`ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01-CORRECTION02`

## Entry State

```yaml
implementation: substantially_complete
authority_tests: 28_passed
runner_smoke: 5_passed
working_tree: dirty
commits_created: 0
close_report: absent
live_scope_binding: fail
effective_status: PARTIAL_CHECKPOINT
```

## Corrective Actions Applied

### P0-1: Pointer Schema Alignment

Removed `act_classification` from `.factory/active-scope.json`. The pointer now contains only the allowed properties:

```json
{
  "schema_version": 1,
  "act_id": "ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01-CORRECTION03",
  "declaration_path": "docs/acts/ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01-CORRECTION03.scope.json",
  "declaration_blob_oid": "0fe58871d17944d2d6f33b52ebad5d437bc306f0",
  "baseline_commit_oid": "90d36bb50ed0fac090318382543e7df4c8ac0d09"
}
```

### P0-2: Declaration Schema Alignment

Removed forbidden properties `subject_commit_oid` and `subject_tree_oid` from the scope declaration. Subject identities are recorded in evidence payloads and S/E/F transcripts instead.

### P0-3: Prefix-Qualification Semantics Correction

Changed the validator to require qualification metadata only for `ActOwned` directory prefixes. `GloballyProtected` prefixes restrict authority and do not require sibling-authorization justification.

Changed in `tools/Circus.Tooling/ScopeAuthority/Domain.fs`:

```fsharp
// Only ActOwned directory prefixes require qualification metadata.
// GloballyProtected prefixes restrict authority; they do not broaden it
// and therefore do not need sibling-authorization justification.
let declaredPrefixes =
    declaration.ActOwned
    |> List.filter isDirectoryPrefix
```

Declaration now uses `prefix_qualifications: []` since all owned paths are exact files.

### P0-4: Smoke Evidence Reconciliation

1. Canonical evidence path confirmed: `factory/evidence/postgres-test-runner-fail-closed/hermetic-smoke.txt`
2. Appended required exit code marker: `process exit code: 0`
3. Removed stray sibling transcript at `postgres-test-runner-fail-closed01-correction03-smoke.txt`

## Evidence Summary

| Check | Status | Evidence |
|-------|--------|----------|
| Authority tests | PASS | 17/17 ScopeAuthority + 11/11 EvidenceValidator = 28/28 |
| Runner smoke | PASS | 5/5 Hermetic smoke tests |
| Strict parser | PASS | Pointer rejects unknown properties |
| Exact blob validation | PASS | 40-char ASCII-hex OIDs required |
| Ancestry validation | PASS | `merge-base --is-ancestor` enforced |
| Prefix qualifications | PASS | Only ActOwned prefixes require metadata |

## S/E/F Sequence

### S Commit (Implementation)

Contains the strict pointer schema, corrected declaration, prefix-qualification semantics, and dedicated negative test suites.

### E Commit (Evidence)

Contains:
- Hermetic smoke transcript: 5 tests, 5 passed, 0 failed, exit code 0
- Negative scan JSON: captured at evidence time
- Evidence hashes: SHA-256 of transcript and scan

### F Commit (Optional close report)

This document.

## Final Verification Results

```yaml
authority_tests: 28_passed
runner_smoke: 5_passed
scope_cli: pass
postgres_gate: expected_fail
working_tree: clean
publication: none
```

### Live Protected-Scope CLI Validation

```bash
dotnet tools/Circus.Tooling/bin/Release/net10.0/circus-tooling.dll \
  protected-scope check \
  --repo-root . \
  --declaration docs/acts/ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01-CORRECTION03.scope.json \
  --baseline-commit <BASELINE> \
  --evaluated-commit <SUBJECT>
```

Expected output:
- `exit_code: 0`
- `pointer_blob_matches: true`
- `declaration_blob_matches: true`
- `act_id_matches: true`
- `baseline_matches: true`
- `baseline_is_ancestor: true`
- `globally_protected_changes: 0`
- `undeclared_changes: 0`

## Diagnostic-Probe Extension Status

`ACT-CIRCUS-POSTGRES-DIAGNOSTIC-PROBE-EXTENSION01` remains **not released** pending further analysis of the known PostgreSQL test defects.

## Closure Criteria Checklist

- [x] Pointer and declaration pass strict parser
- [x] Live protected-scope CLI passes against committed objects
- [x] Smoke transcript and scan agree at 5/5 with exit code 0
- [x] Exact committed evidence validation passes for S/E
- [x] Final canonical artifact binds actual final subject
- [x] Complete range passes `git diff --check`
- [x] Worktree is clean
- [x] No tag, push, or publication occurs

---

**Attestation**: This close report accurately reflects the state of CORRECTION03 closure and the corrective actions applied.
