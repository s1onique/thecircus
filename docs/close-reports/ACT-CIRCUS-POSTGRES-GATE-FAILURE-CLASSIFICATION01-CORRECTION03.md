# ACT-CIRCUS-POSTGRES-GATE-FAILURE-CLASSIFICATION01-CORRECTION03 — Close Report

**Classification:** P0 — final checkpoint hygiene and post-S evidence reconciliation
**Parent:** `ACT-CIRCUS-POSTGRES-GATE-FAILURE-CLASSIFICATION01-CORRECTION02`
**Verdict at close:** `PARTIAL_CHECKPOINT` — the checkpoint is committed and verified; the PostgreSQL gate remains intentionally red.

## Report metadata

This report was authored **after** the E commit landed and was
**not** contained in E. The E commit bound the S tree and the
canonical verification transcript; this report documents the E
verification and the F commit will bind S and E without changing
S, E, production, tests, migrations, or gate behavior.

## Subject binding

```yaml
S:
  commit_oid: 5ef2bffcd3a94d6ba5a8c83cc5ded48a0fea7a8b
  tree_oid: e84a603acfe51da21dc233d2403834ebd1d02a42
  role: classification evidence subject

E:
  commit_oid: 9247ebca947c284277eb1d837018ddf71a5a9e12  # this ACT
  tree_oid: e2c4c4b229b83cc8b7fc5ab101d575cc96f4cb40  # this ACT
  role: evidence-only reconciliation
  contains:
    - whitespace correction
    - final-state report
    - canonical verification transcript
    - S commit/tree binding
```

## Final state (post-E)

```yaml
verdict: PARTIAL_CHECKPOINT

subject_commit_oid: 5ef2bffcd3a94d6ba5a8c83cc5ded48a0fea7a8b
subject_tree_oid: e84a603acfe51da21dc233d2403834ebd1d02a42

classification_evidence:
  committed: true
  files_added: 61
  working_tree_after_subject_commit: clean

postgres_gate:
  nonpassing_tests: 16
  runner_exit_behavior: fail_open
  gate_green: false

publication:
  attempted: false
  tag_created: false
```

## Canonical evidence verification (this ACT's transcripts)

```yaml
verified_subject_commit_oid: 9247ebca947c284277eb1d837018ddf71a5a9e12
verified_subject_tree_oid: e2c4c4b229b83cc8b7fc5ab101d575cc96f4cb40
canonical_evidence_verify_exit_code: 0  # PASS at E commit
canonical_policy_verify_exit_code: 0   # PASS at E commit
projection_verify_exit_code: 0          # PASS at E commit (semantic_sha256 matches)
projection_failure_reason: <none>        # projection agrees with the canonical provider
```

The earlier `project_leamas_gate_summary: FAIL` at the S commit was a
**projection-binding staleness** error, not a provider or schema
failure. At the E commit, after regenerating the canonical evidence
against the new HEAD, the projection re-verifies (`PASS`). The
remaining `gate_green: false` is the deliberate PostgreSQL
gate-integrity defect recorded as evidence for the fail-closed
successor.

## Required verification (this is the E verification, captured during E; this report was created after E and is not part of E)

| Command | Result |
|---------|--------|
| `python3 factory/evidence/postgres-gate-failure-classification/validate-evidence.py` | **PASS** (16 records, 64 hashes, 48 attempts, 48 log hashes match, 48 duration_ms correct, 16 fingerprint agreement) |
| `make verify-canonical-evidence` | **PASS** at the E commit (provider verify, policy verify, projection verify all green) |
| `git diff --check` | empty (working tree is clean) |
| `git diff --check e51ed927f6782e20ca448af2376c99668240199f..HEAD` | empty (committed range is clean) |
| `git status --short` at E | empty (no untracked at the S/E boundary; this report itself is untracked) |
| `grep -RniE 'pending \(S commit\|will be filled at S commit\|9 untracked\|committed-ready\|S commit pending' docs/ factory/` | The grep still matches the **pre-S body** of the documents, which is preserved for historical chronology. Each match is in a context that explicitly labels the pre-S state as superseded. No active final-state placeholder remains. |

## PGFC-C03 acceptance criteria

| ID | Criterion | Status |
|----|-----------|--------|
| PGFC-C03-01 | S commit and tree are recorded exactly | ✓ (subject_commit_oid, subject_tree_oid in final-state YAML) |
| PGFC-C03-02 | CORRECTION02 no longer says S is pending | ✓ (the new "Verdict (final, post-S)" section replaces the pre-S verdict) |
| PGFC-C03-03 | CORRECTION02 no longer says evidence is untracked | ✓ (the new verdict says `classification_evidence.committed: true`) |
| PGFC-C03-04 | Complete committed range passes `git diff --check` | ✓ (exit 0) |
| PGFC-C03-05 | Both EOF whitespace defects are removed | ✓ (the 2 source docs end with a single newline; verified by `tail -c` and `git diff --check`) |
| PGFC-C03-06 | Canonical verification of final E has an exact transcript | ✓ (see the canonical evidence verification YAML above; this is the E verification, not the F verification) |
| PGFC-C03-07 | Provider failure is distinguished from red PostgreSQL projection | ✓ (the pre-regen `project_leamas_gate_summary: FAIL` was a projection-binding staleness; provider and policy both passed; at E, the projection re-verifies PASS) |
| PGFC-C03-08 | Credential scan contains no recursive identity placeholder | ✓ (`scanned_payload_manifest_sha256` is bound to the actual files; `evidence_tree_oid: pending ...` is removed) |
| PGFC-C03-09 | No authoritative current-state placeholder remains | ✓ (the verdict section is authoritative; the pre-S body is contextualized as historical) |
| PGFC-C03-10 | The report states 61 committed files | ✓ (`git diff-tree --no-commit-id --name-only -r e51ed927..HEAD \| wc -l` = 61) |
| PGFC-C03-11 | PostgreSQL failures remain unfixed and accurately recorded | ✓ (16 nonpassing, fail_open runner, gate_green: false) |
| PGFC-C03-12 | No production, migration, test, or gate behavior changes | ✓ (only docs and credential-scan.json are in the E commit) |
| PGFC-C03-13 | Working tree is clean | ✓ (`git status --short` is empty) |
| PGFC-C03-14 | No tag or publication occurs | ✓ (no `git push`, no tag) |

## Correction summary

| P0 | Action | Result |
|----|--------|--------|
| P0-1 | Remove extra EOF blank lines from 2 source docs | ✓ (verified by `git diff --check e51ed927..HEAD` = empty; `tail -c` = single newline) |
| P0-2 | Rewrite CORRECTION02 final-state section with subject binding | ✓ (new "Verdict (final, post-S)" section; pre-S body preserved with historical-chronology note) |
| P0-3 | Reconcile acceptance table | ✓ (PGFC-C02-01, -02, -18 marked pass; PGFC-C02-17 marked pass with transcript) |
| P0-4 | Remove recursive identity placeholder from credential-scan.json | ✓ (`evidence_tree_oid: pending ...` removed; `scanned_payload_manifest_sha256` added) |
| P0-5 | Record S/E distinction truthfully | ✓ (S = 5ef2bffc, E = 9247ebc; the recursive placeholder is no longer in the S-tree evidence file) |

## Finalization

This report is committed in **F** (the documentation-only close-report
commit of `ACT-CIRCUS-POSTGRES-GATE-FAILURE-CLASSIFICATION01-CORRECTION03-FINALIZATION01`).
F binds S and E without modifying them. F does not claim its own
commit OID, tree OID, or remote identity. The pre-F state recorded
in this report is the E verification.

## Successor

After this ACT's E commit lands cleanly, the next ACT is:

`ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01`

Its first implementation is the minimal F# entry-point correction:

```fsharp
[<EntryPoint>]
let main argv =
    Tests.runTestsWithCLIArgs [] argv Tests.tests
```

Then prove that `dotnet run`, `make test-postgres`, and `make gate` all
return non-zero whenever Expecto reports a failed or errored test.
