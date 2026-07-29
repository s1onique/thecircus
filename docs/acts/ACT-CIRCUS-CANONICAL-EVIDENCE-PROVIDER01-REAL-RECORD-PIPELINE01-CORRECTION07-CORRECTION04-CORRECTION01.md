# ACT: ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01-REAL-RECORD-PIPELINE01-CORRECTION07-CORRECTION04-CORRECTION01

## Title

Production authority and nonvacuous proof for canonical evidence test suites

## Parent ACT

ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01-REAL-RECORD-PIPELINE01-CORRECTION07-CORRECTION04 (INVALID_CLOSURE_CHECKPOINT)

## Status

IN_PROGRESS

## Date

2026-07-29

## Problem Statement

CORRECTION04 attempted to implement 5 terminal test suites for the canonical evidence record pipeline. While 75 tests were added and all pass, the reviewer correctly identified that the tests only validate test-local helper logic rather than production code paths.

### Specific Deficiencies

**Workstream A — Compatibility structural equality:**
- `compareCompatibilityProjection` is defined inside the test file only
- Production staged publisher does not invoke this comparator
- Missing/unknown checks calculated only when list lengths are equal
- `Set.ofList` and `Map.ofList` discard duplicate-ID multiplicity
- No staged mutation integration

**Workstream B — Aggregate structural equality:**
- `compareAggregateProjection` is defined inside the test file only
- Not all aggregate fields are mutated
- No production recomputation authority wired in

**Workstream C — Typed cleanup-failure behavior:**
- No cleanup failure is actually injected
- Pattern matches accept any failure via `_ -> ()`
- No proof of cleanup invoked exactly once

**Workstream D — Partial replacement and restoration:**
- No replacement failure is injected
- `previous snapshot preserved on staging error` contains no staging error
- No rollback or restoration failure exercised

**Workstream E — Provider once-only orchestration:**
- Suite does not call the provider
- Two tests are materially vacuous (`() -> ()`)
- No counters for RunCheck, clock calls, ordering

### Additional Issues

- 20 whitespace defects in committed files
- CLI publication integration tests absent
- Fresh gate not run

## Solution

### Phase 1: Production Authority (Required)

1. **Move compatibility comparator to production**
   - Extract `CompatibilityDifference` and `compareCompatibilityProjection` to `Publication.fs`
   - Wire into staged validation before live file replacement
   - Ensure duplicate-check detection without multiplicity loss

2. **Move aggregate comparator to production**
   - Extract `AggregateDifference` and `compareAggregateProjection` to `Publication.fs`
   - Wire into staged validation
   - Verify production recomputes aggregate from parsed staged records

3. **Add duplicate-check bijection proof**
   - Prove exact 1:1 mapping between check IDs and evidence IDs
   - Handle duplicate detection without Set/Map multiplicity loss

### Phase 2: Nonvacuous Failure Injection (Required)

4. **Inject cleanup failure**
   - Create failure-injecting file system abstraction
   - Test validation failure plus cleanup failure
   - Test replacement failure plus cleanup failure
   - Test cleanup failure after successful replacement
   - Verify cleanup invoked exactly once
   - Verify initiating failure retained alongside cleanup failure

5. **Inject partial replacement failure**
   - Test failure before first replacement
   - Test failure after one replacement
   - Test failure after two replacements
   - Test failure after three replacements
   - Test restoration of previously present files
   - Test removal of files previously absent
   - Test restoration failure

### Phase 3: Provider Orchestration (Required)

6. **Exercise real provider**
   - Instrument `executeCanonicalChecksWithPerCheckTimestamps`
   - Count `provideWithDependenciesFull` calls
   - Verify each check executes exactly once
   - Verify subject resolution, working-tree reads, scope resolution
   - Verify clock calls, RunCheck calls
   - Verify execution ordering and short-circuiting

### Phase 4: Hygiene and Integration (Required)

7. **Fix whitespace defects**
   - Run `git diff --check` to identify issues
   - Fix trailing whitespace, line ending issues
   - Ensure ordinary forward commit

8. **Add CLI publication integration tests**
   - Verify CLI passes exact records/aggregate/projection
   - Test CLI with various failure scenarios

9. **Run fresh canonical gate**
   - Execute full test suite including all 75 new tests
   - Verify all tests pass
   - Document commit/tree binding

## Acceptance Criteria

| ID | Criterion | Verification |
|----|----------|--------------|
| AC1 | Compatibility comparator in production code | Source inspection |
| AC2 | Aggregate comparator in production code | Source inspection |
| AC3 | Both comparators wired to staged validation | Unit test with mutated staged bytes |
| AC4 | Duplicate-check bijection proven | Test with duplicate IDs |
| AC5 | Cleanup failure injected and verified | Test with cleanup failure |
| AC6 | Partial replacement failure injected | Test at indices 0-3 |
| AC7 | Restoration failure injected | Test with restoration failure |
| AC8 | Provider once-only verified | Test with counter instrumentation |
| AC9 | No whitespace defects | `git diff --check` passes |
| AC10 | CLI integration tested | CLI test execution |
| AC11 | Fresh gate passes | Full test suite execution |

## Subscopes

| Subscope | Status |
|----------|--------|
| production_compatibility_comparison_authority | IN_PROGRESS |
| production_aggregate_comparison_authority | IN_PROGRESS |
| staged_mutation_integration | PENDING |
| exact_bijection | PENDING |
| actual_cleanup_failure_injection | PENDING |
| partial_replacement_injection | PENDING |
| restoration_failure_injection | PENDING |
| provider_once_only_orchestration | PENDING |
| CLI_publication_integration | PENDING |
| committed_range_hygiene | PENDING |
| fresh_gate | PENDING |

## Predecessor Digest

```
File: docs/close-reports/closure-ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01-REAL-RECORD-PIPELINE01-CORRECTION07-CORRECTION04.md
Status: INVALID_CLOSURE_CHECKPOINT
SHA-256: <to be computed>
```

## Implementation Notes

- Production code changes must be in `tools/Circus.Tooling/CanonicalEvidence/Publication.fs` or similar
- Test helpers in test files should be removed once production equivalents exist
- Use dependency injection for failure injection rather than mocking
- Provider instrumentation should use discriminated unions for counters
