module Circus.Tooling.FSharpDiagnostics.RepairEpisodes.OccurrenceReader

// =============================================================================
// Strict occurrence reader
// =============================================================================
//
// Reads foundation occurrence JSONL streams.  The contract is exact:
//
//   * UTF-8, NUL bytes forbidden, decoder throws on invalid bytes.
//   * Line endings normalised to LF (CRLF and lone CR both accepted).
//   * Each nonblank line is a single JSON object.
//   * Duplicate property names are rejected (line-level and nested).
//   * Unknown property names are rejected.
//   * Required properties must be present and non-null.
//   * Nullable-but-required properties must be present; absent and
//     explicit ``null`` are distinguished.
//   * ``event_ordinal`` is parsed as a non-fractional ``int64``.
//   * ``span`` is a nested object whose coordinates are ``int option``.
//   * Severity, source kind, and location kind accept only canonical tokens.
//   * All optional metadata is preserved as ``option`` values.
//   * A malformed nonblank line aborts the entire file (no partial list).
//
// The public boundary is ``Result<list, OccurrenceReadFailure>``.  A single
// private exception is used internally to short-circuit nested helpers
// without threading failure values through every layer.

open System
open System.IO
open System.Text
open System.Text.Json
open Circus.Tooling.FSharpDiagnostics.Domain

type OccurrenceReadFailure =
    | FileMissing of canonicalPath: string
    | InvalidUtf8 of canonicalPath: string
    | InvalidJson of canonicalPath: string * lineNumber: int * detail: string
    | RootNotObject of canonicalPath: string * lineNumber: int
    | MissingField of canonicalPath: string * lineNumber: int * field: string
    | DuplicateField of canonicalPath: string * lineNumber: int * field: string
    | UnknownField of canonicalPath: string * lineNumber: int * field: string
    | WrongJsonKind of canonicalPath: string * lineNumber: int * field: string
    | InvalidEnumToken of canonicalPath: string * lineNumber: int * field: string * token: string
    | IntegerOutOfRange of canonicalPath: string * lineNumber: int * field: string
    | SchemaVersionMismatch of canonicalPath: string * lineNumber: int * actual: string

exception private ParserAbort of OccurrenceReadFailure

// =============================================================================
// Strict UTF-8 decoding
// =============================================================================

let private strictUtf8 : UTF8Encoding =
    UTF8Encoding(false, true)

let private decodeBytes (canonicalPath: string) (bytes: byte[]) : string =
    try
        strictUtf8.GetString(bytes)
    with
    | :? DecoderFallbackException ->
        raise (ParserAbort (InvalidUtf8 canonicalPath))

// =============================================================================
// Line ending normalisation
// =============================================================================

let private normaliseLineEndings (s: string) : string =
    let sb = StringBuilder(s.Length)
    let mutable i = 0
    while i < s.Length do
        let c = s.[i]
        if c = '\r' then
            sb.Append '\n' |> ignore
            if i + 1 < s.Length && s.[i + 1] = '\n' then
                i <- i + 1
        else
            sb.Append c |> ignore
        i <- i + 1
    sb.ToString()

// =============================================================================
// Property-name collection and duplicate detection (line text scan)
// =============================================================================

let private collectPropertyNames (line: string) (targetDepth: int) : string list =
    let names = ResizeArray<string>()
    let sb = StringBuilder()
    let mutable i = 0
    let mutable inString = false
    let mutable escaped = false
    let mutable depth = 0
    while i < line.Length do
        let c = line.[i]
        if inString then
            if escaped then
                escaped <- false
                sb.Append c |> ignore
                if c = 'u' && i + 4 < line.Length then
                    sb.Append (line.Substring(i + 1, 4)) |> ignore
                    i <- i + 4
            elif c = '\\' then
                escaped <- true
            elif c = '"' then
                inString <- false
                let name = sb.ToString()
                sb.Clear() |> ignore
                let mutable j = i + 1
                while j < line.Length && Char.IsWhiteSpace line.[j] do
                    j <- j + 1
                if depth = targetDepth && j < line.Length && line.[j] = ':' then
                    names.Add name
            else
                sb.Append c |> ignore
        else
            if c = '"' then
                inString <- true
                sb.Clear() |> ignore
            elif c = '{' || c = '[' then
                depth <- depth + 1
            elif c = '}' || c = ']' then
                depth <- depth - 1
        i <- i + 1
    names |> Seq.toList

let private findDuplicatePropertyName (line: string) (targetDepth: int) : string option =
    let names = collectPropertyNames line targetDepth
    let seen = System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
    let mutable dup : string option = None
    for n in names do
        match dup with
        | Some _ -> ()
        | None ->
            if not (seen.Add n) then
                dup <- Some n
    dup

// =============================================================================
// Known field sets (ordinal, case-sensitive)
// =============================================================================

let private knownTopLevelNames : Set<string> =
    set [
        "schema_version"
        "extractor_version"
        "capture_id"
        "source_kind"
        "event_ordinal"
        "severity"
        "subcategory"
        "code"
        "message_raw"
        "message_normalized"
        "location_kind"
        "source_path"
        "project_path"
        "span"
        "sender_name"
        "event_timestamp"
        "build_context"
        "legacy_source_line_start"
        "legacy_source_line_end"
    ]

let private knownSpanNames : Set<string> =
    set [
        "start_line"
        "start_column"
        "end_line"
        "end_column"
    ]

let private knownBuildContextNames : Set<string> =
    set [
        "node_id"
        "project_context_id"
        "target_id"
        "task_id"
        "evaluation_id"
        "submission_id"
    ]

// =============================================================================
// Field access helpers
// =============================================================================

let private failWith (failure: OccurrenceReadFailure) : 'a =
    raise (ParserAbort failure)

let private tryGetProperty (root: JsonElement) (name: string) : JsonElement option =
    let mutable result = Unchecked.defaultof<JsonElement>
    if root.TryGetProperty(name, &result) then
        Some result
    else
        None

let private checkUnknownFields
    (canonicalPath: string)
    (lineNumber: int)
    (allowed: Set<string>)
    (root: JsonElement)
    : unit =
    let firstUnknown =
        root.EnumerateObject()
        |> Seq.tryPick (fun p ->
            if Set.contains p.Name allowed then None
            else Some p.Name)
    match firstUnknown with
    | Some n -> failWith (UnknownField (canonicalPath, lineNumber, n))
    | None -> ()

let private requiredStringField
    (canonicalPath: string)
    (lineNumber: int)
    (field: string)
    (root: JsonElement)
    : string =
    let el =
        match tryGetProperty root field with
        | Some v -> v
        | None -> failWith (MissingField (canonicalPath, lineNumber, field))
    if el.ValueKind = JsonValueKind.Null then
        failWith (WrongJsonKind (canonicalPath, lineNumber, field))
    elif el.ValueKind <> JsonValueKind.String then
        failWith (WrongJsonKind (canonicalPath, lineNumber, field))
    else
        el.GetString()

let private optionalStringField
    (canonicalPath: string)
    (lineNumber: int)
    (field: string)
    (root: JsonElement)
    : string option =
    match tryGetProperty root field with
    | None -> None
    | Some el ->
        if el.ValueKind = JsonValueKind.Null then
            None
        elif el.ValueKind <> JsonValueKind.String then
            failWith (WrongJsonKind (canonicalPath, lineNumber, field))
        else
            Some (el.GetString())

let private requiredInt64Field
    (canonicalPath: string)
    (lineNumber: int)
    (field: string)
    (root: JsonElement)
    : int64 =
    let el =
        match tryGetProperty root field with
        | Some v -> v
        | None -> failWith (MissingField (canonicalPath, lineNumber, field))
    if el.ValueKind <> JsonValueKind.Number then
        failWith (WrongJsonKind (canonicalPath, lineNumber, field))
    let raw = el.GetRawText()
    if raw.Contains "." || raw.Contains "e" || raw.Contains "E" then
        failWith (IntegerOutOfRange (canonicalPath, lineNumber, field))
    try
        Int64.Parse(raw, Globalization.CultureInfo.InvariantCulture)
    with _ ->
        failWith (IntegerOutOfRange (canonicalPath, lineNumber, field))

let private optionalIntField
    (canonicalPath: string)
    (lineNumber: int)
    (field: string)
    (root: JsonElement)
    : int option =
    match tryGetProperty root field with
    | None -> None
    | Some el ->
        if el.ValueKind = JsonValueKind.Null then
            None
        elif el.ValueKind <> JsonValueKind.Number then
            failWith (WrongJsonKind (canonicalPath, lineNumber, field))
        else
            let raw = el.GetRawText()
            if raw.Contains "." || raw.Contains "e" || raw.Contains "E" then
                failWith (IntegerOutOfRange (canonicalPath, lineNumber, field))
            try
                Some (Int32.Parse(raw, Globalization.CultureInfo.InvariantCulture))
            with _ ->
                failWith (IntegerOutOfRange (canonicalPath, lineNumber, field))

let private requiredEnumField
    (canonicalPath: string)
    (lineNumber: int)
    (field: string)
    (root: JsonElement)
    (tryParse: string -> 'a option)
    : 'a =
    let el =
        match tryGetProperty root field with
        | Some v -> v
        | None -> failWith (MissingField (canonicalPath, lineNumber, field))
    if el.ValueKind = JsonValueKind.Null then
        failWith (WrongJsonKind (canonicalPath, lineNumber, field))
    elif el.ValueKind <> JsonValueKind.String then
        failWith (WrongJsonKind (canonicalPath, lineNumber, field))
    else
        let token = el.GetString()
        match tryParse token with
        | Some v -> v
        | None -> failWith (InvalidEnumToken (canonicalPath, lineNumber, field, token))

// =============================================================================
// Nested object parsing
// =============================================================================

let private parseSpan
    (canonicalPath: string)
    (lineNumber: int)
    (root: JsonElement)
    : SourceSpan =
    let el =
        match tryGetProperty root "span" with
        | Some v -> v
        | None -> failWith (MissingField (canonicalPath, lineNumber, "span"))
    if el.ValueKind = JsonValueKind.Null then
        failWith (WrongJsonKind (canonicalPath, lineNumber, "span"))
    elif el.ValueKind <> JsonValueKind.Object then
        failWith (WrongJsonKind (canonicalPath, lineNumber, "span"))
    else
        let rawText = el.GetRawText()
        match findDuplicatePropertyName rawText 1 with
        | Some dup -> failWith (DuplicateField (canonicalPath, lineNumber, "span." + dup))
        | None -> ()
        checkUnknownFields canonicalPath lineNumber knownSpanNames el
        let startLine = optionalIntField canonicalPath lineNumber "start_line" el
        let startColumn = optionalIntField canonicalPath lineNumber "start_column" el
        let endLine = optionalIntField canonicalPath lineNumber "end_line" el
        let endColumn = optionalIntField canonicalPath lineNumber "end_column" el
        { StartLine = startLine
          StartColumn = startColumn
          EndLine = endLine
          EndColumn = endColumn }

let private parseBuildContext
    (canonicalPath: string)
    (lineNumber: int)
    (root: JsonElement)
    : BuildContext option =
    let el =
        match tryGetProperty root "build_context" with
        | Some v -> v
        | None -> failWith (MissingField (canonicalPath, lineNumber, "build_context"))
    if el.ValueKind = JsonValueKind.Null then
        None
    elif el.ValueKind <> JsonValueKind.Object then
        failWith (WrongJsonKind (canonicalPath, lineNumber, "build_context"))
    else
        let rawText = el.GetRawText()
        match findDuplicatePropertyName rawText 1 with
        | Some dup -> failWith (DuplicateField (canonicalPath, lineNumber, "build_context." + dup))
        | None -> ()
        checkUnknownFields canonicalPath lineNumber knownBuildContextNames el
        let nodeId = optionalIntField canonicalPath lineNumber "node_id" el
        let projectContextId = optionalIntField canonicalPath lineNumber "project_context_id" el
        let targetId = optionalIntField canonicalPath lineNumber "target_id" el
        let taskId = optionalIntField canonicalPath lineNumber "task_id" el
        let evaluationId = optionalIntField canonicalPath lineNumber "evaluation_id" el
        let submissionId = optionalIntField canonicalPath lineNumber "submission_id" el
        Some
            { NodeId = nodeId
              ProjectContextId = projectContextId
              TargetId = targetId
              TaskId = taskId
              EvaluationId = evaluationId
              SubmissionId = submissionId }

// =============================================================================
// Line-level parsing
// =============================================================================

let private parseLine
    (canonicalPath: string)
    (lineNumber: int)
    (line: string)
    : DiagnosticOccurrence =
    match findDuplicatePropertyName line 1 with
    | Some dup -> failWith (DuplicateField (canonicalPath, lineNumber, dup))
    | None -> ()

    let doc =
        try
            JsonDocument.Parse(line)
        with
        | :? JsonException as ex ->
            failWith (InvalidJson (canonicalPath, lineNumber, ex.Message))

    use doc = doc
    let root = doc.RootElement
    if root.ValueKind <> JsonValueKind.Object then
        failWith (RootNotObject (canonicalPath, lineNumber))
    checkUnknownFields canonicalPath lineNumber knownTopLevelNames root

    let schemaVersion = requiredStringField canonicalPath lineNumber "schema_version" root
    if schemaVersion <> OccurrenceSchemaVersion then
        failWith (SchemaVersionMismatch (canonicalPath, lineNumber, schemaVersion))

    let extractorVersion = requiredStringField canonicalPath lineNumber "extractor_version" root
    let captureId = requiredStringField canonicalPath lineNumber "capture_id" root
    let sourceKind = requiredEnumField canonicalPath lineNumber "source_kind" root tryParseSourceKind
    let eventOrdinal = requiredInt64Field canonicalPath lineNumber "event_ordinal" root
    let severity = requiredEnumField canonicalPath lineNumber "severity" root tryParseSeverity
    let subcategory = optionalStringField canonicalPath lineNumber "subcategory" root
    let code = optionalStringField canonicalPath lineNumber "code" root
    let messageRaw = requiredStringField canonicalPath lineNumber "message_raw" root
    let messageNormalized = requiredStringField canonicalPath lineNumber "message_normalized" root
    let locationKind = requiredEnumField canonicalPath lineNumber "location_kind" root tryParseLocationKind
    let sourcePath = optionalStringField canonicalPath lineNumber "source_path" root
    let projectPath = optionalStringField canonicalPath lineNumber "project_path" root
    let span = parseSpan canonicalPath lineNumber root
    let senderName = optionalStringField canonicalPath lineNumber "sender_name" root
    let eventTimestamp = optionalStringField canonicalPath lineNumber "event_timestamp" root
    let buildContext = parseBuildContext canonicalPath lineNumber root
    let legacyLineStart = optionalIntField canonicalPath lineNumber "legacy_source_line_start" root
    let legacyLineEnd = optionalIntField canonicalPath lineNumber "legacy_source_line_end" root

    { SchemaVersion = schemaVersion
      ExtractorVersion = extractorVersion
      CaptureId = captureId
      SourceKind = sourceKind
      EventOrdinal = eventOrdinal
      Severity = severity
      Subcategory = subcategory
      Code = code
      MessageRaw = messageRaw
      MessageNormalized = messageNormalized
      LocationKind = locationKind
      SourcePath = sourcePath
      ProjectPath = projectPath
      Span = span
      SenderName = senderName
      EventTimestamp = eventTimestamp
      BuildContext = buildContext
      LegacySourceLineStart = legacyLineStart
      LegacySourceLineEnd = legacyLineEnd }

// =============================================================================
// File reader (public boundary)
// =============================================================================

let readOccurrences (canonicalPath: string) : Result<DiagnosticOccurrence list, OccurrenceReadFailure> =
    if not (File.Exists canonicalPath) then
        Result.Error (FileMissing canonicalPath)
    else
        try
            let bytes = File.ReadAllBytes canonicalPath
            if Array.exists (fun b -> b = 0uy) bytes then
                Result.Error (InvalidUtf8 canonicalPath)
            else
                let text = decodeBytes canonicalPath bytes
                let normalised = normaliseLineEndings text
                let lines =
                    normalised.Split([| '\n' |], StringSplitOptions.None)
                let mutable acc : DiagnosticOccurrence list = []
                let mutable lineNumber = 0
                let mutable failure : OccurrenceReadFailure =
                    FileMissing canonicalPath
                let mutable aborted = false
                for line in lines do
                    lineNumber <- lineNumber + 1
                    if not aborted then
                        if not (String.IsNullOrWhiteSpace line) then
                            try
                                let occ =
                                    parseLine canonicalPath lineNumber line
                                acc <- occ :: acc
                            with
                            | ParserAbort f ->
                                aborted <- true
                                failure <- f
                if aborted then
                    Result.Error failure
                else
                    Result.Ok (acc |> List.rev)
        with
        | ParserAbort f -> Result.Error f
