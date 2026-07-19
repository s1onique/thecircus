module Circus.Tooling.Tests.SourcePolicy.LineCountingTests

open Expecto
open Circus.Tooling.SourcePolicy.LineCounting

let tests =
    testList "LineCounting" [
        test "empty file returns 0" {
            Expect.equal (count (System.Text.Encoding.UTF8.GetBytes "")) 0 "Empty"
        }
        test "single LF-terminated line returns 1" {
            Expect.equal (count (System.Text.Encoding.UTF8.GetBytes "a\n")) 1 "1 line"
        }
        test "single unterminated line returns 1" {
            Expect.equal (count (System.Text.Encoding.UTF8.GetBytes "a")) 1 "1 line"
        }
        test "two lines LF-terminated" {
            Expect.equal (count (System.Text.Encoding.UTF8.GetBytes "a\nb\n")) 2 "2 lines"
        }
        test "blank lines count" {
            Expect.equal (count (System.Text.Encoding.UTF8.GetBytes "a\n\n\nb\n")) 4 "blank lines count"
        }
        test "CRLF counts same as LF" {
            Expect.equal (count (System.Text.Encoding.UTF8.GetBytes "a\r\nb\r\n")) 2 "CRLF"
        }
    ]
