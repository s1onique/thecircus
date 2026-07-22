module Circus.Tooling.Tests.FSharpDiagnostics.OccurrenceIdentityTests

open Expecto
open Circus.Tooling.FSharpDiagnostics.Domain
open Circus.Tooling.FSharpDiagnostics.OccurrenceIdentity

let mkOcc severity code msg sourcePath span =
    { SchemaVersion = OccurrenceSchemaVersion
      ExtractorVersion = "test-v1"
      CaptureId = "test-capture"
      SourceKind = LegacyText
      EventOrdinal = 1L
      Severity = severity
      Subcategory = None
      Code = Some code
      MessageRaw = msg
      MessageNormalized = msg
      LocationKind = Source
      SourcePath = Some sourcePath
      ProjectPath = None
      Span = span
      SenderName = None
      EventTimestamp = None
      BuildContext = None
      LegacySourceLineStart = None
      LegacySourceLineEnd = None }

let baseOcc =
    mkOcc
        Warning
        "FS0001"
        "hello"
        "src/Foo.fs"
        { StartLine = Some 10
          StartColumn = Some 5
          EndLine = Some 10
          EndColumn = Some 10 }

[<Tests>]
let tests =
    testList
        "FSharpDiagnostics.OccurrenceIdentity"
        [ test "fingerprint is deterministic" {
              let a = fingerprintFor baseOcc
              let b = fingerprintFor baseOcc
              Expect.equal a b "deterministic"
              Expect.equal (a.Length) 64 "sha256 length"
          }
          test "different severity changes fingerprint" {
              let other = { baseOcc with Severity = Error }
              Expect.notEqual (fingerprintFor baseOcc) (fingerprintFor other) "different severity"
          }
          test "different code changes fingerprint" {
              let other = { baseOcc with Code = Some "FS0002" }
              Expect.notEqual (fingerprintFor baseOcc) (fingerprintFor other) "different code"
          }
          test "different source_path changes fingerprint" {
              let other =
                  { baseOcc with
                      SourcePath = Some "src/Bar.fs" }

              Expect.notEqual (fingerprintFor baseOcc) (fingerprintFor other) "different path"
          }
          test "different start_line changes fingerprint" {
              let other =
                  { baseOcc with
                      Span =
                          { baseOcc.Span with
                              StartLine = Some 11 } }

              Expect.notEqual (fingerprintFor baseOcc) (fingerprintFor other) "different start_line"
          }
          test "different start_column changes fingerprint" {
              let other =
                  { baseOcc with
                      Span =
                          { baseOcc.Span with
                              StartColumn = Some 6 } }

              Expect.notEqual (fingerprintFor baseOcc) (fingerprintFor other) "different start_column"
          }
          test "different end_line changes fingerprint" {
              let other =
                  { baseOcc with
                      Span = { baseOcc.Span with EndLine = Some 11 } }

              Expect.notEqual (fingerprintFor baseOcc) (fingerprintFor other) "different end_line"
          }
          test "different end_column changes fingerprint" {
              let other =
                  { baseOcc with
                      Span =
                          { baseOcc.Span with
                              EndColumn = Some 11 } }

              Expect.notEqual (fingerprintFor baseOcc) (fingerprintFor other) "different end_column"
          }
          test "different message_normalized changes fingerprint" {
              let other =
                  { baseOcc with
                      MessageNormalized = "different" }

              Expect.notEqual (fingerprintFor baseOcc) (fingerprintFor other) "different message"
          }
          test "different capture_id does NOT change fingerprint" {
              let other =
                  { baseOcc with
                      CaptureId = "other-capture" }

              Expect.equal (fingerprintFor baseOcc) (fingerprintFor other) "capture_id excluded"
          }
          test "different event_ordinal does NOT change fingerprint" {
              let other = { baseOcc with EventOrdinal = 99L }
              Expect.equal (fingerprintFor baseOcc) (fingerprintFor other) "event_ordinal excluded"
          }
          test "different sender does NOT change fingerprint" {
              let other =
                  { baseOcc with
                      SenderName = Some "F# Compiler" }

              Expect.equal (fingerprintFor baseOcc) (fingerprintFor other) "sender excluded"
          }
          test "different subcategory changes fingerprint" {
              let other =
                  { baseOcc with
                      Subcategory = Some "FSCompilers" }

              Expect.notEqual (fingerprintFor baseOcc) (fingerprintFor other) "different subcategory"
          }
          test "aggregateFingerprints counts duplicates" {
              let occs = [ baseOcc; baseOcc; baseOcc ]
              let fps = aggregateFingerprints occs
              Expect.equal (List.length fps) 1 "one unique"
              Expect.equal (List.head fps).OccurrenceCount 3 "count = 3"
          }
          test "aggregateFingerprints keeps distinct fingerprints separate" {
              let other = { baseOcc with Code = Some "FS0002" }
              let occs = [ baseOcc; baseOcc; other ]
              let fps = aggregateFingerprints occs
              Expect.equal (List.length fps) 2 "two unique"
          }
          test "two messages at same coordinate are different fingerprints" {
              let a =
                  mkOcc
                      Warning
                      "FS0001"
                      "first message"
                      "src/Foo.fs"
                      { StartLine = Some 10
                        StartColumn = Some 5
                        EndLine = Some 10
                        EndColumn = Some 10 }

              let b =
                  mkOcc
                      Warning
                      "FS0001"
                      "second message"
                      "src/Foo.fs"
                      { StartLine = Some 10
                        StartColumn = Some 5
                        EndLine = Some 10
                        EndColumn = Some 10 }

              Expect.notEqual (fingerprintFor a) (fingerprintFor b) "messages differ"
              Expect.equal a.SourcePath b.SourcePath "same path"
              Expect.equal a.Span b.Span "same span"
          } ]
