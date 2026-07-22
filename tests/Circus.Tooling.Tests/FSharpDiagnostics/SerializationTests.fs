module Circus.Tooling.Tests.FSharpDiagnostics.SerializationTests

open Expecto
open Circus.Tooling.FSharpDiagnostics.Domain
open Circus.Tooling.FSharpDiagnostics.Serialization

[<Tests>]
let tests =
    testList
        "FSharpDiagnostics.Serialization"
        [ test "escapeJsonString escapes special characters" {
              Expect.equal (escapeJsonString "hello") "\"hello\""
              Expect.equal (escapeJsonString "a\"b") "\"a\\\"b\""
              Expect.equal (escapeJsonString "a\nb") "\"a\\nb\""
              Expect.equal (escapeJsonString "a\\b") "\"a\\\\b\""
              Expect.equal (escapeJsonString "a	b") "\"a\\tb\""
          }
          test "optStr null vs empty" {
              Expect.equal (optStr None) "null"
              Expect.equal (optStr (Some "")) "\"\""
              Expect.equal (optStr (Some "x")) "\"x\""
          }
          test "optIntStr renders null" {
              Expect.equal (optIntStr None) "null"
              Expect.equal (optIntStr (Some 42)) "42"
          }
          test "renderFingerprintTsv renders row" {
              let fp =
                  { FingerprintVersion = ExactFingerprintVersion
                    Severity = "warning"
                    Subcategory = None
                    Code = Some "FS0001"
                    SourcePath = Some "src/Foo.fs"
                    ProjectPath = None
                    StartLine = Some 10
                    StartColumn = Some 5
                    EndLine = Some 10
                    EndColumn = Some 10
                    MessageNormalized = "msg"
                    Sha256 = "abc"
                    OccurrenceCount = 1 }

              let row = renderFingerprintTsv fp
              Expect.stringContains row "abc\twarning\t" "sha256 then severity"
              Expect.stringContains row "\tFS0001\t" "code"
              Expect.stringContains row "\tsrc/Foo.fs\t" "path"
              Expect.stringContains row "\t10\t5\t10\t10\t" "span"
              Expect.stringContains row "\tmsg\t" "message"
              Expect.stringContains row "\t1" "count"
          }
          test "renderOccurrence JSON has stable field order" {
              let occ =
                  { SchemaVersion = OccurrenceSchemaVersion
                    ExtractorVersion = "v1"
                    CaptureId = "cap-1"
                    SourceKind = LegacyText
                    EventOrdinal = 1L
                    Severity = Warning
                    Subcategory = None
                    Code = Some "FS0001"
                    MessageRaw = "msg"
                    MessageNormalized = "msg"
                    LocationKind = Source
                    SourcePath = Some "src/Foo.fs"
                    ProjectPath = None
                    Span =
                      { StartLine = Some 1
                        StartColumn = Some 1
                        EndLine = None
                        EndColumn = None }
                    SenderName = None
                    EventTimestamp = None
                    BuildContext = None
                    LegacySourceLineStart = None
                    LegacySourceLineEnd = None }

              let json = renderOccurrence occ
              Expect.stringContains json "\"schema_version\":\"diagnostic-occurrence-v1\"" "schema"
              Expect.stringContains json "\"capture_id\":\"cap-1\"" "capture_id"
              Expect.stringContains json "\"source_kind\":\"legacy_text\"" "source_kind"
              Expect.stringContains json "\"event_ordinal\":1" "ordinal"
              Expect.stringContains json "\"severity\":\"warning\"" "severity"
              Expect.stringContains json "\"code\":\"FS0001\"" "code"
              Expect.stringContains json "\"message_raw\":\"msg\"" "raw"
              Expect.stringContains json "\"message_normalized\":\"msg\"" "normalized"
              Expect.stringContains json "\"location_kind\":\"source\"" "location"
              Expect.stringContains json "\"source_path\":\"src/Foo.fs\"" "source_path"
              Expect.stringContains json "\"project_path\":null" "null project_path"
              Expect.stringContains json "\"end_line\":null" "null end_line"
          }
          test "renderCorpusSummary renders all counts" {
              let s =
                  { SchemaVersion = CorpusSummarySchemaVersion
                    ExtractorVersion = "v1"
                    ArtifactsTotal = 5
                    RawArtifacts = 2
                    NormalizedArtifacts = 2
                    DerivedArtifacts = 1
                    CorrectionArtifacts = 0
                    SourceSnapshotArtifacts = 0
                    ObsoleteRetainedArtifacts = 0
                    UnclassifiedArtifacts = 0
                    CapturesTotal = 1
                    BinlogCaptures = 0
                    LegacyTextCaptures = 1
                    MixedCaptures = 0
                    OccurrenceCount = 10
                    UniqueExactFingerprintCount = 8
                    DuplicateOccurrenceCount = 2
                    DiagnosticLookingUnparsedLines = 0
                    MetadataGaps = [] }

              let json = renderCorpusSummary s
              Expect.stringContains json "\"occurrence_count\":10" "occurrences"
              Expect.stringContains json "\"unique_exact_fingerprint_count\":8" "unique"
              Expect.stringContains json "\"duplicate_occurrence_count\":2" "duplicates"
          } ]
