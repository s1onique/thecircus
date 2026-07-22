module Circus.Tooling.FSharpDiagnostics.Domain

// =============================================================================
// Artefact classification
// =============================================================================

/// Primary classification of a discovered FSB artefact. Each artefact receives
/// exactly one class. The serialized lowercase tokens are stable wire values.
type ArtifactClass =
    | Raw
    | Normalized
    | Derived
    | Correction
    | SourceSnapshot
    | ObsoleteRetained

/// Lowercase, stable wire token for each artifact class.
let artifactClassToken (cls: ArtifactClass) : string =
    match cls with
    | Raw -> "raw"
    | Normalized -> "normalized"
    | Derived -> "derived"
    | Correction -> "correction"
    | SourceSnapshot -> "source_snapshot"
    | ObsoleteRetained -> "obsolete_retained"

/// Parse a stable wire token back into an ArtifactClass.
/// Returns None when the token is unrecognised so callers can fail closed.
let tryParseArtifactClass (token: string) : ArtifactClass option =
    match token with
    | "raw" -> Some Raw
    | "normalized" -> Some Normalized
    | "derived" -> Some Derived
    | "correction" -> Some Correction
    | "source_snapshot" -> Some SourceSnapshot
    | "obsolete_retained" -> Some ObsoleteRetained
    | _ -> None

/// Authority for an artefact. The canonical corpus root is the only authority
/// for F# diagnostic evidence in this ACT.
type ArtifactAuthority =
    /// Tracked canonical root.
    | CanonicalCorpus
    /// Authoritative legacy location (will be migrated into the canonical
    /// root by the inventory command).
    | LegacyAuthoritative
    /// Non-authoritative scratch that must never be read as corpus authority.
    | NonAuthoritativeScratch
    /// Unclassified or unknown provenance.
    | Unclassified

let authorityToken (a: ArtifactAuthority) : string =
    match a with
    | CanonicalCorpus -> "canonical_corpus"
    | LegacyAuthoritative -> "legacy_authoritative"
    | NonAuthoritativeScratch -> "non_authoritative_scratch"
    | Unclassified -> "unclassified"

/// Status for an artefact in the manifest.
type ArtifactStatus =
    | Present
    | Migrated
    | Obsolete

let statusToken (s: ArtifactStatus) : string =
    match s with
    | Present -> "present"
    | Migrated -> "migrated"
    | Obsolete -> "obsolete"

// =============================================================================
// Artefact manifest entry
// =============================================================================

/// One entry in the canonical artifact manifest (artifacts-v1.jsonl).
type ArtifactManifestEntry = {
    SchemaVersion: string
    CanonicalPath: string
    OriginalPath: string
    ArtifactClass: string
    Authority: string
    Status: string
    MediaType: string
    ByteLength: int64
    Sha256: string
    CaptureId: string option
    Supersedes: string option
    SupersededBy: string option
    MetadataGaps: string list
}

/// Top-level artifact manifest header value (used for the jsonl envelope).
let ArtifactManifestSchemaVersion = "artifact-manifest-v1"

// =============================================================================
// Capture manifest
// =============================================================================

/// Valid capture kind values.
type CaptureKind =
    | Binlog
    | LegacyText
    | Mixed

let captureKindToken (k: CaptureKind) : string =
    match k with
    | Binlog -> "binlog"
    | LegacyText -> "legacy_text"
    | Mixed -> "mixed"

let tryParseCaptureKind (token: string) : CaptureKind option =
    match token with
    | "binlog" -> Some Binlog
    | "legacy_text" -> Some LegacyText
    | "mixed" -> Some Mixed
    | _ -> None

/// Source-root alias declaration: an absolute path under which the historical
/// capture lived is mapped to the canonical repository-relative path.
type SourceRootAlias = {
    AbsoluteRoot: string
    CanonicalRoot: string
}

type CaptureManifest = {
    SchemaVersion: string
    CaptureId: string
    CaptureKind: string
    RawArtifacts: string list
    Command: string option
    WorkingDirectory: string option
    RepositoryCommitOid: string option
    RepositoryTreeOid: string option
    WorkingTreeState: string option
    SourceRootAliases: SourceRootAlias list
    DotnetSdkVersion: string option
    MsbuildVersion: string option
    FsharpCompilerVersion: string option
    OperatingSystem: string option
    Architecture: string option
    Culture: string option
    StartedAt: string option
    CompletedAt: string option
    ExitCode: int option
    MetadataGaps: string list
}

let CaptureManifestSchemaVersion = "capture-manifest-v1"

// =============================================================================
// Diagnostic occurrence
// =============================================================================

type DiagnosticSeverity =
    | Warning
    | Error

let severityToken (s: DiagnosticSeverity) : string =
    match s with
    | Warning -> "warning"
    | Error -> "error"

let tryParseSeverity (token: string) : DiagnosticSeverity option =
    match token with
    | "warning" -> Some Warning
    | "error" -> Some Error
    | _ -> None

type DiagnosticSourceKind =
    | Binlog
    | LegacyText

let sourceKindToken (s: DiagnosticSourceKind) : string =
    match s with
    | Binlog -> "binlog"
    | LegacyText -> "legacy_text"

let tryParseSourceKind (token: string) : DiagnosticSourceKind option =
    match token with
    | "binlog" -> Some Binlog
    | "legacy_text" -> Some LegacyText
    | _ -> None

type DiagnosticLocationKind =
    | Source
    | Project
    | Tool

let locationKindToken (k: DiagnosticLocationKind) : string =
    match k with
    | Source -> "source"
    | Project -> "project"
    | Tool -> "tool"

let tryParseLocationKind (token: string) : DiagnosticLocationKind option =
    match token with
    | "source" -> Some Source
    | "project" -> Some Project
    | "tool" -> Some Tool
    | _ -> None

type SourceSpan = {
    StartLine: int option
    StartColumn: int option
    EndLine: int option
    EndColumn: int option
}

let emptySpan : SourceSpan =
    { StartLine = None; StartColumn = None; EndLine = None; EndColumn = None }

type BuildContext = {
    NodeId: int option
    ProjectContextId: int option
    TargetId: int option
    TaskId: int option
    EvaluationId: int option
    SubmissionId: int option
}

let emptyBuildContext : BuildContext =
    { NodeId = None
      ProjectContextId = None
      TargetId = None
      TaskId = None
      EvaluationId = None
      SubmissionId = None }

type DiagnosticOccurrence = {
    SchemaVersion: string
    ExtractorVersion: string
    CaptureId: string
    SourceKind: DiagnosticSourceKind
    EventOrdinal: int64
    Severity: DiagnosticSeverity
    Subcategory: string option
    Code: string option
    MessageRaw: string
    MessageNormalized: string
    LocationKind: DiagnosticLocationKind
    SourcePath: string option
    ProjectPath: string option
    Span: SourceSpan
    SenderName: string option
    EventTimestamp: string option
    BuildContext: BuildContext option
    LegacySourceLineStart: int option
    LegacySourceLineEnd: int option
}

let OccurrenceSchemaVersion = "diagnostic-occurrence-v1"

// =============================================================================
// Exact fingerprint v1
// =============================================================================

type ExactFingerprint = {
    FingerprintVersion: string
    Severity: string
    Subcategory: string option
    Code: string option
    SourcePath: string option
    ProjectPath: string option
    StartLine: int option
    StartColumn: int option
    EndLine: int option
    EndColumn: int option
    MessageNormalized: string
    Sha256: string
    OccurrenceCount: int
}

let ExactFingerprintVersion = "exact-fingerprint-v1"

// =============================================================================
// Corpus summary
// =============================================================================

type CorpusSummary = {
    SchemaVersion: string
    ExtractorVersion: string
    ArtifactsTotal: int
    RawArtifacts: int
    NormalizedArtifacts: int
    DerivedArtifacts: int
    CorrectionArtifacts: int
    SourceSnapshotArtifacts: int
    ObsoleteRetainedArtifacts: int
    UnclassifiedArtifacts: int
    CapturesTotal: int
    BinlogCaptures: int
    LegacyTextCaptures: int
    MixedCaptures: int
    OccurrenceCount: int
    UniqueExactFingerprintCount: int
    DuplicateOccurrenceCount: int
    DiagnosticLookingUnparsedLines: int
    MetadataGaps: string list
}

let CorpusSummarySchemaVersion = "corpus-summary-v1"