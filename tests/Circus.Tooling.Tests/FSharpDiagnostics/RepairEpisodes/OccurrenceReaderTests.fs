module Circus.Tooling.Tests.FSharpDiagnostics.RepairEpisodes.OccurrenceReaderTests

open Expecto
open System.IO
open System.Text
open Circus.Tooling.FSharpDiagnostics.Domain
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.OccurrenceReader
open Circus.Tooling.FSharpDiagnostics.Serialization

let private newTempDir () =
    let dir =
        Path.Combine(
            Path.GetTempPath(),
            "fsharp-diagnostics-occurrence-reader-" + System.Guid.NewGuid().ToString("N")
        )

    Directory.CreateDirectory dir |> ignore
    dir

let private cleanup (dir: string) =
    if Directory.Exists dir then
        try
            Directory.Delete(dir, true)
        with _ ->
            ()

let private writeBytes (dir: string) (name: string) (bytes: byte[]) : string =
    let path = Path.Combine(dir, name)
    File.WriteAllBytes(path, bytes)
    path

let private utf8NoBom : Encoding =
    new UTF8Encoding(false)

let private writeText (dir: string) (name: string) (text: string) : string =
    let path = Path.Combine(dir, name)
    File.WriteAllText(path, text, utf8NoBom)
    path

/// Build a complete valid occurrence line (no optional fields populated).
let private validJsonLine () : string =
    "{\"schema_version\":\"diagnostic-occurrence-v1\""
    + ",\"extractor_version\":\"test-v1\""
    + ",\"capture_id\":\"test-capture\""
    + ",\"source_kind\":\"legacy_text\""
    + ",\"event_ordinal\":1"
    + ",\"severity\":\"warning\""
    + ",\"subcategory\":null"
    + ",\"code\":null"
    + ",\"message_raw\":\"hello\""
    + ",\"message_normalized\":\"hello\""
    + ",\"location_kind\":\"source\""
    + ",\"source_path\":null"
    + ",\"project_path\":null"
    + ",\"span\":{\"start_line\":null,\"start_column\":null,\"end_line\":null,\"end_column\":null}"
    + ",\"sender_name\":null"
    + ",\"event_timestamp\":null"
    + ",\"build_context\":null"
    + ",\"legacy_source_line_start\":null"
    + ",\"legacy_source_line_end\":null}"

let private validOccurrence =
    { SchemaVersion = OccurrenceSchemaVersion
      ExtractorVersion = "test-v1"
      CaptureId = "test-capture"
      SourceKind = LegacyText
      EventOrdinal = 1L
      Severity = Warning
      Subcategory = None
      Code = None
      MessageRaw = "hello"
      MessageNormalized = "hello"
      LocationKind = Source
      SourcePath = None
      ProjectPath = None
      Span = emptySpan
      SenderName = None
      EventTimestamp = None
      BuildContext = None
      LegacySourceLineStart = None
      LegacySourceLineEnd = None }

[<Tests>]
let tests =
    testList
        "FSharpDiagnostics.RepairEpisodes.OccurrenceReader"
        [ test "complete valid occurrence parses to one record" {
              let dir = newTempDir ()
              try
                  let path = writeText dir "one.jsonl" (validJsonLine () + "\n")
                  let r = readOccurrences path
                  match r with
                  | Result.Ok records ->
                      Expect.equal (List.length records) 1 "one record"
                      Expect.equal (List.head records) validOccurrence "matches"
                  | Result.Error f -> failwithf "expected Ok, got %A" f
              finally
                  cleanup dir
          }

          test "nested span with four coordinates parses correctly" {
              let dir = newTempDir ()
              try
                  let line =
                      "{\"schema_version\":\"diagnostic-occurrence-v1\""
                      + ",\"extractor_version\":\"v1\""
                      + ",\"capture_id\":\"cap\""
                      + ",\"source_kind\":\"binlog\""
                      + ",\"event_ordinal\":7"
                      + ",\"severity\":\"error\""
                      + ",\"subcategory\":null,\"code\":\"FS0001\""
                      + ",\"message_raw\":\"m\""
                      + ",\"message_normalized\":\"m\""
                      + ",\"location_kind\":\"source\""
                      + ",\"source_path\":\"src/A.fs\""
                      + ",\"project_path\":null"
                      + ",\"span\":{\"start_line\":10,\"start_column\":5,\"end_line\":12,\"end_column\":15}"
                      + ",\"sender_name\":null,\"event_timestamp\":null,\"build_context\":null"
                      + ",\"legacy_source_line_start\":null,\"legacy_source_line_end\":null}"
                  let path = writeText dir "span.jsonl" (line + "\n")
                  let r = readOccurrences path
                  match r with
                  | Result.Ok records ->
                      let occ = List.head records
                      Expect.equal occ.Span.StartLine (Some 10) "start_line"
                      Expect.equal occ.Span.StartColumn (Some 5) "start_column"
                      Expect.equal occ.Span.EndLine (Some 12) "end_line"
                      Expect.equal occ.Span.EndColumn (Some 15) "end_column"
                  | Result.Error f -> failwithf "expected Ok, got %A" f
              finally
                  cleanup dir
          }

          test "explicitly null span coordinates are preserved as None" {
              let dir = newTempDir ()
              try
                  let path = writeText dir "nullspan.jsonl" (validJsonLine () + "\n")
                  let r = readOccurrences path
                  match r with
                  | Result.Ok records ->
                      let occ = List.head records
                      Expect.isNone occ.Span.StartLine "start_line"
                      Expect.isNone occ.Span.StartColumn "start_column"
                      Expect.isNone occ.Span.EndLine "end_line"
                      Expect.isNone occ.Span.EndColumn "end_column"
                  | Result.Error f -> failwithf "expected Ok, got %A" f
              finally
                  cleanup dir
          }

          test "event ordinal above Int32.MaxValue is accepted as int64" {
              let dir = newTempDir ()
              try
                  let line =
                      "{\"schema_version\":\"diagnostic-occurrence-v1\""
                      + ",\"extractor_version\":\"v1\",\"capture_id\":\"cap\""
                      + ",\"source_kind\":\"binlog\""
                      + ",\"event_ordinal\":3000000000"
                      + ",\"severity\":\"warning\",\"subcategory\":null,\"code\":null"
                      + ",\"message_raw\":\"m\",\"message_normalized\":\"m\""
                      + ",\"location_kind\":\"source\",\"source_path\":null,\"project_path\":null"
                      + ",\"span\":{\"start_line\":null,\"start_column\":null,\"end_line\":null,\"end_column\":null}"
                      + ",\"sender_name\":null,\"event_timestamp\":null,\"build_context\":null"
                      + ",\"legacy_source_line_start\":null,\"legacy_source_line_end\":null}"
                  let path = writeText dir "big.jsonl" (line + "\n")
                  let r = readOccurrences path
                  match r with
                  | Result.Ok records ->
                      Expect.equal (List.head records).EventOrdinal 3000000000L "ordinal"
                  | Result.Error f -> failwithf "expected Ok, got %A" f
              finally
                  cleanup dir
          }

          test "missing required field returns MissingField" {
              let dir = newTempDir ()
              try
                  // extractor_version intentionally omitted.
                  let line =
                      "{\"schema_version\":\"diagnostic-occurrence-v1\""
                      + ",\"capture_id\":\"cap\""
                      + ",\"source_kind\":\"binlog\",\"event_ordinal\":1"
                      + ",\"severity\":\"warning\",\"subcategory\":null,\"code\":null"
                      + ",\"message_raw\":\"m\",\"message_normalized\":\"m\""
                      + ",\"location_kind\":\"source\",\"source_path\":null,\"project_path\":null"
                      + ",\"span\":{\"start_line\":null,\"start_column\":null,\"end_line\":null,\"end_column\":null}"
                      + ",\"sender_name\":null,\"event_timestamp\":null,\"build_context\":null"
                      + ",\"legacy_source_line_start\":null,\"legacy_source_line_end\":null}"
                  let path = writeText dir "missing.jsonl" (line + "\n")
                  let r = readOccurrences path
                  match r with
                  | Result.Ok _ -> failwithf "expected Error"
                  | Result.Error (MissingField (_, _, field)) ->
                      Expect.equal field "extractor_version" "extractor_version missing"
                  | Result.Error f -> failwithf "expected MissingField, got %A" f
              finally
                  cleanup dir
          }

          test "duplicate top-level field returns DuplicateField" {
              let dir = newTempDir ()
              try
                  let line =
                      "{\"schema_version\":\"diagnostic-occurrence-v1\""
                      + ",\"schema_version\":\"diagnostic-occurrence-v1\""
                      + ",\"extractor_version\":\"v1\",\"capture_id\":\"cap\""
                      + ",\"source_kind\":\"binlog\",\"event_ordinal\":1"
                      + ",\"severity\":\"warning\",\"subcategory\":null,\"code\":null"
                      + ",\"message_raw\":\"m\",\"message_normalized\":\"m\""
                      + ",\"location_kind\":\"source\",\"source_path\":null,\"project_path\":null"
                      + ",\"span\":{\"start_line\":null,\"start_column\":null,\"end_line\":null,\"end_column\":null}"
                      + ",\"sender_name\":null,\"event_timestamp\":null,\"build_context\":null"
                      + ",\"legacy_source_line_start\":null,\"legacy_source_line_end\":null}"
                  let path = writeText dir "dup.jsonl" (line + "\n")
                  let r = readOccurrences path
                  match r with
                  | Result.Ok _ -> failwithf "expected Error"
                  | Result.Error (DuplicateField (_, _, field)) ->
                      Expect.equal field "schema_version" "schema_version"
                  | Result.Error f -> failwithf "expected DuplicateField, got %A" f
              finally
                  cleanup dir
          }

          test "duplicate span field returns DuplicateField" {
              let dir = newTempDir ()
              try
                  let line =
                      "{\"schema_version\":\"diagnostic-occurrence-v1\""
                      + ",\"extractor_version\":\"v1\",\"capture_id\":\"cap\""
                      + ",\"source_kind\":\"binlog\",\"event_ordinal\":1"
                      + ",\"severity\":\"warning\",\"subcategory\":null,\"code\":null"
                      + ",\"message_raw\":\"m\",\"message_normalized\":\"m\""
                      + ",\"location_kind\":\"source\",\"source_path\":null,\"project_path\":null"
                      + ",\"span\":{\"start_line\":10,\"start_line\":11,\"start_column\":5,\"end_line\":12,\"end_column\":15}"
                      + ",\"sender_name\":null,\"event_timestamp\":null,\"build_context\":null"
                      + ",\"legacy_source_line_start\":null,\"legacy_source_line_end\":null}"
                  let path = writeText dir "dupspan.jsonl" (line + "\n")
                  let r = readOccurrences path
                  match r with
                  | Result.Ok _ -> failwithf "expected Error"
                  | Result.Error (DuplicateField (_, _, field)) ->
                      Expect.equal field "span.start_line" "span.start_line"
                  | Result.Error f -> failwithf "expected DuplicateField, got %A" f
              finally
                  cleanup dir
          }

          test "unknown field returns UnknownField" {
              let dir = newTempDir ()
              try
                  let line =
                      "{\"schema_version\":\"diagnostic-occurrence-v1\""
                      + ",\"extractor_version\":\"v1\",\"capture_id\":\"cap\""
                      + ",\"source_kind\":\"binlog\",\"event_ordinal\":1"
                      + ",\"severity\":\"warning\",\"subcategory\":null,\"code\":null"
                      + ",\"message_raw\":\"m\",\"message_normalized\":\"m\""
                      + ",\"location_kind\":\"source\",\"source_path\":null,\"project_path\":null"
                      + ",\"span\":{\"start_line\":null,\"start_column\":null,\"end_line\":null,\"end_column\":null}"
                      + ",\"sender_name\":null,\"event_timestamp\":null,\"build_context\":null"
                      + ",\"legacy_source_line_start\":null,\"legacy_source_line_end\":null"
                      + ",\"extra_field\":\"extra\"}"
                  let path = writeText dir "unknown.jsonl" (line + "\n")
                  let r = readOccurrences path
                  match r with
                  | Result.Ok _ -> failwithf "expected Error"
                  | Result.Error (UnknownField (_, _, field)) ->
                      Expect.equal field "extra_field" "extra_field"
                  | Result.Error f -> failwithf "expected UnknownField, got %A" f
              finally
                  cleanup dir
          }

          test "unknown severity returns InvalidEnumToken" {
              let dir = newTempDir ()
              try
                  let line =
                      "{\"schema_version\":\"diagnostic-occurrence-v1\""
                      + ",\"extractor_version\":\"v1\",\"capture_id\":\"cap\""
                      + ",\"source_kind\":\"binlog\",\"event_ordinal\":1"
                      + ",\"severity\":\"not_a_severity\",\"subcategory\":null,\"code\":null"
                      + ",\"message_raw\":\"m\",\"message_normalized\":\"m\""
                      + ",\"location_kind\":\"source\",\"source_path\":null,\"project_path\":null"
                      + ",\"span\":{\"start_line\":null,\"start_column\":null,\"end_line\":null,\"end_column\":null}"
                      + ",\"sender_name\":null,\"event_timestamp\":null,\"build_context\":null"
                      + ",\"legacy_source_line_start\":null,\"legacy_source_line_end\":null}"
                  let path = writeText dir "badsev.jsonl" (line + "\n")
                  let r = readOccurrences path
                  match r with
                  | Result.Ok _ -> failwithf "expected Error"
                  | Result.Error (InvalidEnumToken (_, _, field, token)) ->
                      Expect.equal field "severity" "severity"
                      Expect.equal token "not_a_severity" "token"
                  | Result.Error f -> failwithf "expected InvalidEnumToken, got %A" f
              finally
                  cleanup dir
          }

          test "unknown source kind returns InvalidEnumToken" {
              let dir = newTempDir ()
              try
                  let line =
                      "{\"schema_version\":\"diagnostic-occurrence-v1\""
                      + ",\"extractor_version\":\"v1\",\"capture_id\":\"cap\""
                      + ",\"source_kind\":\"not_a_source\",\"event_ordinal\":1"
                      + ",\"severity\":\"warning\",\"subcategory\":null,\"code\":null"
                      + ",\"message_raw\":\"m\",\"message_normalized\":\"m\""
                      + ",\"location_kind\":\"source\",\"source_path\":null,\"project_path\":null"
                      + ",\"span\":{\"start_line\":null,\"start_column\":null,\"end_line\":null,\"end_column\":null}"
                      + ",\"sender_name\":null,\"event_timestamp\":null,\"build_context\":null"
                      + ",\"legacy_source_line_start\":null,\"legacy_source_line_end\":null}"
                  let path = writeText dir "badsk.jsonl" (line + "\n")
                  let r = readOccurrences path
                  match r with
                  | Result.Ok _ -> failwithf "expected Error"
                  | Result.Error (InvalidEnumToken (_, _, field, token)) ->
                      Expect.equal field "source_kind" "source_kind"
                      Expect.equal token "not_a_source" "token"
                  | Result.Error f -> failwithf "expected InvalidEnumToken, got %A" f
              finally
                  cleanup dir
          }

          test "unknown location kind returns InvalidEnumToken" {
              let dir = newTempDir ()
              try
                  let line =
                      "{\"schema_version\":\"diagnostic-occurrence-v1\""
                      + ",\"extractor_version\":\"v1\",\"capture_id\":\"cap\""
                      + ",\"source_kind\":\"binlog\",\"event_ordinal\":1"
                      + ",\"severity\":\"warning\",\"subcategory\":null,\"code\":null"
                      + ",\"message_raw\":\"m\",\"message_normalized\":\"m\""
                      + ",\"location_kind\":\"nowhere\",\"source_path\":null,\"project_path\":null"
                      + ",\"span\":{\"start_line\":null,\"start_column\":null,\"end_line\":null,\"end_column\":null}"
                      + ",\"sender_name\":null,\"event_timestamp\":null,\"build_context\":null"
                      + ",\"legacy_source_line_start\":null,\"legacy_source_line_end\":null}"
                  let path = writeText dir "badlk.jsonl" (line + "\n")
                  let r = readOccurrences path
                  match r with
                  | Result.Ok _ -> failwithf "expected Error"
                  | Result.Error (InvalidEnumToken (_, _, field, token)) ->
                      Expect.equal field "location_kind" "location_kind"
                      Expect.equal token "nowhere" "token"
                  | Result.Error f -> failwithf "expected InvalidEnumToken, got %A" f
              finally
                  cleanup dir
          }

          test "wrong JSON kind (number for required string) returns WrongJsonKind" {
              let dir = newTempDir ()
              try
                  // capture_id is a required string but provided as a number.
                  let line =
                      "{\"schema_version\":\"diagnostic-occurrence-v1\""
                      + ",\"extractor_version\":\"v1\",\"capture_id\":42"
                      + ",\"source_kind\":\"binlog\",\"event_ordinal\":1"
                      + ",\"severity\":\"warning\",\"subcategory\":null,\"code\":null"
                      + ",\"message_raw\":\"m\",\"message_normalized\":\"m\""
                      + ",\"location_kind\":\"source\",\"source_path\":null,\"project_path\":null"
                      + ",\"span\":{\"start_line\":null,\"start_column\":null,\"end_line\":null,\"end_column\":null}"
                      + ",\"sender_name\":null,\"event_timestamp\":null,\"build_context\":null"
                      + ",\"legacy_source_line_start\":null,\"legacy_source_line_end\":null}"
                  let path = writeText dir "wrongkind.jsonl" (line + "\n")
                  let r = readOccurrences path
                  match r with
                  | Result.Ok _ -> failwithf "expected Error"
                  | Result.Error (WrongJsonKind (_, _, field)) ->
                      Expect.equal field "capture_id" "capture_id"
                  | Result.Error f -> failwithf "expected WrongJsonKind, got %A" f
              finally
                  cleanup dir
          }

          test "required field set to null returns WrongJsonKind" {
              let dir = newTempDir ()
              try
                  let line =
                      "{\"schema_version\":\"diagnostic-occurrence-v1\""
                      + ",\"extractor_version\":null,\"capture_id\":\"cap\""
                      + ",\"source_kind\":\"binlog\",\"event_ordinal\":1"
                      + ",\"severity\":\"warning\",\"subcategory\":null,\"code\":null"
                      + ",\"message_raw\":\"m\",\"message_normalized\":\"m\""
                      + ",\"location_kind\":\"source\",\"source_path\":null,\"project_path\":null"
                      + ",\"span\":{\"start_line\":null,\"start_column\":null,\"end_line\":null,\"end_column\":null}"
                      + ",\"sender_name\":null,\"event_timestamp\":null,\"build_context\":null"
                      + ",\"legacy_source_line_start\":null,\"legacy_source_line_end\":null}"
                  let path = writeText dir "requirednull.jsonl" (line + "\n")
                  let r = readOccurrences path
                  match r with
                  | Result.Ok _ -> failwithf "expected Error"
                  | Result.Error (WrongJsonKind (_, _, field)) ->
                      Expect.equal field "extractor_version" "extractor_version"
                  | Result.Error f -> failwithf "expected WrongJsonKind, got %A" f
              finally
                  cleanup dir
          }

          test "fractional event ordinal returns IntegerOutOfRange" {
              let dir = newTempDir ()
              try
                  let line =
                      "{\"schema_version\":\"diagnostic-occurrence-v1\""
                      + ",\"extractor_version\":\"v1\",\"capture_id\":\"cap\""
                      + ",\"source_kind\":\"binlog\",\"event_ordinal\":1.5"
                      + ",\"severity\":\"warning\",\"subcategory\":null,\"code\":null"
                      + ",\"message_raw\":\"m\",\"message_normalized\":\"m\""
                      + ",\"location_kind\":\"source\",\"source_path\":null,\"project_path\":null"
                      + ",\"span\":{\"start_line\":null,\"start_column\":null,\"end_line\":null,\"end_column\":null}"
                      + ",\"sender_name\":null,\"event_timestamp\":null,\"build_context\":null"
                      + ",\"legacy_source_line_start\":null,\"legacy_source_line_end\":null}"
                  let path = writeText dir "frac.jsonl" (line + "\n")
                  let r = readOccurrences path
                  match r with
                  | Result.Ok _ -> failwithf "expected Error"
                  | Result.Error (IntegerOutOfRange (_, _, field)) ->
                      Expect.equal field "event_ordinal" "event_ordinal"
                  | Result.Error f -> failwithf "expected IntegerOutOfRange, got %A" f
              finally
                  cleanup dir
          }

          test "event ordinal outside Int64 returns IntegerOutOfRange" {
              let dir = newTempDir ()
              try
                  let line =
                      "{\"schema_version\":\"diagnostic-occurrence-v1\""
                      + ",\"extractor_version\":\"v1\",\"capture_id\":\"cap\""
                      + ",\"source_kind\":\"binlog\""
                      + ",\"event_ordinal\":99999999999999999999"
                      + ",\"severity\":\"warning\",\"subcategory\":null,\"code\":null"
                      + ",\"message_raw\":\"m\",\"message_normalized\":\"m\""
                      + ",\"location_kind\":\"source\",\"source_path\":null,\"project_path\":null"
                      + ",\"span\":{\"start_line\":null,\"start_column\":null,\"end_line\":null,\"end_column\":null}"
                      + ",\"sender_name\":null,\"event_timestamp\":null,\"build_context\":null"
                      + ",\"legacy_source_line_start\":null,\"legacy_source_line_end\":null}"
                  let path = writeText dir "bigeo.jsonl" (line + "\n")
                  let r = readOccurrences path
                  match r with
                  | Result.Ok _ -> failwithf "expected Error"
                  | Result.Error (IntegerOutOfRange (_, _, field)) ->
                      Expect.equal field "event_ordinal" "event_ordinal"
                  | Result.Error f -> failwithf "expected IntegerOutOfRange, got %A" f
              finally
                  cleanup dir
          }

          test "malformed JSON reports correct one-based line number" {
              let dir = newTempDir ()
              try
                  let content =
                      validJsonLine () + "\n"
                      + validJsonLine () + "\n"
                      + "{not valid json"
                  let path = writeText dir "malformed.jsonl" content
                  let r = readOccurrences path
                  match r with
                  | Result.Ok _ -> failwithf "expected Error"
                  | Result.Error (InvalidJson (_, lineNumber, _)) ->
                      Expect.equal lineNumber 3 "line number"
                  | Result.Error f -> failwithf "expected InvalidJson, got %A" f
              finally
                  cleanup dir
          }

          test "valid lines followed by invalid line return no partial list" {
              let dir = newTempDir ()
              try
                  let content =
                      validJsonLine () + "\n"
                      + validJsonLine () + "\n"
                      + "{\"oops\":true}"
                  let path = writeText dir "partial.jsonl" content
                  let r = readOccurrences path
                  match r with
                  | Result.Ok _ -> failwithf "expected Error, not partial list"
                  | Result.Error _ -> ()
              finally
                  cleanup dir
          }

          test "invalid UTF-8 bytes return InvalidUtf8" {
              let dir = newTempDir ()
              try
                  // 0xFF 0xFE 0xFD are not valid UTF-8 leading bytes.
                  let bytes = [| 0xFFuy; 0xFEuy; 0xFDuy |]
                  let path = writeBytes dir "badutf8.jsonl" bytes
                  let r = readOccurrences path
                  match r with
                  | Result.Ok _ -> failwithf "expected Error"
                  | Result.Error (InvalidUtf8 _) -> ()
                  | Result.Error f -> failwithf "expected InvalidUtf8, got %A" f
              finally
                  cleanup dir
          }

          test "CRLF and LF produce identical typed results" {
              let dir = newTempDir ()
              try
                  let lf = validJsonLine () + "\n" + validJsonLine () + "\n"
                  let crlf = lf.Replace("\n", "\r\n")
                  let lfPath = writeText dir "lf.jsonl" lf
                  let crlfPath = writeText dir "crlf.jsonl" crlf
                  let lfR = readOccurrences lfPath
                  let crlfR = readOccurrences crlfPath
                  match lfR, crlfR with
                  | Result.Ok lfRecords, Result.Ok crlfRecords ->
                      Expect.equal lfRecords crlfRecords "identical records"
                  | _ -> failwith "expected both Ok"
              finally
                  cleanup dir
          }

          test "fully populated optional metadata is preserved" {
              let occ =
                  { SchemaVersion = OccurrenceSchemaVersion
                    ExtractorVersion = "v1"
                    CaptureId = "cap"
                    SourceKind = Binlog
                    EventOrdinal = 99L
                    Severity = Error
                    Subcategory = Some "FSCompilers"
                    Code = Some "FS0001"
                    MessageRaw = "raw message"
                    MessageNormalized = "normalized message"
                    LocationKind = Source
                    SourcePath = Some "src/Foo.fs"
                    ProjectPath = Some "src/Foo.fsproj"
                    Span =
                      { StartLine = Some 10
                        StartColumn = Some 5
                        EndLine = Some 12
                        EndColumn = Some 15 }
                    SenderName = Some "F# Compiler"
                    EventTimestamp = Some "2024-01-01T00:00:00Z"
                    BuildContext =
                      Some
                        { NodeId = Some 1
                          ProjectContextId = Some 2
                          TargetId = Some 3
                          TaskId = Some 4
                          EvaluationId = Some 5
                          SubmissionId = Some 6 }
                    LegacySourceLineStart = Some 7
                    LegacySourceLineEnd = Some 8 }

              let rendered = renderOccurrence occ
              let dir = newTempDir ()
              try
                  let path = writeText dir "full.jsonl" (rendered + "\n")
                  let r = readOccurrences path
                  match r with
                  | Result.Ok records ->
                      Expect.equal (List.length records) 1 "one record"
                      Expect.equal (List.head records) occ "round-trip equality"
                  | Result.Error f -> failwithf "expected Ok, got %A" f
              finally
                  cleanup dir
          }

          test "renderer-reader round trip preserves all fields" {
              let make i =
                  { validOccurrence with
                      EventOrdinal = int64 i
                      Code = Some (sprintf "FS%04d" i)
                      MessageRaw = sprintf "raw-%d" i
                      MessageNormalized = sprintf "normalized-%d" i
                      SourcePath = Some (sprintf "src/File%d.fs" i)
                      Span =
                        { StartLine = Some i
                          StartColumn = Some (i * 2)
                          EndLine = Some (i + 1)
                          EndColumn = Some (i * 2 + 1) } }

              let records = [ for i in 1 .. 3 -> make i ]
              let rendered = records |> List.map renderOccurrence |> String.concat "\n"
              let dir = newTempDir ()
              try
                  let path = writeText dir "round.jsonl" (rendered + "\n")
                  let r = readOccurrences path
                  match r with
                  | Result.Ok got ->
                      Expect.equal got records "round-trip equality"
                  | Result.Error f -> failwithf "expected Ok, got %A" f
              finally
                  cleanup dir
          }

          test "missing file returns FileMissing" {
              let path =
                  Path.Combine(
                      Path.GetTempPath(),
                      "does-not-exist-" + System.Guid.NewGuid().ToString("N") + ".jsonl"
                  )
              let r = readOccurrences path
              match r with
              | Result.Ok _ -> failwithf "expected Error"
              | Result.Error (FileMissing _) -> ()
              | Result.Error f -> failwithf "expected FileMissing, got %A" f
          }

          test "blank lines are skipped, file with only blank lines yields empty list" {
              let dir = newTempDir ()
              try
                  let path = writeText dir "blank.jsonl" "\n\n   \n\n"
                  let r = readOccurrences path
                  match r with
                  | Result.Ok records -> Expect.isEmpty records "no records"
                  | Result.Error f -> failwithf "expected Ok, got %A" f
              finally
                  cleanup dir
          } ]
