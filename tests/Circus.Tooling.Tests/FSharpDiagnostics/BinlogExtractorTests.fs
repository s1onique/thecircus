module Circus.Tooling.Tests.FSharpDiagnostics.BinlogExtractorTests

open Expecto
open System.IO
open Circus.Tooling.FSharpDiagnostics.BinlogExtractor
open Circus.Tooling.FSharpDiagnostics.Domain
open Circus.Tooling.FSharpDiagnostics.Hashing

let mkEvent severity subcategory code file line col endLine endCol msg =
    {
      EventOrdinal = 0L
      Severity = severity
      Subcategory = subcategory
      Code = code
      File = file
      ProjectFile = Some "src/Project.fsproj"
      LineNumber = line
      ColumnNumber = col
      EndLineNumber = endLine
      EndColumnNumber = endCol
      Message = msg
      SenderName = Some "F# Compiler"
      Timestamp = None
      NodeId = None
      ProjectContextId = None
      TargetId = None
      TaskId = None
    }

let aliases =
    [ { AbsoluteRoot = "/home/me/project"
        CanonicalRoot = "<REPO>" } ]

[<Tests>]
let tests =
    testList "FSharpDiagnostics.BinlogExtractor" [
      test "fromSyntheticEvents produces ordered occurrences" {
        let path = Path.GetTempFileName()
        try
            let events = [
              mkEvent Warning (Some "FSCompilers") (Some "FS0001")
                (Some "/home/me/project/src/Foo.fs")
                (Some 10) (Some 5) (Some 10) (Some 10)
                "first warning"
              mkEvent Error (Some "FSCompilers") (Some "FS0002")
                (Some "/home/me/project/src/Bar.fs")
                (Some 3) (Some 7) (Some 3) (Some 7)
                "first error"
            ]
            let result, occs = fromSyntheticEvents "cap-1" aliases "v1" path events
            Expect.equal result.PreReplay.Sha256 (Circus.Tooling.FSharpDiagnostics.Hashing.sha256OfFile path) "hash recorded"
            Expect.equal (List.length occs) 2 "two occurrences"
            Expect.equal occs.[0].EventOrdinal 1L "first ordinal"
            Expect.equal occs.[1].EventOrdinal 2L "second ordinal"
            Expect.equal occs.[0].SourceKind Binlog "binlog"
        finally
            File.Delete path
      }
      test "fromSyntheticEvents preserves complete source span" {
        let path = Path.GetTempFileName()
        try
            let events = [
              mkEvent Warning (Some "FSCompilers") (Some "FS0001")
                (Some "/home/me/project/src/Foo.fs")
                (Some 10) (Some 5) (Some 12) (Some 30)
                "spans multiple lines"
            ]
            let _, occs = fromSyntheticEvents "cap-1" aliases "v1" path events
            let occ = List.head occs
            Expect.equal occ.Span.StartLine (Some 10) "start_line"
            Expect.equal occ.Span.StartColumn (Some 5) "start_column"
            Expect.equal occ.Span.EndLine (Some 12) "end_line"
            Expect.equal occ.Span.EndColumn (Some 30) "end_column"
        finally
            File.Delete path
      }
      test "fromSyntheticEvents preserves complete message text" {
        let path = Path.GetTempFileName()
        try
            let events = [
              mkEvent Warning (Some "FSCompilers") (Some "FS0001")
                (Some "/home/me/project/src/Foo.fs")
                (Some 10) (Some 5) (Some 10) (Some 5)
                "Type 'List<int>' has 42 elements which is a lot"
            ]
            let _, occs = fromSyntheticEvents "cap-1" aliases "v1" path events
            let occ = List.head occs
            Expect.stringContains occ.MessageRaw "List<int>'" "type preserved"
            Expect.stringContains occ.MessageRaw "42" "number preserved"
            Expect.equal occ.MessageRaw "Type 'List<int>' has 42 elements which is a lot" "complete"
        finally
            File.Delete path
      }
      test "fromSyntheticEvents separates warning and error severities" {
        let path = Path.GetTempFileName()
        try
            let events = [
              mkEvent Warning (Some "FSCompilers") (Some "FS0001")
                (Some "/home/me/project/src/Foo.fs")
                (Some 1) (Some 1) (Some 1) (Some 1) "w"
              mkEvent Error (Some "FSCompilers") (Some "FS0002")
                (Some "/home/me/project/src/Foo.fs")
                (Some 2) (Some 1) (Some 2) (Some 1) "e"
            ]
            let _, occs = fromSyntheticEvents "cap-1" aliases "v1" path events
            Expect.equal occs.[0].Severity Warning "warning first"
            Expect.equal occs.[1].Severity Error "error second"
        finally
            File.Delete path
      }
      test "extractFromBinlog rejects missing file" {
        Expect.throws (fun () -> extractFromBinlog "/nonexistent/path/x.binlog" |> ignore) "raises"
      }
      test "extractFromBinlog rejects corrupt file" {
        let path = Path.GetTempFileName()
        File.WriteAllBytes(path, [| 0xCAuy; 0xFEuy; 0xBAuy; 0xBEuy |])
        try
            Expect.throws (fun () -> extractFromBinlog path |> ignore) "raises BinlogExtractionFailure"
        finally
            File.Delete path
      }
      test "fromSyntheticEvents aliases source path" {
        let path = Path.GetTempFileName()
        try
            let events = [
              mkEvent Warning (Some "FSCompilers") (Some "FS0001")
                (Some "/home/me/project/src/Foo.fs")
                (Some 10) (Some 5) (Some 10) (Some 5)
                "msg"
            ]
            let _, occs = fromSyntheticEvents "cap-1" aliases "v1" path events
            Expect.equal (List.head occs).SourcePath (Some "<REPO>/src/Foo.fs") "aliased"
        finally
            File.Delete path
      }
    ]