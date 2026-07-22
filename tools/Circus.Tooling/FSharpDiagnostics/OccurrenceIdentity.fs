module Circus.Tooling.FSharpDiagnostics.OccurrenceIdentity

open System.Text
open Circus.Tooling.FSharpDiagnostics.Domain
open Circus.Tooling.FSharpDiagnostics.Hashing

// =============================================================================
// Exact fingerprint v1
// =============================================================================
//
// The fingerprint input is serialized through a canonical, fixed-order text
// encoding and then SHA-256 hashed.  The encoding uses explicit null tokens
// and uses one LF between fields.  The resulting SHA-256 digest is rendered
// in lowercase hexadecimal.

let private nullToken = "<null>"

/// Render an optional integer using invariant culture and explicit null.
let private renderOptInt (v: int option) : string =
    match v with
    | Some n -> n.ToString(System.Globalization.CultureInfo.InvariantCulture)
    | None -> nullToken

/// Render an optional string.  Embedded LF would corrupt the canonical
/// encoding so we replace LF with a unit separator (US, 0x1F).  CR becomes
/// the same US marker.  This is purely an encoding step, not a semantic
/// normalization step (the actual message is preserved verbatim in
/// DiagnosticOccurrence.MessageRaw and DiagnosticOccurrence.MessageNormalized).
let private renderOptString (v: string option) : string =
    match v with
    | Some s ->
        let sb = StringBuilder()
        for c in s do
            if c = '\n' || c = '\r' then
                sb.Append '\x1F' |> ignore
            else
                sb.Append c |> ignore
        sb.ToString()
    | None -> nullToken

/// Canonical encoding of the fingerprint input.  Field order is fixed.
let private canonicalEncoding (severity: string)
                              (subcategory: string option)
                              (code: string option)
                              (sourcePath: string option)
                              (projectPath: string option)
                              (startLine: int option)
                              (startColumn: int option)
                              (endLine: int option)
                              (endColumn: int option)
                              (messageNormalized: string)
                              : string =
    let sb = StringBuilder()
    sb.Append "fingerprint_version=" |> ignore
    sb.Append ExactFingerprintVersion |> ignore
    sb.Append '\n' |> ignore
    sb.Append "severity=" |> ignore
    sb.Append severity |> ignore
    sb.Append '\n' |> ignore
    sb.Append "subcategory=" |> ignore
    sb.Append(renderOptString subcategory) |> ignore
    sb.Append '\n' |> ignore
    sb.Append "code=" |> ignore
    sb.Append(renderOptString code) |> ignore
    sb.Append '\n' |> ignore
    sb.Append "source_path=" |> ignore
    sb.Append(renderOptString sourcePath) |> ignore
    sb.Append '\n' |> ignore
    sb.Append "project_path=" |> ignore
    sb.Append(renderOptString projectPath) |> ignore
    sb.Append '\n' |> ignore
    sb.Append "start_line=" |> ignore
    sb.Append(renderOptInt startLine) |> ignore
    sb.Append '\n' |> ignore
    sb.Append "start_column=" |> ignore
    sb.Append(renderOptInt startColumn) |> ignore
    sb.Append '\n' |> ignore
    sb.Append "end_line=" |> ignore
    sb.Append(renderOptInt endLine) |> ignore
    sb.Append '\n' |> ignore
    sb.Append "end_column=" |> ignore
    sb.Append(renderOptInt endColumn) |> ignore
    sb.Append '\n' |> ignore
    sb.Append "message_normalized=" |> ignore
    sb.Append(renderOptString (Some messageNormalized)) |> ignore
    sb.ToString()

/// Compute the exact fingerprint for one occurrence.  The fingerprint input
/// excludes capture_id, event_ordinal, event_timestamp, machine_name,
/// current_working_directory, absolute_repository_root,
/// extractor_execution_time, and build-context IDs.
let fingerprintFor (occ: DiagnosticOccurrence) : string =
    let severity = severityToken occ.Severity
    let encoding =
        canonicalEncoding
            severity
            occ.Subcategory
            occ.Code
            occ.SourcePath
            occ.ProjectPath
            occ.Span.StartLine
            occ.Span.StartColumn
            occ.Span.EndLine
            occ.Span.EndColumn
            occ.MessageNormalized
    sha256OfUtf8 encoding

/// Aggregate occurrence list into exact fingerprints.  Ordering of the input
/// is preserved; occurrences are NOT deduplicated.  Returns the fingerprints
/// sorted by sha256 ascending (ordinal) with occurrence counts.
let aggregateFingerprints (occurrences: DiagnosticOccurrence list) : ExactFingerprint list =
    let dict = System.Collections.Generic.Dictionary<string, ExactFingerprint>()
    for occ in occurrences do
        let fp = fingerprintFor occ
        let severity = severityToken occ.Severity
        match dict.TryGetValue fp with
        | true, existing ->
            dict.[fp] <- { existing with OccurrenceCount = existing.OccurrenceCount + 1 }
        | false, _ ->
            dict.[fp] <-
                { FingerprintVersion = ExactFingerprintVersion
                  Severity = severity
                  Subcategory = occ.Subcategory
                  Code = occ.Code
                  SourcePath = occ.SourcePath
                  ProjectPath = occ.ProjectPath
                  StartLine = occ.Span.StartLine
                  StartColumn = occ.Span.StartColumn
                  EndLine = occ.Span.EndLine
                  EndColumn = occ.Span.EndColumn
                  MessageNormalized = occ.MessageNormalized
                  Sha256 = fp
                  OccurrenceCount = 1 }
    dict.Values
    |> Seq.sortBy (fun fp -> fp.Sha256)
    |> Seq.toList

/// Compute the canonical encoding for one occurrence for test inspection.
/// Not used in production code paths; tests use this to assert canonical
/// encoding changes when fields change.
let debugEncoding (occ: DiagnosticOccurrence) : string =
    let severity = severityToken occ.Severity
    canonicalEncoding
        severity
        occ.Subcategory
        occ.Code
        occ.SourcePath
        occ.ProjectPath
        occ.Span.StartLine
        occ.Span.StartColumn
        occ.Span.EndLine
        occ.Span.EndColumn
        occ.MessageNormalized