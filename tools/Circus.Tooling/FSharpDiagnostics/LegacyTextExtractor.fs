module Circus.Tooling.FSharpDiagnostics.LegacyTextExtractor

open System.Text.RegularExpressions
open Circus.Tooling.FSharpDiagnostics.Domain
open Circus.Tooling.FSharpDiagnostics.Normalization
open Circus.Tooling.FSharpDiagnostics.Paths

// =============================================================================
// Legacy text diagnostic parser
// =============================================================================
//
// This adapter parses the exact historical text formats represented in
// committed fixtures.  It is explicitly labelled "legacy_text" for any
// diagnostic it produces (the wire token "legacy_text" is recorded on every
// diagnostic occurrence it emits).
//
// The parser preserves complete diagnostic messages, including multiline
// continuation lines.  It never deduplicates, never truncates messages,
// never normalises arbitrary numbers, quoted symbols, generic arguments,
// type names, or identifiers, and never silently ignores a
// diagnostic-looking line.

/// Result of parsing a legacy text log.
type LegacyParseResult = {
    InputLines: int
    ParsedDiagnostics: int
    ContinuationLines: int
    IgnoredNonDiagnosticLines: int
    DiagnosticLookingUnparsedLines: int
    Occurrences: DiagnosticOccurrence list
    CaptureId: string
    UnparsedDiagnosticLikeSamples: string list
    UndeclaredAbsolutePaths: string list
}

/// One emitted occurrence plus the underlying matched text for diagnostics.
type private ParsedDiagnostic = {
    LineStart: int
    LineEnd: int
    Occurrence: DiagnosticOccurrence
}

/// Common regex shapes observed in historical F# MSBuild text logs.
///
/// MSBuild output uses one of two common shapes for warnings/errors:
///
///     <path>(<line>,<col>): <severity> <code>: <message>
///     <path>(<line>,<col>-<lineEnd>,<colEnd>): <severity> <code>: <message>
///
/// Some logs prefix with "MSBUILD : " or omit the column entirely.
///
/// We use one regex per severity and accept both shapes.  We anchor the path
/// with a permissive but constrained character class so we don't accidentally
/// eat non-diagnostic text.
let private warningRegex1Col : Regex =
    Regex(@"^(?<path>[^(]+?)\((?<line>\d+),(?<col>\d+)\):\s+(?<severity>warning)\s+(?<code>[A-Z]+[0-9]+)\s*:\s*(?<msg>.*)$",
          RegexOptions.Compiled)

let private warningRegex2Col : Regex =
    Regex(@"^(?<path>[^(]+?)\((?<line>\d+),(?<col>\d+)-(?<lineEnd>\d+),(?<colEnd>\d+)\):\s+(?<severity>warning)\s+(?<code>[A-Z]+[0-9]+)\s*:\s*(?<msg>.*)$",
          RegexOptions.Compiled)

let private errorRegex1Col : Regex =
    Regex(@"^(?<path>[^(]+?)\((?<line>\d+),(?<col>\d+)\):\s+(?<severity>error)\s+(?<code>[A-Z]+[0-9]+)\s*:\s*(?<msg>.*)$",
          RegexOptions.Compiled)

let private errorRegex2Col : Regex =
    Regex(@"^(?<path>[^(]+?)\((?<line>\d+),(?<col>\d+)-(?<lineEnd>\d+),(?<colEnd>\d+)\):\s+(?<severity>error)\s+(?<code>[A-Z]+[0-9]+)\s*:\s*(?<msg>.*)$",
          RegexOptions.Compiled)

/// Generic diagnostic-looking line regex.  Used to detect malformed lines
/// that look like diagnostics but did not match the structured regex.
let private genericDiagnosticLikeRegex : Regex =
    Regex(@"^(?<path>[^(]+?)\((?<line>\d+),(?<col>\d+)\):\s+(warning|error)\s",
          RegexOptions.Compiled)

/// Continuation line: a non-blank line whose first column has only whitespace
/// and then text.  Continuation lines belong to the previous diagnostic.
let private continuationRegex : Regex =
    Regex(@"^\s+\S",
          RegexOptions.Compiled)

/// Patterns that identify ordinary non-diagnostic build output, e.g.
/// "Build succeeded." or "  X Warning(s)" or "MSBUILD : ..." summary lines.
let private ordinaryNonDiagnosticRegexes : Regex list =
    [
      Regex(@"^\s*Build (succeeded|FAILED)\.\s*$", RegexOptions.Compiled)
      Regex(@"^\s*\d+\s+Warning\(s\)\s*$", RegexOptions.Compiled)
      Regex(@"^\s*\d+\s+Error\(s\)\s*$", RegexOptions.Compiled)
      Regex(@"^\s*Time Elapsed\s+\d+", RegexOptions.Compiled)
      Regex(@"^Microsoft \(R\) Visual C# Compiler", RegexOptions.Compiled)
      Regex(@"^MSBUILD\s*:", RegexOptions.Compiled)
    ]

let private isOrdinaryNonDiagnostic (line: string) : bool =
    ordinaryNonDiagnosticRegexes |> List.exists (fun r -> r.IsMatch line)

/// Try to parse one line as the start of a diagnostic. Returns Some
/// ParsedDiagnostic with a single line if matched, None otherwise.
let private tryMatchDiagnosticLine
    (lineNo: int)
    (line: string)
    (captureId: string)
    (aliases: SourceRootAlias list)
    (extractorVersion: string)
    : ParsedDiagnostic option =
    let m1 =
        if warningRegex1Col.IsMatch line then Some(warningRegex1Col.Match line)
        elif warningRegex2Col.IsMatch line then Some(warningRegex2Col.Match line)
        elif errorRegex1Col.IsMatch line then Some(errorRegex1Col.Match line)
        elif errorRegex2Col.IsMatch line then Some(errorRegex2Col.Match line)
        else None
    match m1 with
    | None -> None
    | Some m ->
        let severityText = m.Groups.["severity"].Value
        let severity =
            if severityText = "warning" then Warning else Error
        let path = m.Groups.["path"].Value.Trim()
        let lineStart = int m.Groups.["line"].Value
        let colStart = int m.Groups.["col"].Value
        let lineEndOpt =
            let grp = m.Groups.["lineEnd"]
            if grp.Success then Some(int grp.Value) else None
        let colEndOpt =
            let grp = m.Groups.["colEnd"]
            if grp.Success then Some(int grp.Value) else None
        let code = m.Groups.["code"].Value
        let msg = m.Groups.["msg"].Value

        let normalizedPath = resolveThroughAliases aliases path

        let normalizedMsg = normalizeMessage aliases msg

        let occ : DiagnosticOccurrence =
            { SchemaVersion = OccurrenceSchemaVersion
              ExtractorVersion = extractorVersion
              CaptureId = captureId
              SourceKind = LegacyText
              EventOrdinal = int64 lineNo
              Severity = severity
              Subcategory = None
              Code = Some code
              MessageRaw = msg
              MessageNormalized = normalizedMsg
              LocationKind = Source
              SourcePath = Some normalizedPath
              ProjectPath = None
              Span =
                { StartLine = Some lineStart
                  StartColumn = Some colStart
                  EndLine = lineEndOpt
                  EndColumn = colEndOpt }
              SenderName = None
              EventTimestamp = None
              BuildContext = None
              LegacySourceLineStart = Some lineNo
              LegacySourceLineEnd = Some lineNo }
        Some { LineStart = lineNo; LineEnd = lineNo; Occurrence = occ }

/// Parse a legacy text log.  Returns the full accounting result plus the
/// collected occurrences.  The function is total: malformed lines that
/// look diagnostic are accumulated, never silently dropped.
let parseText
    (captureId: string)
    (aliases: SourceRootAlias list)
    (extractorVersion: string)
    (text: string)
    : LegacyParseResult =
    let lines =
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n')
    let totalLines = lines.Length
    let mutable ordinal = 0L
    let occurrences = System.Collections.Generic.List<DiagnosticOccurrence>()
    let undeclared = System.Collections.Generic.List<string>()
    let unparsedSamples = System.Collections.Generic.List<string>()
    let mutable continuationCount = 0
    let mutable ignoredCount = 0
    let mutable unparsedCount = 0
    let mutable i = 0
    while i < lines.Length do
        let line = lines.[i]
        let lineNo = i + 1
        if System.String.IsNullOrWhiteSpace line then
            // blank line — never a diagnostic, never a continuation.
            i <- i + 1
        else
            let parsed = tryMatchDiagnosticLine lineNo line captureId aliases extractorVersion
            match parsed with
            | Some d ->
                // Collect continuation lines until we hit a non-continuation line.
                let mutable endLine = lineNo
                let mutable j = i + 1
                let mutable continuationMsg = ""
                while j < lines.Length do
                    let next = lines.[j]
                    if System.String.IsNullOrWhiteSpace next then
                        j <- lines.Length  // stop at blank line
                    elif continuationRegex.IsMatch next
                         && not (tryMatchDiagnosticLine (j + 1) next captureId aliases extractorVersion |> Option.isSome)
                         && not (isOrdinaryNonDiagnostic next) then
                        continuationMsg <- continuationMsg + (if continuationMsg = "" then "" else "\n") + next.TrimStart()
                        continuationCount <- continuationCount + 1
                        endLine <- j + 1
                        j <- j + 1
                    else
                        j <- lines.Length
                let fullMsg =
                    if continuationMsg = "" then d.Occurrence.MessageRaw
                    else d.Occurrence.MessageRaw + "\n" + continuationMsg
                let fullMsgNormalized = normalizeMessage aliases fullMsg
                ordinal <- ordinal + 1L
                let occ : DiagnosticOccurrence =
                    { d.Occurrence with
                        EventOrdinal = ordinal
                        MessageRaw = fullMsg
                        MessageNormalized = fullMsgNormalized
                        LegacySourceLineStart = Some lineNo
                        LegacySourceLineEnd = Some endLine }
                occurrences.Add occ
                // Check that the source path uses a declared alias.
                match occ.SourcePath with
                | Some p when
                       containsUndeclaredAbsolutePath aliases p
                       && not (matchesDeclaredAlias aliases p) ->
                    undeclared.Add p
                | _ -> ()
                i <- endLine
            | None ->
                if genericDiagnosticLikeRegex.IsMatch line then
                    unparsedCount <- unparsedCount + 1
                    if unparsedSamples.Count < 8 then
                        unparsedSamples.Add line
                elif isOrdinaryNonDiagnostic line then
                    ignoredCount <- ignoredCount + 1
                else
                    // Truly ordinary content (e.g. compiler banner). Count as
                    // ignored non-diagnostic.
                    ignoredCount <- ignoredCount + 1
                i <- i + 1
    { InputLines = totalLines
      ParsedDiagnostics = occurrences.Count
      ContinuationLines = continuationCount
      IgnoredNonDiagnosticLines = ignoredCount
      DiagnosticLookingUnparsedLines = unparsedCount
      Occurrences = occurrences |> Seq.toList
      CaptureId = captureId
      UnparsedDiagnosticLikeSamples = unparsedSamples |> Seq.toList
      UndeclaredAbsolutePaths = undeclared |> Seq.distinct |> Seq.toList }

/// Convenience: parse text and assert fail-closed conditions.
let parseTextFailClosed
    (captureId: string)
    (aliases: SourceRootAlias list)
    (extractorVersion: string)
    (text: string)
    : Result<LegacyParseResult, string> =
    let result = parseText captureId aliases extractorVersion text
    if result.DiagnosticLookingUnparsedLines > 0 then
        Result.Error(
            sprintf
                "legacy text extraction failed closed: %d diagnostic-looking unparsed line(s) (samples: %s)"
                result.DiagnosticLookingUnparsedLines
                (String.concat " | " result.UnparsedDiagnosticLikeSamples))
    elif not (List.isEmpty result.UndeclaredAbsolutePaths) then
        Result.Error(
            sprintf
                "legacy text extraction failed closed: undeclared absolute paths: %s"
                (String.concat ", " result.UndeclaredAbsolutePaths))
    else
        Ok result