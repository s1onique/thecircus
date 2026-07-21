module Circus.Tooling.Tests.SourcePolicy.NulInventoryTests

/// Pure parser tests for ``NulInventory`` (CORRECTION01 §P1-2).

open System
open System.Text
open Expecto

open Circus.Tooling.SourcePolicy.NulInventory

let private nul: byte = byte 0
let private ascii (s: string) : byte[] = Encoding.ASCII.GetBytes s
let private utf8 (s: string) : byte[] = Encoding.UTF8.GetBytes s

[<Tests>]
let tests =
    testList
        "NUL parser"
        [ test "empty byte array is the empty inventory" {
              match parse "git-test" [||] with
              | Ok paths -> Expect.isEmpty paths "empty bytes -> empty inventory"
              | Error d -> failtestf "unexpected error: %s" (renderDiagnostic d)
          }

          test "single NUL is the empty inventory" {
              match parse "git-test" [| nul |] with
              | Ok paths -> Expect.isEmpty paths "single NUL -> empty"
              | Error d -> failtestf "unexpected error: %s" (renderDiagnostic d)
          }

          test "single ordinary path round-trips" {
              let bytes = Array.append (ascii "src/foo.fs") [| nul |]

              match parse "git-test" bytes with
              | Ok [ "src/foo.fs" ] -> ()
              | Ok xs -> failtestf "unexpected: %A" xs
              | Error d -> failtestf "unexpected error: %s" (renderDiagnostic d)
          }

          test "multiple ordinary paths round-trip" {
              let bytes =
                  Array.concat [ ascii "a.fs"; [| nul |]; ascii "b.fs"; [| nul |]; ascii "c.fs"; [| nul |] ]

              match parse "git-test" bytes with
              | Ok xs -> Expect.equal xs [ "a.fs"; "b.fs"; "c.fs" ] "three paths"
              | Error d -> failtestf "unexpected error: %s" (renderDiagnostic d)
          }

          test "path containing a space round-trips" {
              let bytes = Array.append (ascii "src/has space.fs") [| nul |]

              match parse "git-test" bytes with
              | Ok [ "src/has space.fs" ] -> ()
              | Ok xs -> failtestf "unexpected: %A" xs
              | Error d -> failtestf "unexpected error: %s" (renderDiagnostic d)
          }

          test "path containing a tab round-trips" {
              let bytes = Array.append (ascii "src/has\ttab.fs") [| nul |]

              match parse "git-test" bytes with
              | Ok [ "src/has\ttab.fs" ] -> ()
              | Ok xs -> failtestf "unexpected: %A" xs
              | Error d -> failtestf "unexpected error: %s" (renderDiagnostic d)
          }

          test "path containing an embedded newline round-trips" {
              let bytes = Array.append (ascii "src/has\nnewline.fs") [| nul |]

              match parse "git-test" bytes with
              | Ok [ "src/has\nnewline.fs" ] -> ()
              | Ok xs -> failtestf "unexpected: %A" xs
              | Error d -> failtestf "unexpected error: %s" (renderDiagnostic d)
          }

          test "path containing quotes and backslashes round-trips" {
              let bytes = Array.append (ascii "src/\"weird\\path\".fs") [| nul |]

              match parse "git-test" bytes with
              | Ok [ p ] -> Expect.equal p "src/\"weird\\path\".fs" "quotes/backslashes"
              | Ok xs -> failtestf "unexpected: %A" xs
              | Error d -> failtestf "unexpected error: %s" (renderDiagnostic d)
          }

          test "path starting with a dash round-trips" {
              let bytes = Array.append (ascii "-rf") [| nul |]

              match parse "git-test" bytes with
              | Ok [ "-rf" ] -> ()
              | Ok xs -> failtestf "unexpected: %A" xs
              | Error d -> failtestf "unexpected error: %s" (renderDiagnostic d)
          }

          test "unicode path round-trips" {
              let bytes = Array.append (utf8 "путь/файл.fs") [| nul |]

              match parse "git-test" bytes with
              | Ok [ "путь/файл.fs" ] -> ()
              | Ok xs -> failtestf "unexpected: %A" xs
              | Error d -> failtestf "unexpected error: %s" (renderDiagnostic d)
          }

          test "trailing NUL after a single path is consumed silently" {
              let bytes = Array.concat [ ascii "a.fs"; [| nul |]; ascii "b.fs"; [| nul |] ]

              match parse "git-test" bytes with
              | Ok xs -> Expect.equal xs [ "a.fs"; "b.fs" ] "two paths"
              | Error d -> failtestf "unexpected error: %s" (renderDiagnostic d)
          }

          test "consecutive NUL records between real records are rejected" {
              let bytes = Array.concat [ ascii "a.fs"; [| nul; nul |]; ascii "b.fs"; [| nul |] ]

              match parse "git-test" bytes with
              | Ok xs -> failtestf "should have failed: %A" xs
              | Error d -> Expect.stringContains (renderDiagnostic d) "empty_interior_record" "interior NUL rejected"
          }

          test "invalid UTF-8 byte sequence fails closed" {
              let bytes = Array.append [| byte 0xFFuy; byte 0xFEuy |] [| nul |]

              match parse "git-test" bytes with
              | Ok xs -> failtestf "should have failed: %A" xs
              | Error d ->
                  let rendered = renderDiagnostic d
                  Expect.stringContains rendered "command=git-test" "command identity"
                  Expect.stringContains rendered "invalid_utf8" "category"
                  Expect.stringContains rendered "byte=0xff" "actual offending byte reported"
          }

          test "invalid UTF-8 in second record reports the correct offset" {
              // "a.fs" + NUL = 5 bytes; second record's invalid byte at
              // global offset 5 (the start of the second record).
              let prefix = Array.append (ascii "a.fs") [| nul |]
              let bad = Array.append [| byte 0xC3uy; byte 0x28uy |] [| nul |]
              let bytes = Array.append prefix bad

              match parse "git-test" bytes with
              | Ok xs -> failtestf "should have failed: %A" xs
              | Error d ->
                  let rendered = renderDiagnostic d
                  Expect.stringContains rendered "invalid_utf8" "category"
                  Expect.stringContains rendered "offset=5" "byte offset of second record's invalid byte"
                  Expect.stringContains rendered "record=1" "second record index"
          }

          test "unterminated final record fails closed" {
              let bytes = ascii "orphan-no-nul"

              match parse "git-test" bytes with
              | Ok xs -> failtestf "should have failed: %A" xs
              | Error d -> Expect.stringContains (renderDiagnostic d) "unterminated_final_record" "category"
          }

          test "large record round-trips" {
              let body = String.init 10000 (fun i -> "x")
              let bytes = Array.append (ascii body) [| nul |]

              match parse "git-test" bytes with
              | Ok [ p ] -> Expect.equal p body "ten thousand x's"
              | Ok xs -> failtestf "unexpected: %A" xs
              | Error d -> failtestf "unexpected error: %s" (renderDiagnostic d)
          }

          test "diagnostic is sanitised" {
              let bytes = Array.append [| byte 0xC2uy; byte 0x00uy; byte 0x41uy |] [| nul |]

              match parse "git-test" bytes with
              | Ok xs -> failtestf "should have failed: %A" xs
              | Error d ->
                  let rendered = renderDiagnostic d
                  Expect.isFalse (rendered.Contains "\u0000") "no raw NUL leaks into the diagnostic"
                  Expect.stringContains rendered "category=" "category prefix present"
          } ]
