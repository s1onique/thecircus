module Circus.Tooling.FSharpDiagnostics.RepairEpisodes.OccurrenceReader

// =============================================================================
// Strict occurrence reader
// =============================================================================
//
// Reads foundation occurrence JSONL streams.  The contract is exact:
//
//   * UTF-8, NUL bytes forbidden, decoder throws on invalid bytes.
//   * UTF-8 BOM is rejected with a dedicated non-canonical-encoding error.
//   * Line endings normalised to LF (CRLF and lone CR both accepted).
//   * Each nonblank line is a single JSON object.
//   * Duplicate property names are detected via the parsed JsonDocument
//     so escape-sequence equivalents (e.g. ``"\\u0073chema_version"`` and
//     ``"schema_version"``) are rejected.
//   * Unknown property names are rejected.
//   * Required non-null properties must be present and non-null.
//   * Nullable-but-required properties must be present; absent and
//     explicit ``null`` are distinguished.
//   * ``event_ordinal`` is parsed as a non-fractional ``int64``.
//   * ``span`` is a nested object whose coordinates are ``int option``.
//   * Severity, source kind, and location kind accept only canonical tokens.
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
    | FileReadFailed of canonicalPath: string * detail: string
    | NonCanonicalEncoding of canonicalPath: string
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
// File-level guard bytes
// =============================================================================

let private startsWithBom (bytes: byte[]) : bool =
    bytes.Length >= 3
    && bytes.[0] = 0xEFuy
    && bytes.[1] = 0xBBuy
    && bytes.[2] = 0xBFuy

let private containsNul (bytes: byte[]) : bool =
    Array.exists (fun b -> b = 0uy) bytes

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

let private checkDuplicates
    (canonicalPath: string)
    (lineNumber: int)
    (fieldPrefix: string)
    (root: JsonElement)
    : unit =
    let seen = System.Collections.Generic.HashSet<string>(StringComparer.Ordinal)
    let mutable dup : string option = None
    for prop in root.EnumerateObject() do
        if dup.IsNone && not (seen.Add prop.Name) then
            dup <- Some prop.Name
    match dup with
    | Some n -> failWith (DuplicateField (canonicalPath, lineNumber, fieldPrefix + n))
    | None -> ()

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

/// Required non-null string.  Absent or explicit null raise WrongJsonKind/MissingField.
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

/// Nullable-but-required string.  Absent → MissingField, null → None,
/// wrong kind → WrongJsonKind, string → Some value.
let private requiredNullableStringField
    (canonicalPath: string)
    (lineNumber: int)
    (field: string)
    (root: JsonElement)
    : string option =
    let el =
        match tryGetProperty root field with
        | Some v -> v
        | None -> failWith (MissingField (canonicalPath, lineNumber, field))
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

/// Nullable-but-required int.  Absent → MissingField, null → None,
/// wrong kind → WrongJsonKind, integer → Some value, fractional/out-of-range → IntegerOutOfRange.
let private requiredNullableIntField
    (canonicalPath: string)
    (lineNumber: int)
    (field: string)
    (root: JsonElement)
    : int option =
    let el =
        match tryGetProperty root field with
        | Some v -> v
        | None -> failWith (MissingField (canonicalPath, lineNumber, field))
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
        checkDuplicates canonicalPath lineNumber "span." el
        checkUnknownFields canonicalPath lineNumber knownSpanNames el
        let startLine = requiredNullableIntField canonicalPath lineNumber "start_line" el
        let startColumn = requiredNullableIntField canonicalPath lineNumber "start_column" el
        let endLine = requiredNullableIntField canonicalPath lineNumber "end_line" el
        let endColumn = requiredNullableIntField canonicalPath lineNumber "end_column" el
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
        checkDuplicates canonicalPath lineNumber "build_context." el
        checkUnknownFields canonicalPath lineNumber knownBuildContextNames el
        let nodeId = requiredNullableIntField canonicalPath lineNumber "node_id" el
        let projectContextId = requiredNullableIntField canonicalPath lineNumber "project_context_id" el
        let targetId = requiredNullableIntField canonicalPath lineNumber "target_id" el
        let taskId = requiredNullableIntField canonicalPath lineNumber "task_id" el
        let evaluationId = requiredNullableIntField canonicalPath lineNumber "evaluation_id" el
        let submissionId = requiredNullableIntField canonicalPath lineNumber "submission_id" el
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
    checkDuplicates canonicalPath lineNumber "" root
    checkUnknownFields canonicalPath lineNumber knownTopLevelNames root

    let schemaVersion = requiredStringField canonicalPath lineNumber "schema_version" root
    if schemaVersion <> OccurrenceSchemaVersion then
        failWith (SchemaVersionMismatch (canonicalPath, lineNumber, schemaVersion))

    let extractorVersion = requiredStringField canonicalPath lineNumber "extractor_version" root
    let captureId = requiredStringField canonicalPath lineNumber "capture_id" root
    let sourceKind = requiredEnumField canonicalPath lineNumber "source_kind" root tryParseSourceKind
    let eventOrdinal = requiredInt64Field canonicalPath lineNumber "event_ordinal" root
    let severity = requiredEnumField canonicalPath lineNumber "severity" root tryParseSeverity
    let subcategory = requiredNullableStringField canonicalPath lineNumber "subcategory" root
    let code = requiredNullableStringField canonicalPath lineNumber "code" root
    let messageRaw = requiredStringField canonicalPath lineNumber "message_raw" root
    let messageNormalized = requiredStringField canonicalPath lineNumber "message_normalized" root
    let locationKind = requiredEnumField canonicalPath lineNumber "location_kind" root tryParseLocationKind
    let sourcePath = requiredNullableStringField canonicalPath lineNumber "source_path" root
    let projectPath = requiredNullableStringField canonicalPath lineNumber "project_path" root
    let span = parseSpan canonicalPath lineNumber root
    let senderName = requiredNullableStringField canonicalPath lineNumber "sender_name" root
    let eventTimestamp = requiredNullableStringField canonicalPath lineNumber "event_timestamp" root
    let buildContext = parseBuildContext canonicalPath lineNumber root
    let legacyLineStart = requiredNullableIntField canonicalPath lineNumber "legacy_source_line_start" root
    let legacyLineEnd = requiredNullableIntField canonicalPath lineNumber "legacy_source_line_end" root

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

let private readBytes (canonicalPath: string) : Result<byte[], OccurrenceReadFailure> =
    try
        Result.Ok (File.ReadAllBytes canonicalPath)
    with
    | :? IOException as ex ->
        Result.Error (FileReadFailed (canonicalPath, ex.Message))
    | :? UnauthorizedAccessException as ex ->
        Result.Error (FileReadFailed (canonicalPath, ex.Message))

let readOccurrences (canonicalPath: string) : Result<DiagnosticOccurrence list, OccurrenceReadFailure> =
    if not (File.Exists canonicalPath) then
        Result.Error (FileMissing canonicalPath)
    else
        match readBytes canonicalPath with
        | Result.Error e -> Result.Error e
        | Result.Ok bytes ->
            if containsNul bytes then
                Result.Error (InvalidUtf8 canonicalPath)
            elif startsWithBom bytes then
                Result.Error (NonCanonicalEncoding canonicalPath)
            else
                try
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
