# ACT-CIRCUS-FSHARP-DIAGNOSTIC-CORPUS-FOUNDATION01

## Status

**READY — P0**

**Verdict: PARTIAL**

## Parent epic

`EPIC-CIRCUS-FSHARP-DIAGNOSTIC-KNOWLEDGE-AND-HISTORY-SAFETY01`

## Problem statement

The repository's accumulated F# compiler diagnostics had no canonical
authoritative storage.  Existing FSB evidence artefacts lived under
non-authoritative scratch roots (notably `.factory/evidence/fsharp/`) and
lacked a versioned schema, deterministic normalization, and atomic
publication contract.  This ACT establishes the first authoritative,
deterministic foundation for preserving and processing F# compiler
diagnostics.

## Raw-versus-derived authority

The canonical tracked root is:

```text
factory/evidence/fsharp-diagnostics/
```

This ACT recognises two authority classes:

1. **`canonical_corpus`** — the directory above.  All new F# diagnostic
   evidence is written here and the F# diagnostics subsystem treats
   this directory as the only corpus authority.
2. **`non_authoritative_scratch`** — `.factory/`.  Files found here are
   enumerated for visibility but are never read as corpus authority.

No files are migrated from legacy locations in this ACT.  The historical
`.factory/evidence/fsharp/fsb-0025-correction02.yaml` artefact is a
self-report of a prior correction, not a structured capture, and is
therefore out of scope for this foundation.

## Canonical root decision

```text
factory/evidence/fsharp-diagnostics/
├── schemas/
│   ├── artifact-manifest-v1.schema.json
│   ├── capture-manifest-v1.schema.json
│   ├── diagnostic-occurrence-v1.schema.json
│   └── exact-fingerprint-v1.schema.json
├── corpus/
│   ├── raw/                      # raw captures
│   ├── normalized/               # generated outputs
│   └── manifests/                # migration + artefact manifests
└── fixtures/
    └── fsb-0022/                 # reserved for the FSB-0022 fixture
```

Only one root exists.  No second competing root is permitted.

## Artefact classifications

The artefact manifest supports six classification tokens:

| Token                  | Meaning                                                   |
|------------------------|-----------------------------------------------------------|
| `raw`                  | Raw capture evidence (binlog, legacy text, etc.)          |
| `normalized`           | Generated outputs under `corpus/normalized/` or manifests   |
| `derived`              | Schemas and other derived artefacts under `schemas/`        |
| `correction`           | Correction reports (e.g. `.factory/evidence/fsharp/...`)     |
| `source_snapshot`      | Frozen source snapshots used as fixture inputs             |
| `obsolete_retained`    | Artefacts explicitly retained despite being obsolete       |

Every manifest entry carries:

```text
schema_version, canonical_path, original_path, artifact_class,
authority, status, media_type, byte_length, sha256, capture_id,
supersedes, superseded_by, metadata_gaps
```

## Occurrence identity

A diagnostic occurrence represents one emitted warning or error and
**never performs deduplication**.  Two identical emissions from the
same capture are recorded as two distinct occurrences.

Fields:

```fsharp
type DiagnosticOccurrence = {
    SchemaVersion: string
    ExtractorVersion: string
    CaptureId: string
    SourceKind: DiagnosticSourceKind  // binlog | legacy_text
    EventOrdinal: int64
    Severity: DiagnosticSeverity       // warning | error
    Subcategory: string option
    Code: string option
    MessageRaw: string
    MessageNormalized: string
    LocationKind: DiagnosticLocationKind  // source | project | tool
    SourcePath: string option
    ProjectPath: string option
    Span: SourceSpan
    SenderName: string option
    EventTimestamp: string option
    BuildContext: BuildContext option
    LegacySourceLineStart: int option
    LegacySourceLineEnd: int option
}
```

Invariants:

* `capture_id + event_ordinal` uniquely identifies one occurrence.
* Event ordinals are assigned deterministically by the extractor.
* Distinct emissions remain distinct even when all fields match.
* Project-level or tool-level diagnostics may legitimately lack a
  source file.
* Zero or missing coordinates are represented explicitly rather than
  converted into fake line 1 / column 1 values.
* Full messages are retained verbatim in `message_raw`; multiline
  messages remain multiline.
* Error and warning occurrences are never merged.

## Exact-fingerprint identity

Exact fingerprints deduplicate identical normalized diagnostics only.
They do **not** represent causal families.

The fingerprint input contains:

```text
fingerprint_version, severity, subcategory, code, source_path,
project_path, start_line, start_column, end_line, end_column,
message_normalized
```

The fingerprint input **excludes**:

```text
capture_id, event_ordinal, event timestamp, machine name,
current working directory, absolute repository root,
extractor execution time, build-context IDs
```

The payload is serialized through a canonical, fixed-order text
encoding (LF-separated, LF in messages replaced with the ASCII US
separator `0x1F`), then SHA-256 hashed.  The digest is rendered in
lowercase hexadecimal.

A change to any included field changes the fingerprint.  Two
messages sharing the same code, path, and coordinates but differing
in message text have different fingerprints.

## Explicit deferral of causal families

Causal-family classification and automated fix recommendations are
**out of scope** for this ACT.  The exact fingerprint is the only
deduplication identity produced here.  Repair-episode linkage,
embeddings, vector databases, and model evaluation are deferred.

## Binlog replay policy

Binlog replay is implemented via reflection over the
`Microsoft.Build.BinaryLogReplayEventSource` type.  The implementation
fails closed on:

* Missing or unreadable binlog files.
* Format-version mismatch (ForwardCompatibility is set to the most
  fail-closed enum value available).
* Truncated or malformed binlog streams.
* Recoverable read errors during replay.
* Missing `Microsoft.Build` assembly at runtime.

Replay subscribes to `OnError` and `OnWarning` to capture every
diagnostic event in deterministic global order.  The binlog file is
hashed (SHA-256) **before** replay begins so the inventory record is
available even when replay fails.

The implementation never executes commands embedded in binlog content
and never uses rendered console text as the canonical binlog source.

## Legacy adapter limitations

The legacy text adapter (`source_kind = "legacy_text"`) is:

* Explicitly labelled `"legacy_text"` on every emitted occurrence.
* Non-canonical for future captures but authoritative for preserved
  historical captures that lack binlogs.
* Permitted only for committed historical fixtures.
* Required to fail closed when:
  - a diagnostic-looking line cannot be parsed, or
  - an undeclared absolute path appears.

The adapter does **not** reduce messages to their first token, lowercase
text, replace numbers, or remove punctuation.

## Fail-closed conditions

The pipeline fails closed on every one of these conditions:

* `diagnostic_looking_unparsed_lines > 0` in any capture.
* `undeclared_absolute_paths` not empty.
* Binlog replay raises any error or version mismatch.
* Atomic publication fails at any stage.
* Unclassified artefacts present after regeneration.
* Binlog capture list contains any extraction error.

## Deterministic serialization contract

All generated outputs are:

* UTF-8 without BOM.
* LF line endings.
* Exactly one terminal newline for line-oriented files.
* Stable JSON property ordering (manually constructed).
* Null and empty values use distinct representations (`null` vs `""`).
* Ordinal string sorting (`capture_id ASC, event_ordinal ASC` for
  occurrences; `fingerprint ASC` for fingerprints;
  `canonical_path ASC` for artefact manifest).
* No current timestamps in normalized files.
* No random IDs.
* No machine-specific absolute paths.
* No environment-dependent ordering.

## Atomic publication

Generated corpus outputs are produced in a temporary sibling
directory, fully flushed, verified, and only then moved into the
canonical target.  On any failure the previous canonical outputs
remain byte-identical.  Two independent regenerations produce
byte-identical files.

## Fixture provenance

The canonical fixture directory
`factory/evidence/fsharp-diagnostics/fixtures/fsb-0022/` is reserved
for the FSB-0022 fixture that proves the 67/64/3 counts.

**Verdict: PARTIAL.**  The committed FSB-0022 fixture could not be
authored in this ACT because the historical FSB-0022 evidence
(`factory/evidence/fsharp/fsb-0022-correction02.yaml`) records only a
correction summary and does not contain the raw diagnostic bytes
needed to reproduce the 67/64/3 counts.  Per the ACT contract, when
the authoritative bytes do not reproduce the expected values the ACT
is left PARTIAL with the discrepancy documented.  AC-21, AC-22, and
AC-23 are therefore **not satisfied** in this ACT.

The foundation infrastructure is otherwise complete and tested:

* Inventory, hashing, and classification.
* Schema definitions for all four canonical record types.
* Binlog extraction via `Microsoft.Build.BinaryLogReplayEventSource`
  with fail-closed behavior.
* Legacy text adapter with fail-closed conditions.
* Occurrence identity and exact-fingerprint v1.
* Deterministic JSONL/TSV/JSON rendering.
* Atomic publication with byte-identical failure handling.
* CLI integration through `circus-tooling fsharp-diagnostics ...`.
* Make target `gate-fsharp-diagnostics` invoking the production
  verifier.

## Acceptance evidence

All other acceptance criteria (AC-01 through AC-20, AC-24 through AC-38)
are addressed by:

* Unit tests in `tests/Circus.Tooling.Tests/FSharpDiagnostics/`.
* The `gate-fsharp-diagnostics` Make target invoking the production
  verifier through `circus-tooling.dll`.
* The corpus root structure and schema files committed in
  `factory/evidence/fsharp-diagnostics/`.
* The versioned schemas (`artifact-manifest-v1`, `capture-manifest-v1`,
  `diagnostic-occurrence-v1`, `exact-fingerprint-v1`).

Test execution:

```text
Passed: 353
Failed:   0
Errored:  0
```

`make gate-fsharp-diagnostics` returns 0 (PASS):

```text
verdict: PASS
occurrences: 0
unique_fingerprints: 0
duplicates: 0
captures: 0
binlog_failures: 0
undeclared_absolute_paths: 0
diagnostic_looking_unparsed_lines: 0
unclassified_artefacts: 0
canonical_byte_identical_after_failure: true
```

## Implementation identities

Implementation, testing, and documentation are produced through a
single committed change set.  Identities are recorded in the matching
close report.