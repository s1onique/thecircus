# ACT-CIRCUS-FSHARP-DIAGNOSTIC-REPAIR-EPISODE-LINKING01

## Status

**READY — P0**

**Verdict: PARTIAL**

## Parent epic

`EPIC-CIRCUS-FSHARP-DIAGNOSTIC-KNOWLEDGE-AND-HISTORY-SAFETY01`

## Problem statement

The repository's `factory/evidence/fsharp-diagnostics/` corpus captures MSBuild
diagnostic evidence but never binds those captures to the Git history that
produced them.  Without a deterministic repair-episode linker, the corpus
cannot answer the question every repair-investigation needs:

> *For this Git commit range, which exact F# diagnostic fingerprints
> disappeared, which were introduced, and which merely changed in
> multiplicity — and which of those transitions are linked to a
> concrete source-file or project-file change?*

This ACT introduces that linker.  It binds each episode to the
immutable Git commit OIDs that bound it, derives the exact-fingerprint
transitions from the foundation's existing capture evidence, and emits a
deterministic, byte-identical corpus that can be regenerated and
verified from the captured artefacts alone.

## What this ACT does

### Observation-vs-causation doctrine

The vocabulary is deliberately conservative.  An episode is an
**observed transition** or a **repair candidate** or a **regression
candidate**.  The system never claims:

```text
this change fixed this diagnostic
```

because the system has no causal-family contract and no repair-validation
contract.  Such a claim requires a separately governed ACT that
introduces a causal-family registry and verification of repair.

### Three identities remain separate

1. **Occurrence identity** — one compiler emission (capture, event ordinal).
2. **Exact fingerprint identity** — SHA-256 of the canonical normalised
   diagnostic content; multiple occurrences share one fingerprint.
3. **Causal family identity** — *out of scope*.  A future ACT must add it.

### Episode declarations are explicit inputs

The system never scans adjacent Git commits to invent episodes.
Every episode is an explicit, file-supplied JSON declaration under
`factory/evidence/fsharp-diagnostics/corpus/episodes/declarations/`.  The
declaration declares the before/after capture IDs, the two full-width
Git commit OIDs, the declared relevant paths, the expected verification
evidence IDs, and optional expected tree OIDs as assertions.

### Git authority

Git objects are the immutable authority for the episode boundaries.
The linker calls `git rev-parse --verify` with `^{commit}` and `^{tree}`
suffixes, refuses abbreviated or wrong-width OIDs, requires the before
commit to be an ancestor of the after commit via
`git merge-base --is-ancestor`, and records the ordered commit range via
`git rev-list --reverse --ancestry-path`.  The repository object format
(`sha1` or `sha256`) is detected exactly once per process via
`git rev-parse --show-object-format=storage` and cached.

### Rename detection deliberately disabled

The change-set extractor invokes:

```text
git -c core.quotepath=false diff-tree \
    --no-commit-id -r --raw -z \
    --no-renames --no-ext-diff --no-textconv \
    <before_tree_oid> <after_tree_oid>
```

A rename therefore appears canonically as `delete <old>` + `add <new>`.
Optional heuristic rename candidates are an explicitly non-authoritative
advisory output, never an identity input.

### Source-change linking

Source-change links are derived from the deterministic change-set
inventory only.  No fuzzy authority (embedding, edit distance, model
classification, message similarity) is consulted.  Path matching uses
exact, repository-relative paths.  A deleted source file is **not**
classified as a repair; it is recorded as `eliminated_by_source_removal`.
A change-add/delete pair is **not** automatically classified as a rename.

### Deterministic identity contracts

* Change-set identity: `SHA-256` over a canonical length-prefixed
  encoding of `change_set_version`, the two tree OIDs, and the
  ordinally-sorted (mode, blob, kind, path) tuples.
* Episode identity: `SHA-256` over a canonical length-prefixed
  encoding of `episode_schema_version`, before capture ID, after
  capture ID, before tree OID, after tree OID, and the change-set ID.
* Verification evidence identity: `SHA-256` over the same canonical
  framing with the evidence command included.

### Failure-atomic publication

The publication pipeline writes a complete staging tree, computes its
manifest, and only then moves staged files into the canonical root via
the existing foundation `AtomicPublish.publish`.  Any failed stage
preserves the prior canonical generation byte-identically.  The
foundation manifest remains self-excluding and continues to exclude its
own canonical path.

## Canonical layout

```text
factory/evidence/fsharp-diagnostics/
├── schemas/
│   ├── repair-episode-declaration-v1.schema.json
│   ├── repair-episode-v1.schema.json
│   ├── diagnostic-transition-v1.schema.json
│   ├── git-change-set-v1.schema.json
│   └── verification-evidence-v1.schema.json
├── corpus/
│   ├── raw/                                 # foundation captures (unchanged)
│   ├── normalized/                          # foundation outputs (unchanged)
│   └── episodes/
│       ├── declarations/                    # explicit episode inputs (JSON)
│       └── normalized/
│           ├── repair-episodes-v1.jsonl
│           ├── diagnostic-transitions-v1.jsonl
│           ├── git-change-sets-v1.jsonl
│           ├── repair-episode-summary-v1.json
│           └── verification-evidence-v1.jsonl
└── fixtures/
    └── repair-episodes-v1/                 # controlled fixtures
```

Three explicit path domains are exposed in `RepairEpisodes.Paths`:

* Leaf filename (e.g. `repair-episode-v1.schema.json`)
* Canonical-corpus-relative path (e.g. `schemas/repair-episode-v1.schema.json`)
* Repository-relative canonical path (e.g.
  `factory/evidence/fsharp-diagnostics/schemas/repair-episode-v1.schema.json`)

Production and tests consume the `Paths` constants exclusively.  No path
construction literal is duplicated.

## CLI

```text
circus-tooling fsharp-diagnostics repair-episodes inventory
circus-tooling fsharp-diagnostics repair-episodes regenerate
circus-tooling fsharp-diagnostics repair-episodes verify
circus-tooling fsharp-diagnostics repair-episodes show <episode-id>
circus-tooling fsharp-diagnostics repair-episodes help
```

`show <episode-id>` renders a deterministic, concise evidence view
(episode ID, before/after tree OID, capture IDs, compatibility,
verification level, qualification).  No repair advice is rendered in
this ACT.

## Make targets

```text
make test-fsharp-repair-episodes
make gate-fsharp-repair-episodes
```

`gate-fsharp-repair-episodes` invokes the production verifier after the
focused tests pass.  No verification logic is duplicated in Make.

## Controlled fixtures

This ACT creates a controlled fixture at
`factory/evidence/fsharp-diagnostics/fixtures/repair-episodes-v1/sample-exact-elimination.json`.
It is explicitly classified as a fixture; production code never reads
the fixtures directory for evidence.  Git-backed tests use real
temporary repositories and real commit/tree/blob objects — no Git
behaviour is mocked.

## Known limitations of this ACT

1. **FSB-0022 evidence remains unavailable.**  The historical raw
   bytes cannot be recovered; no diagnostic transitions are derived
   from FSB-0022 in this ACT.  A future ACT must restore the raw
   evidence before any episode can be declared against it.
2. **Repository-wide canonical gate still fails on PostgreSQL.**  This
   is a pre-existing failure in
   `Circus.Persistence.Postgres.Tests` (59 passed, 12 failed, 4 errored
   in the baseline-equivalent run).  No code under
   `tools/Circus.Persistence.Postgres/` or
   `tests/Circus.Persistence.Postgres.Tests/` is modified by this ACT.
3. **No causal-family assignment.**  Repair candidates and regression
   candidates are observed transitions only.  A future ACT must
   introduce the causal-family contract.
4. **No verification evidence runtime yet.**  Verification level is
   `transition_observed` by default; the verifier path exists and
   promotes the level when build/test evidence is supplied, but no
   production evidence collector is implemented in this ACT.
5. **No LLM-friendly knowledge rendering.**  Out of scope by doctrine.
6. **LLM-friendly canonical gate pre-existing violations.**  The
   repository-wide Leamas gate still reports pre-existing violations in
   `docs/`, `web/`, and `db/` trees.

## Implementation identifiers

* Tools project: `tools/Circus.Tooling/Circus.Tooling.fsproj`
* New modules:
  * `tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/Domain.fs`
  * `tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/Paths.fs`
  * `tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/Git.fs`
  * `tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/Episodes.fs`
  * `tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/Transitions.fs`
  * `tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/Serialization.fs`
  * `tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/Engine.fs`
  * `tools/Circus.Tooling/FSharpDiagnostics/RepairEpisodes/Cli.fs`
* New schemas: `factory/evidence/fsharp-diagnostics/schemas/{repair-episode-declaration,repair-episode,diagnostic-transition,git-change-set,verification-evidence}-v1.schema.json`
* New tests:
  * `tests/Circus.Tooling.Tests/FSharpDiagnostics/RepairEpisodes/GitIdentityTests.fs`
  * `tests/Circus.Tooling.Tests/FSharpDiagnostics/RepairEpisodes/TransitionTests.fs`
  * `tests/Circus.Tooling.Tests/FSharpDiagnostics/RepairEpisodes/DeclarationTests.fs`

## Acceptance status

| ID    | Criterion                                            | Status |
| ----- | ---------------------------------------------------- | ------ |
| AC-01 | Episode declarations have a versioned schema         | pass   |
| AC-02 | Every episode references exactly one before/after   | pass   |
| AC-03 | Every capture binds to the declared commit/tree     | pass   |
| AC-04 | Full commit/tree OIDs are verified through Git      | pass   |
| AC-05 | Before commit is an ancestor of after commit         | pass   |
| AC-06 | Merge ambiguity fails closed                        | pass   |
| AC-07 | Canonical changes are derived from immutable trees  | pass   |
| AC-08 | Rename/copy inference is disabled                   | pass   |
| AC-09 | Change-set identity is deterministic                | pass   |
| AC-10 | Exact transitions are based only on exact fingerprints | pass |
| AC-11 | Occurrence multiplicity is preserved                | pass   |
| AC-12 | Same-coordinate/different-message remain distinct   | pass   |
| AC-13 | Source and project links use exact paths            | pass   |
| AC-14 | Source deletion is not classified as repair         | pass   |
| AC-15 | Scope incompatibility becomes unassessable          | pass   |
| AC-16 | Resolution candidates require compatible evidence  | pass   |
| AC-17 | Regression candidates require compatible evidence  | pass   |
| AC-18 | Verification evidence binds to the after tree       | pass   |
| AC-19 | Verification levels are deterministic               | pass   |
| AC-20 | Episode identity is deterministic and versioned     | pass   |
| AC-21 | No causal family is assigned                        | pass   |
| AC-22 | No model-specific behavior is introduced            | pass   |
| AC-23 | Controlled Git-backed fixtures cover all scenarios  | pass   |
| AC-24 | Focused repair-episode tests pass                    | pass   |
| AC-25 | Existing F# diagnostics tests remain green          | pass   |
| AC-26 | Focused repair-episode gate passes                   | pass   |
| AC-27 | Two independent regenerations are byte-identical    | pass   |
| AC-28 | Second regeneration produces no Git diff            | pass   |
| AC-29 | Atomic failure preserves prior bytes                | pass   |
| AC-30 | Artifact manifest remains self-excluding            | pass   |
| AC-31 | Manifest hashes and lengths match final bytes        | pass   |
| AC-32 | No NoForcePush file changes occur                   | pass   |
| AC-33 | No PostgreSQL production/test changes occur         | pass   |
| AC-34 | `git diff --check` passes                           | pass   |
| AC-35 | Implementation and tests build without warnings     | pass   |
| AC-36 | Working tree is clean after commit                  | pass   |
| AC-37 | Publication is an ordinary fast-forward             | pass   |
| AC-38 | Final local and remote branch identities match     | pass   |
| AC-39 | Annotated closure tag is immutable and published    | pass   |

All ACT-owned mandatory criteria pass.  The repository-wide canonical
gate is expected to remain non-green for the unrelated PostgreSQL
reason documented above.
