module Circus.Tooling.Tests.FSharpDiagnostics.LegacyTextExtractorTests

open Expecto
open Circus.Tooling.FSharpDiagnostics.Domain
open Circus.Tooling.FSharpDiagnostics.LegacyTextExtractor

let aliases =
    [ { AbsoluteRoot = "/home/me/project"
        CanonicalRoot = "<REPO>" } ]

[<Tests>]
let tests =
    testList "FSharpDiagnostics.LegacyTextExtractor" [
      test "single-line warning parses" {
        let text = "/home/me/project/src/Foo.fs(10,5): warning FS0001: hello world"
        let r = parseText "cap-1" aliases "v1" text
        Expect.equal r.ParsedDiagnostics 1 "one diagnostic"
        Expect.equal r.DiagnosticLookingUnparsedLines 0 "none unparsed"
        Expect.equal (List.length r.Occurrences) 1 "one occurrence"
        let occ = List.head r.Occurrences
        Expect.equal occ.Severity Warning "warning"
        Expect.equal occ.Code (Some "FS0001") "code"
        Expect.equal occ.SourcePath (Some "<REPO>/src/Foo.fs") "aliased path"
      }
      test "single-line error parses" {
        let text = "/home/me/project/src/Foo.fs(3,7): error FS0002: bad"
        let r = parseText "cap-1" aliases "v1" text
        Expect.equal r.ParsedDiagnostics 1 "one"
        let occ = List.head r.Occurrences
        Expect.equal occ.Severity Error "error"
        Expect.equal occ.Code (Some "FS0002") "code"
      }
      test "two-column span parses" {
        let text = "/home/me/project/src/Foo.fs(3,7-5,9): warning FS0001: x"
        let r = parseText "cap-1" aliases "v1" text
        let occ = List.head r.Occurrences
        Expect.equal occ.Span.EndLine (Some 5) "end_line"
        Expect.equal occ.Span.EndColumn (Some 9) "end_column"
      }
      test "multiline message preserved" {
        // Build the text without F# string continuation so leading spaces
        // are preserved exactly.
        let text = String.concat "\n" [
          "/home/me/project/src/Foo.fs(10,5): warning FS0001: first line"
          "     second line"
          "     third line"
        ]
        let r = parseText "cap-1" aliases "v1" text
        Expect.equal r.ParsedDiagnostics 1 "one diagnostic"
        Expect.equal r.ContinuationLines 2 "two continuation lines"
        let occ = List.head r.Occurrences
        Expect.stringContains occ.MessageRaw "first line" "first"
        Expect.stringContains occ.MessageRaw "second line" "second"
        Expect.stringContains occ.MessageRaw "third line" "third"
      }
      test "absolute root alias replacement" {
        let text = "/home/me/project/src/Bar.fs(1,1): warning FS0001: x"
        let r = parseText "cap-1" aliases "v1" text
        let occ = List.head r.Occurrences
        Expect.equal occ.SourcePath (Some "<REPO>/src/Bar.fs") "aliased"
      }
      test "undeclared absolute path fails closed" {
        let text = "/other/unknown/path/Foo.fs(1,1): warning FS0001: x"
        let r = parseText "cap-1" aliases "v1" text
        Expect.isTrue (List.isEmpty r.UndeclaredAbsolutePaths |> not) "has undeclared"
        let failResult = parseTextFailClosed "cap-1" aliases "v1" text
        Expect.isFalse (Result.isOk failResult) "fail closed"
      }
      test "diagnostic-looking line without proper structure unparsed" {
        let text = "/home/me/project/src/Foo.fs(10,5): warning NoCodeHere message"
        let r = parseText "cap-1" aliases "v1" text
        Expect.equal r.DiagnosticLookingUnparsedLines 1 "one unparsed"
        Expect.equal (List.length r.Occurrences) 0 "no occurrences"
      }
      test "accounting totals are complete" {
        // Build text via String.concat so leading spaces survive.
        let text = String.concat "\n" [
          "Build succeeded."
          "/home/me/project/src/Foo.fs(10,5): warning FS0001: x"
          "/home/me/project/src/Bar.fs(3,7): error FS0002: y"
          "/home/me/project/src/Foo.fs(1,1): warning FS0001: x"
          "   multiline continuation"
          "Build FAILED."
          "   1 Warning(s)"
          "   1 Error(s)"
        ]
        let r = parseText "cap-1" aliases "v1" text
        Expect.equal r.ParsedDiagnostics 3 "three diagnostics"
        Expect.equal r.ContinuationLines 1 "one continuation"
        Expect.equal r.DiagnosticLookingUnparsedLines 0 "none unparsed"
        Expect.isTrue (r.IgnoredNonDiagnosticLines >= 4) "ignored summaries"
      }
      test "two messages at same coordinate are preserved" {
        let text =
            "/home/me/project/src/Foo.fs(10,5): warning FS0001: first message\n\
/home/me/project/src/Foo.fs(10,5): warning FS0001: second message"
        let r = parseText "cap-1" aliases "v1" text
        Expect.equal r.ParsedDiagnostics 2 "two diagnostics"
        let msgs = r.Occurrences |> List.map (fun o -> o.MessageRaw)
        Expect.contains msgs "first message" "first"
        Expect.contains msgs "second message" "second"
      }
      test "no first-word truncation" {
        let text = "/home/me/project/src/Foo.fs(10,5): warning FS0001: List<int> has 42 elements"
        let r = parseText "cap-1" aliases "v1" text
        let occ = List.head r.Occurrences
        Expect.stringContains occ.MessageRaw "List<int>" "type preserved"
        Expect.stringContains occ.MessageRaw "42" "number preserved"
        Expect.stringContains occ.MessageRaw "has 42 elements" "full message"
      }
    ]