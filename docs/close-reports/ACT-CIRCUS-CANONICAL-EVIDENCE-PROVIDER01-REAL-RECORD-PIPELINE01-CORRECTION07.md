# ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01-REAL-RECORD-PIPELINE01-CORRECTION07

**Status**: INVALID_IMPLEMENTATION_CHECKPOINT (recovery in progress)

**Date**: 2026-07-29

**Objective**: Implement staged multi-file snapshot publication with strict round-trip validation

---

## Verdict

```yaml
verdict: INVALID_IMPLEMENTATION_CHECKPOINT

implemented:
  FailureKind_added_to_record_canonicalisation: true
  exact_byte_write_pipeline_prototyped: true
  mutation_seam_prototyped: true
  four_file_reread_prototyped: true
  typed_staged_failure_model_started: true
  compilation_recovery: true
  active_path_wiring: true
  duplicate_schema_removed: true

invalid_or_open:
  compilation: false
  active_path_wiring: false
  single_publication_authority: true
  compatibility_schema_convergence: true
  strict_parser_semantics: false
  aggregate_recomputation: false
  manifest_exact_inventory: false
  complete_compatibility_validation: false
  cleanup_failure_semantics: false
  executable_tests: false
  patch_hygiene: false
  evidence_binding: false
  fresh_gate: false

recovery_commits:
  8a82730: compilation recovery and duplicate schema fix
  8755bc8: active path delegation and schema cleanup
```

---

## Recovery Sub-ACT

**ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01-REAL-RECORD-PIPELINE01-CORRECTION07-CORRECTION01-ACTIVE-PATH-AND-PARSER-RECOVERY01**

### Recovery Order

1. Remove the second `CanonicalEvidenceRecord` compatibility schema
2. Restore one compatibility model: existing `CanonicalEvidence.Checks`
3. Consolidate the duplicate record-validation types
4. Replace every invalid `JsonElement` byref call with typed mutable locals
5. Build strict field lookup helpers once; remove the two-pass record parser
6. Make aggregate and manifest parsing genuinely typed and fail-closed
7. Make `stageAndPublishSnapshot` return correctly on mutation failure
8. Update every `PublicationOutcome` construction
9. Wire the CLI to the staged publisher and remove the superseded active publisher
10. Recompute the aggregate from parsed staged records
11. Run full compatibility validation before replacement
12. Add the missing CORRECTION07 test suites
13. Remove all 20 whitespace defects and placeholder evidence
14. Build, execute tests, commit, and only then resume the exact-subject proof and fresh gate phases

---

## Blocking Issues Identified

### 1. Invalid `TryGetInt64` and `TryGetInt32` Use

The parser contains:

```fsharp
if found.TryGetInt64(&found) then
    found
```

`found` is a `JsonElement`, but `TryGetInt64` requires an `out int64`. The correct shape is:

```fsharp
let mutable value = 0L
if found.TryGetInt64(&value) then
    value
else
    ...
```

### 2. Invalid Byref Expressions

Calls such as:

```fsharp
root.TryGetProperty("path", &Unchecked.defaultdefault<_>)
```

Need writable byref targets, normally mutable locals.

### 3. `CanonicalEvidence` Does Not Have `Records`

The existing type uses `Checks`, not `Records`. The publication code introduces an incompatible second schema.

### 4. `PublicationOutcome` Constructors Are Incomplete

New fields `LiveSnapshotMayHaveChanged` and `CleanupFailure` are not supplied in all construction sites.

### 5. Mutation Failure Cannot Return Early

The error branch produces `PublicationOutcome` while other branches produce `unit`, causing type inconsistency.

### 6. Duplicate Record-Validation Types

`RecordValidationIssue` unions exist in both `EvidenceRecords.fs` and `RecordPipeline.fs`.

### 7. Parsers Are Not Strict

- Optional helpers silently map missing/null/wrong-type to `None`
- `started_at` is never parsed as a timestamp
- Commit and tree OIDs are not validated
- Partial test-count sets are accepted

### 8. Staged Round Trip Is Incomplete

- Normalizes CRLF to LF (accepts noncanonical bytes)
- Does not enforce exactly one terminal LF
- Aggregate recomputation is missing
- Compatibility validation is shallow

### 9. Cleanup Failure Preservation Is Incomplete

Mutation failure, successful replacement, and replacement failure paths do not protect cleanup calls.

### 10. No CORRECTION07 Tests Exist

### 11. Placeholder Evidence in Close Report

Baseline SHA-256 and tree use placeholder values.

---

## What's Implemented (Prototype Quality)

| Feature | Status |
|---------|--------|
| Canonical UTF-8 bytes authority | ⚠️ Prototype |
| Staged file write phase | ⚠️ Prototype |
| Mutation seam | ⚠️ Prototype (broken return) |
| All-four-file disk reread | ⚠️ Prototype |
| Strict evidence wire parser | ❌ Incomplete semantics |
| Strict aggregate wire parser | ❌ Incomplete semantics |
| Strict artifact manifest parser | ❌ Incomplete semantics |
| Compatibility evidence validation | ❌ Incomplete semantics |
| Typed staged validation failures | ⚠️ Type conflicts |
| Typed cleanup-failure preservation | ❌ Incomplete |
| Previous-snapshot preservation | ❌ Incomplete |

---

## Next Steps

1. **Fix EvidenceRecords.fs JSON parsing** - Valid byref patterns, proper TryGet usage
2. **Fix Publication.fs** - Compatibility schema convergence, complete cleanup protection
3. **Wire CLI to staged publisher** - Replace active publisher
4. **Implement strict parsers** - Fail-closed, typed field lookup
5. **Add aggregate recomputation** - Parse records → compute aggregate → compare
6. **Add CORRECTION07 tests** - All missing test suites
7. **Fix whitespace and evidence** - Real baseline SHA-256, no placeholders
8. **Build, test, commit** - Full pipeline execution
