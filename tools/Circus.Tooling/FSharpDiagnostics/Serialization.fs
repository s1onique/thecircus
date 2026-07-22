module Circus.Tooling.FSharpDiagnostics.Serialization

open System.Globalization
open System.IO
open System.Text
open Circus.Tooling.FSharpDiagnostics.Domain

// =============================================================================
// Deterministic JSON output
// =============================================================================
//
// Rules:
//   * UTF-8 without BOM
//   * LF line endings
//   * Stable JSON property ordering (manually constructed)
//   * Null and empty values use distinct representations:
//       - null   → "null"
//       - ""     → "\"\""
//       - absent → field is omitted
//   * Ordinal string comparison for sort keys
//   * No current timestamps in any generated output
//   * No random IDs

let private NullToken = "<null>"

/// Escape a JSON string literal.  Embedded LF is rendered as "\n" so the
/// output is a single physical line (per ACT §11 "exactly one terminal
/// newline for line-oriented files").  UTF-8 byte order mark (U+FEFF) is
/// also escaped to prevent stray BOMs in concatenation outputs.
let escapeJsonString (s: string) : string =
    let sb = StringBuilder(s.Length + 2)
    sb.Append '"' |> ignore
    for c in s do
        if c = '\\' then sb.Append("\\\\") |> ignore
        elif c = '"' then sb.Append("\\\"") |> ignore
        elif c = '\n' then sb.Append("\\n") |> ignore
        elif c = '\r' then sb.Append("\\r") |> ignore
        elif c = '\t' then sb.Append("\\t") |> ignore
        elif c = '\b' then sb.Append("\\b") |> ignore
        elif c = '\x0c' then sb.Append("\\f") |> ignore
        elif int c < 0x20 then
            sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", int c) |> ignore
        elif c = '\uFEFF' then
            sb.Append("\\ufeff") |> ignore
        else sb.Append c |> ignore
    sb.Append '"' |> ignore
    sb.ToString()

/// Render a string option.  None → "null", Some "" → "\"\"".
let optStr (v: string option) : string =
    match v with
    | None -> "null"
    | Some s -> escapeJsonString s

/// Render an integer using invariant culture.  No null here; optional ints
/// are represented as nullable values via optInt below.
let intStr (v: int) : string =
    v.ToString(CultureInfo.InvariantCulture)

let int64Str (v: int64) : string =
    v.ToString(CultureInfo.InvariantCulture)

let optIntStr (v: int option) : string =
    match v with
    | None -> "null"
    | Some n -> n.ToString(CultureInfo.InvariantCulture)

let optInt64Str (v: int64 option) : string =
    match v with
    | None -> "null"
    | Some n -> n.ToString(CultureInfo.InvariantCulture)

let boolStr (v: bool) : string =
    if v then "true" else "false"

/// Write a string list as a JSON array.
let strListJson (vs: string list) : string =
    let parts = vs |> List.map escapeJsonString
    "[" + String.concat "," parts + "]"

// =============================================================================
// Artifact manifest JSONL rendering
// =============================================================================

let renderArtifactManifestEntry (e: ArtifactManifestEntry) : string =
    let sb = StringBuilder()
    sb.Append "{" |> ignore
    sb.Append "\"schema_version\":" |> ignore
    sb.Append(escapeJsonString e.SchemaVersion) |> ignore
    sb.Append ",\"canonical_path\":" |> ignore
    sb.Append(escapeJsonString e.CanonicalPath) |> ignore
    sb.Append ",\"original_path\":" |> ignore
    sb.Append(escapeJsonString e.OriginalPath) |> ignore
    sb.Append ",\"artifact_class\":" |> ignore
    sb.Append(escapeJsonString e.ArtifactClass) |> ignore
    sb.Append ",\"authority\":" |> ignore
    sb.Append(escapeJsonString e.Authority) |> ignore
    sb.Append ",\"status\":" |> ignore
    sb.Append(escapeJsonString e.Status) |> ignore
    sb.Append ",\"media_type\":" |> ignore
    sb.Append(escapeJsonString e.MediaType) |> ignore
    sb.Append ",\"byte_length\":" |> ignore
    sb.Append(int64Str e.ByteLength) |> ignore
    sb.Append ",\"sha256\":" |> ignore
    sb.Append(escapeJsonString e.Sha256) |> ignore
    sb.Append ",\"capture_id\":" |> ignore
    sb.Append(optStr e.CaptureId) |> ignore
    sb.Append ",\"supersedes\":" |> ignore
    sb.Append(optStr e.Supersedes) |> ignore
    sb.Append ",\"superseded_by\":" |> ignore
    sb.Append(optStr e.SupersededBy) |> ignore
    sb.Append ",\"metadata_gaps\":" |> ignore
    sb.Append(strListJson e.MetadataGaps) |> ignore
    sb.Append "}" |> ignore
    sb.ToString()

// =============================================================================
// Capture manifest JSON rendering
// =============================================================================

let private renderAlias (a: SourceRootAlias) : string =
    "{\"absolute_root\":"
    + escapeJsonString a.AbsoluteRoot
    + ",\"canonical_root\":"
    + escapeJsonString a.CanonicalRoot
    + "}"

let renderCaptureManifest (m: CaptureManifest) : string =
    let sb = StringBuilder()
    sb.Append "{\"schema_version\":" |> ignore
    sb.Append(escapeJsonString m.SchemaVersion) |> ignore
    sb.Append ",\"capture_id\":" |> ignore
    sb.Append(escapeJsonString m.CaptureId) |> ignore
    sb.Append ",\"capture_kind\":" |> ignore
    sb.Append(escapeJsonString m.CaptureKind) |> ignore
    sb.Append ",\"raw_artifacts\":" |> ignore
    sb.Append(m.RawArtifacts |> List.map escapeJsonString |> String.concat "," |> fun s -> "[" + s + "]") |> ignore
    sb.Append ",\"command\":" |> ignore
    sb.Append(optStr m.Command) |> ignore
    sb.Append ",\"working_directory\":" |> ignore
    sb.Append(optStr m.WorkingDirectory) |> ignore
    sb.Append ",\"repository_commit_oid\":" |> ignore
    sb.Append(optStr m.RepositoryCommitOid) |> ignore
    sb.Append ",\"repository_tree_oid\":" |> ignore
    sb.Append(optStr m.RepositoryTreeOid) |> ignore
    sb.Append ",\"working_tree_state\":" |> ignore
    sb.Append(optStr m.WorkingTreeState) |> ignore
    sb.Append ",\"source_root_aliases\":" |> ignore
    sb.Append(m.SourceRootAliases |> List.map renderAlias |> String.concat "," |> fun s -> "[" + s + "]") |> ignore
    sb.Append ",\"dotnet_sdk_version\":" |> ignore
    sb.Append(optStr m.DotnetSdkVersion) |> ignore
    sb.Append ",\"msbuild_version\":" |> ignore
    sb.Append(optStr m.MsbuildVersion) |> ignore
    sb.Append ",\"fsharp_compiler_version\":" |> ignore
    sb.Append(optStr m.FsharpCompilerVersion) |> ignore
    sb.Append ",\"operating_system\":" |> ignore
    sb.Append(optStr m.OperatingSystem) |> ignore
    sb.Append ",\"architecture\":" |> ignore
    sb.Append(optStr m.Architecture) |> ignore
    sb.Append ",\"culture\":" |> ignore
    sb.Append(optStr m.Culture) |> ignore
    sb.Append ",\"started_at\":" |> ignore
    sb.Append(optStr m.StartedAt) |> ignore
    sb.Append ",\"completed_at\":" |> ignore
    sb.Append(optStr m.CompletedAt) |> ignore
    sb.Append ",\"exit_code\":" |> ignore
    sb.Append(optIntStr m.ExitCode) |> ignore
    sb.Append ",\"metadata_gaps\":" |> ignore
    sb.Append(strListJson m.MetadataGaps) |> ignore
    sb.Append "}" |> ignore
    sb.ToString()

// =============================================================================
// Diagnostic occurrence JSON rendering (one JSON object per line)
// =============================================================================

let private renderSpan (s: SourceSpan) : string =
    "{\"start_line\":"
    + optIntStr s.StartLine
    + ",\"start_column\":"
    + optIntStr s.StartColumn
    + ",\"end_line\":"
    + optIntStr s.EndLine
    + ",\"end_column\":"
    + optIntStr s.EndColumn
    + "}"

let private renderBuildContext (b: BuildContext) : string =
    "{\"node_id\":"
    + optIntStr b.NodeId
    + ",\"project_context_id\":"
    + optIntStr b.ProjectContextId
    + ",\"target_id\":"
    + optIntStr b.TargetId
    + ",\"task_id\":"
    + optIntStr b.TaskId
    + ",\"evaluation_id\":"
    + optIntStr b.EvaluationId
    + ",\"submission_id\":"
    + optIntStr b.SubmissionId
    + "}"

let renderOccurrence (o: DiagnosticOccurrence) : string =
    let sb = StringBuilder()
    sb.Append "{\"schema_version\":" |> ignore
    sb.Append(escapeJsonString o.SchemaVersion) |> ignore
    sb.Append ",\"extractor_version\":" |> ignore
    sb.Append(escapeJsonString o.ExtractorVersion) |> ignore
    sb.Append ",\"capture_id\":" |> ignore
    sb.Append(escapeJsonString o.CaptureId) |> ignore
    sb.Append ",\"source_kind\":" |> ignore
    sb.Append(escapeJsonString (sourceKindToken o.SourceKind)) |> ignore
    sb.Append ",\"event_ordinal\":" |> ignore
    sb.Append(int64Str o.EventOrdinal) |> ignore
    sb.Append ",\"severity\":" |> ignore
    sb.Append(escapeJsonString (severityToken o.Severity)) |> ignore
    sb.Append ",\"subcategory\":" |> ignore
    sb.Append(optStr o.Subcategory) |> ignore
    sb.Append ",\"code\":" |> ignore
    sb.Append(optStr o.Code) |> ignore
    sb.Append ",\"message_raw\":" |> ignore
    sb.Append(escapeJsonString o.MessageRaw) |> ignore
    sb.Append ",\"message_normalized\":" |> ignore
    sb.Append(escapeJsonString o.MessageNormalized) |> ignore
    sb.Append ",\"location_kind\":" |> ignore
    sb.Append(escapeJsonString (locationKindToken o.LocationKind)) |> ignore
    sb.Append ",\"source_path\":" |> ignore
    sb.Append(optStr o.SourcePath) |> ignore
    sb.Append ",\"project_path\":" |> ignore
    sb.Append(optStr o.ProjectPath) |> ignore
    sb.Append ",\"span\":" |> ignore
    sb.Append(renderSpan o.Span) |> ignore
    sb.Append ",\"sender_name\":" |> ignore
    sb.Append(optStr o.SenderName) |> ignore
    sb.Append ",\"event_timestamp\":" |> ignore
    sb.Append(optStr o.EventTimestamp) |> ignore
    sb.Append ",\"build_context\":" |> ignore
    match o.BuildContext with
    | Some b -> sb.Append(renderBuildContext b) |> ignore
    | None -> sb.Append "null" |> ignore
    sb.Append ",\"legacy_source_line_start\":" |> ignore
    sb.Append(optIntStr o.LegacySourceLineStart) |> ignore
    sb.Append ",\"legacy_source_line_end\":" |> ignore
    sb.Append(optIntStr o.LegacySourceLineEnd) |> ignore
    sb.Append "}" |> ignore
    sb.ToString()

// =============================================================================
// TSV writers
// =============================================================================

/// Render an exact fingerprint as a TSV line.
/// Columns (in order):
///   sha256    severity    code    source_path    project_path    start_line
///   start_column   end_line end_column  message_normalized  occurrence_count
let renderFingerprintTsv (fp: ExactFingerprint) : string =
    let cols =
        [ fp.Sha256
          fp.Severity
          (match fp.Subcategory with | Some s -> s | None -> "")
          (match fp.Code with | Some s -> s | None -> "")
          (match fp.SourcePath with | Some s -> s | None -> "")
          (match fp.ProjectPath with | Some s -> s | None -> "")
          (match fp.StartLine with | Some n -> n.ToString(CultureInfo.InvariantCulture) | None -> "")
          (match fp.StartColumn with | Some n -> n.ToString(CultureInfo.InvariantCulture) | None -> "")
          (match fp.EndLine with | Some n -> n.ToString(CultureInfo.InvariantCulture) | None -> "")
          (match fp.EndColumn with | Some n -> n.ToString(CultureInfo.InvariantCulture) | None -> "")
          fp.MessageNormalized
          fp.OccurrenceCount.ToString(CultureInfo.InvariantCulture) ]
    cols |> String.concat "\t"

let fingerprintsHeader : string =
    "sha256\tseverity\tsubcategory\tcode\tsource_path\tproject_path\tstart_line\tstart_column\tend_line\tend_column\tmessage_normalized\toccurrence_count"

/// Migration map TSV columns: original_path, canonical_path, sha256, byte_length.
let renderMigrationRow (originalPath: string)
                       (canonicalPath: string)
                       (sha256: string)
                       (byteLength: int64) : string =
    [ originalPath
      canonicalPath
      sha256
      byteLength.ToString(CultureInfo.InvariantCulture) ]
    |> String.concat "\t"

let migrationMapHeader : string =
    "original_path\tcanonical_path\tsha256\tbyte_length"

/// Duplicate occurrences TSV. Columns:
///   fingerprint    capture_id    event_ordinal    message_raw
///   source_path    project_path    span_text
let renderDuplicateRow (fp: string)
                       (captureId: string)
                       (eventOrdinal: int64)
                       (messageRaw: string)
                       (sourcePath: string option)
                       (projectPath: string option)
                       (spanText: string) : string =
    [ fp
      captureId
      eventOrdinal.ToString(CultureInfo.InvariantCulture)
      messageRaw
      (match sourcePath with | Some s -> s | None -> "")
      (match projectPath with | Some s -> s | None -> "")
      spanText ]
    |> String.concat "\t"

let duplicatesHeader : string =
    "fingerprint\tcapture_id\tevent_ordinal\tmessage_raw\tsource_path\tproject_path\tspan_text"

// =============================================================================
// Corpus summary JSON
// =============================================================================

let renderCorpusSummary (s: CorpusSummary) : string =
    let sb = StringBuilder()
    sb.Append "{\"schema_version\":" |> ignore
    sb.Append(escapeJsonString s.SchemaVersion) |> ignore
    sb.Append ",\"extractor_version\":" |> ignore
    sb.Append(escapeJsonString s.ExtractorVersion) |> ignore
    sb.Append ",\"artifacts_total\":" |> ignore
    sb.Append(intStr s.ArtifactsTotal) |> ignore
    sb.Append ",\"raw_artifacts\":" |> ignore
    sb.Append(intStr s.RawArtifacts) |> ignore
    sb.Append ",\"normalized_artifacts\":" |> ignore
    sb.Append(intStr s.NormalizedArtifacts) |> ignore
    sb.Append ",\"derived_artifacts\":" |> ignore
    sb.Append(intStr s.DerivedArtifacts) |> ignore
    sb.Append ",\"correction_artifacts\":" |> ignore
    sb.Append(intStr s.CorrectionArtifacts) |> ignore
    sb.Append ",\"source_snapshot_artifacts\":" |> ignore
    sb.Append(intStr s.SourceSnapshotArtifacts) |> ignore
    sb.Append ",\"obsolete_retained_artifacts\":" |> ignore
    sb.Append(intStr s.ObsoleteRetainedArtifacts) |> ignore
    sb.Append ",\"unclassified_artifacts\":" |> ignore
    sb.Append(intStr s.UnclassifiedArtifacts) |> ignore
    sb.Append ",\"captures_total\":" |> ignore
    sb.Append(intStr s.CapturesTotal) |> ignore
    sb.Append ",\"binlog_captures\":" |> ignore
    sb.Append(intStr s.BinlogCaptures) |> ignore
    sb.Append ",\"legacy_text_captures\":" |> ignore
    sb.Append(intStr s.LegacyTextCaptures) |> ignore
    sb.Append ",\"mixed_captures\":" |> ignore
    sb.Append(intStr s.MixedCaptures) |> ignore
    sb.Append ",\"occurrence_count\":" |> ignore
    sb.Append(intStr s.OccurrenceCount) |> ignore
    sb.Append ",\"unique_exact_fingerprint_count\":" |> ignore
    sb.Append(intStr s.UniqueExactFingerprintCount) |> ignore
    sb.Append ",\"duplicate_occurrence_count\":" |> ignore
    sb.Append(intStr s.DuplicateOccurrenceCount) |> ignore
    sb.Append ",\"diagnostic_looking_unparsed_lines\":" |> ignore
    sb.Append(intStr s.DiagnosticLookingUnparsedLines) |> ignore
    sb.Append ",\"metadata_gaps\":" |> ignore
    sb.Append(strListJson s.MetadataGaps) |> ignore
    sb.Append "}" |> ignore
    sb.ToString()

// =============================================================================
// File writers (UTF-8 without BOM, LF endings, exactly one terminal newline)
// =============================================================================

let private utf8NoBom : Encoding =
    new UTF8Encoding(false)

/// Write `text` (which already uses LF line endings) to `path` with exactly
/// one terminal LF.  Existing file is overwritten; intermediate directories
/// are created.
let writeLineOriented (path: string) (text: string) : unit =
    let dir = Path.GetDirectoryName path
    if not (System.String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
        Directory.CreateDirectory dir |> ignore
    let body =
        if text.EndsWith "\n" then text
        else text + "\n"
    File.WriteAllText(path, body, utf8NoBom)