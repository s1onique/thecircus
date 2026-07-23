# ACT-CIRCUS-FSHARP-DIAGNOSTIC-REPAIR-EPISODE-LINKING01-CORRECTION01

## Status

**READY — P0**

**Verdict: PARTIAL_CHECKPOINT (correcting prior ACT)**

## Parent epic

`EPIC-CIRCUS-FSHARP-DIAGNOSTIC-KNOWLEDGE-AND-HISTORY-SAFETY01`

## Predecessor

`ACT-CIRCUS-FSHARP-DIAGNOSTIC-REPAIR-EPISODE-LINKING01` (closed_partial-v1)

## Problem statement

The review of the predecessor ACT identified ten P0 defects plus one
P1 defect.  This correction ACT must:

1. Connect the production engine to the existing foundation occurrence
   stream so transitions are actually computed for every declared
   episode.
2. Close the capture-binding path: missing capture commit OIDs must
   not silently match, capture tree OIDs must be checked, expected
   tree assertions must be enforced, extraction completeness must be
   verified, raw artefact hashes must be compared with declared hashes,
   and undeclared absolute paths and binlog replay failures must fail
   the capture binding.
3. Implement a real verification evidence runtime: declarations carry
   evidence IDs and the loader reads declared evidence files from
   disk, verifies their deterministic identity, and binds them to the
   after commit and tree.
4. Replace the "regenerate-then-check" verifier with a read-only
   verifier that independently recomputes episode IDs, change-set
   IDs, transition counts, manifest membership, and canonical hashes.
5. Make regeneration fail closed on invalid or incomplete
   declarations and on missing captures, missing Git objects, and
   duplicate keys.
6. Honour the documented canonical layout (`corpus/episodes/normalized/`)
   by emitting the five new outputs there and migrating the foundation
   manifest to inventory them.
7. Add `--no-abbrev` to the Git `diff-tree` invocation and detect
   merge ambiguity to fail closed.
8. Render span coordinates as JSON numbers (not strings), preserving
   schema conformance.
9. Preserve `GitOverflowFailure`, `GitLaunchFailure`, and
   `GitIoFailure` distinctions at the public seam.
10. Add a controlled end-to-end fixture with two real commits, two
    real capture manifests and occurrence streams, a real declaration,
    and assertions that nonzero transitions emerge.

## Doctrine

This correction ACT preserves the observation-vs-causation doctrine.
It does not introduce causal-family assignment, fuzzy authority, or
LLM-derived knowledge.  It strictly connects the existing
scaffolding to the foundation's already-canonical occurrence stream.

## Scope of changes

* `tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/Episodes.fs`
  — full capture binding, including raw hash verification, expected
  tree assertion enforcement, extraction completeness, binlog replay
  status, legacy-text unparsed-line and absolute-path accounting.
* `tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/Engine.fs`
  — load real per-capture occurrence lists, call `buildTransitions`
  per episode, accumulate episode-specific transition counts and
  assessments, load and verify evidence files, and fail closed when
  any required input is missing or invalid.
* `tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/Verifier.fs`
  (new) — read-only verifier: recomputes episode IDs, change-set
  IDs, transition counts, manifest membership, hashes, and
  ordering.  Never regenerates.
* `tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/Paths.fs`
  — wire the episode subdirectory outputs to
  `corpus/episodes/normalized/` and migrate the foundation manifest.
* `tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/Git.fs`
  — add `--no-abbrev`, distinguish merge ambiguity, classify
  exceptions accurately.
* `tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/Serialization.fs`
  — render span integers as numbers, not strings.
* `factory/evidence/fsharp-diagnostics/corpus/episodes/` — populated
  with the controlled end-to-end fixture and matching declarations.
* `tests/Circus.Tooling.Tests/FSharpDiagnostics/RepairEpisodes/` —
  tests for end-to-end nonzero transitions, read-only verifier
  mutation detection, bounded-process classification, span number
  rendering, merge ambiguity, expected-tree assertion enforcement,
  raw hash verification, and invalid-input fail-closed.

## Scope isolation

The correction ACT does not modify:

* `tools/Circus.Tooling/NoForcePush/`
* `src/Circus.Persistence.Postgres/`
* `tests/Circus.Persistence.Postgres.Tests/`
* Any foundation capture-extraction or normalization code.

## Acceptance criteria (summary)

| ID    | Criterion                                                              |
| ----- | ---------------------------------------------------------------------- |
| CC-01 | Per-episode transitions are produced from real occurrence streams       |
| CC-02 | Capture binding checks commit, tree, raw hashes, extraction completeness, binlog replay, absolute paths, and expected-tree assertions |
| CC-03 | Regeneration fails closed on any invalid or incomplete declaration      |
| CC-04 | Verifier is read-only, never regenerates, and detects mutations         |
| CC-05 | Episode outputs are emitted under `corpus/episodes/normalized/`         |
| CC-06 | Foundation manifest inventories the five new episode artefacts            |
| CC-07 | `diff-tree` invocation includes `--no-abbrev`                           |
| CC-08 | Merge ambiguity fails closed with parent-selection evidence rule       |
| CC-09 | Span coordinates render as JSON numbers                                 |
| CC-10 | `GitOverflowFailure`, `GitLaunchFailure`, `GitIoFailure` distinguished  |
| CC-11 | End-to-end controlled fixture produces nonzero transitions              |
| CC-12 | All P0 findings from the review are addressed                          |
