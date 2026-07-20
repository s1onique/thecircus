module Circus.Tooling.Tests.SourcePolicy.NulInventoryTests

/// Pure parser tests for ``NulInventory``.

open System
open System.Text
open Expecto

open Circus.Tooling.SourcePolicy.NulInventory

let private nul : byte = byte 0
let private ascii (s: string) : byte[] = Encoding.ASCII.GetBytes s
let private utf8 (s: string) : byte[] = Encoding.UTF8.GetBytes s

[<Tests>]
let tests =
    testList "NUL parser" [
        test "empty byte array is the empty inventory" {
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
                Array.concat [
                    ascii "a.fs"; [| nul |]
                    ascii "b.fs"; [| nul |]
                    ascii "c.fs"; [| nul |]
                ]
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

        test "path beginning with a dash round-trips" {
            let bytes = Array.append (ascii "-flaglike.txt") [| nul |]
            match parse "git-test" bytes with
            | Ok [ "-flaglike.txt" ] -> ()
            | Ok xs -> failtestf "unexpected: %A" xs
            | Error d -> failtestf "unexpected error: %s" (renderDiagnostic d)
        }

        test "non-ASCII Unicode path round-trips" {
            let bytes = Array.append (utf8 "src/über/naïve/π.fs") [| nul |]
            match parse "git-test" bytes with
            | Ok [ "src/über/naïve/π.fs" ] -> ()
            | Ok xs -> failtestf "unexpected: %A" xs
            | Error d -> failtestf "unexpected error: %s" (renderDiagnostic d)
        }

        test "trailing NUL is consumed silently" {
            let bytes =
                Array.concat [ ascii "a.fs"; [| nul |]; ascii "b.fs"; [| nul |] ]
            match parse "git-test" bytes with
            | Ok xs -> Expect.equal xs [ "a.fs"; "b.fs" ] "two paths"
            | Error d -> failtestf "unexpected error: %s" (renderDiagnostic d)
        }

        test "consecutive NUL records produce no phantom empty path" {
            let bytes = Array.concat [ ascii "a.fs"; [| nul; nul |]; ascii "b.fs"; [| nul |] ]
            match parse "git-test" bytes with
            | Ok xs -> Expect.equal xs [ "a.fs"; "b.fs" ] "consecutive NUL ignored"
            | Error d -> failtestf "unexpected error: %s" (renderDiagnostic d)
        }

        test "invalid UTF-8 byte sequence fails closed" {
            let bytes = Array.append [| byte 0xFFuy; byte 0xFEuy |] [| nul |]
            match parse "git-test" bytes with
            | Ok xs -> failtestf "should have failed: %A" xs
            | Error d ->
                let rendered = renderDiagnostic d
                Expect.stringContains rendered "command=git-test" "command identity"
                Expect.stringContains rendered "invalid_utf8" "category"
        }

        test "unterminated final record fails closed" {
            let bytes = ascii "orphan-no-nul"
            match parse "git-test" bytes with
            | Ok xs -> failtestf "should have failed: %A" xs
            | Error d ->
                Expect.stringContains (renderDiagnostic d) "unterminated_final_record" "category"
        }

        test "no NUL framing at all fails closed" {
            let bytes = ascii "no-delimiters-at-all"
            match parse "git-test" bytes with
            | Ok xs -> failtestf "should have failed: %A" xs
            | Error d ->
                Expect.stringContains (renderDiagnostic d) "unterminated_final_record" "category"
        }

        test "large record survives round-trip" {
            let big = String('x', 50000)
            let bytes = Array.append (ascii big) [| nul |]
            match parse "git-test" bytes with
            | Ok [ p ] -> Expect.equal p.Length 50000 "big record preserved"
            | Ok xs -> failtestf "unexpected: %A" xs
            | Error d -> failtestf "unexpected error: %s" (renderDiagnostic d)
        }

        test "many small records round-trip in order" {
            let sb = StringBuilder()
            for i in 0 .. 999 do
                sb.Append(sprintf "f%d.fs" i) |> ignore
                sb.Append('\000') |> ignore
            let bytes = utf8 (sb.ToString())
            match parse "git-test" bytes with
            | Ok xs ->
                Expect.equal (List.length xs) 1000 "1000 records"
                Expect.equal xs.[0] "f0.fs" "first record"
                Expect.equal xs.[999] "f999.fs" "last record"
            | Error d -> failtestf "unexpected error: %s" (renderDiagnostic d)
        }

        test "diagnostic render is sanitised (no terminal control bytes)" {
            let bytes = Array.append [| byte 0xFFuy; byte 0xFEuy |] [| nul |]
            match parse "git-test" bytes with
            | Error d ->
                let s = renderDiagnostic d
                for c in s do
                    let n = int c
                    Expect.isFalse (n < 0x20 && c <> '\n') "no control bytes in diagnostic"
            | Ok _ -> failtestf "expected decode failure"
        }
    ]