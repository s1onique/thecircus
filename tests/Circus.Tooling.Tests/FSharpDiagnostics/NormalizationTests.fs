module Circus.Tooling.Tests.FSharpDiagnostics.NormalizationTests

open Expecto
open Circus.Tooling.FSharpDiagnostics.Domain
open Circus.Tooling.FSharpDiagnostics.Normalization

let aliases =
    [ { AbsoluteRoot = "/home/me/project"
        CanonicalRoot = "<REPO>" } ]

[<Tests>]
let tests =
    testList
        "FSharpDiagnostics.Normalization"
        [ test "normalizeMessage replaces declared alias" {
              let text = "/home/me/project/src/Foo.fs(10,5): warning FS0001: message"
              let normalized = normalizeMessage aliases text
              Expect.stringContains normalized "<REPO>" "alias replaced"
              Expect.isFalse (normalized.Contains "/home/me/project") "no absolute root"
          }
          test "normalizeMessage preserves unknown absolute paths" {
              let text = "/other/path/foo.fs(10,5): error FS0001: x"
              let normalized = normalizeMessage aliases text
              Expect.equal text normalized "unchanged"
          }
          test "normalizeMessage converts CRLF to LF" {
              let text = "line1\r\nline2\r\nline3"
              let normalized = normalizeMessage aliases text
              Expect.equal normalized "line1\nline2\nline3" "CRLF converted"
          }
          test "normalizeMessage does NOT lowercase" {
              let text = "WARNING FS0042: PascalCase preserved"
              let normalized = normalizeMessage aliases text
              Expect.stringContains normalized "PascalCase" "case preserved"
          }
          test "normalizeMessage does NOT trim or remove numbers" {
              let text = "Type 'List<int>' at line 42 contains 3 elements"
              let normalized = normalizeMessage aliases text
              Expect.stringContains normalized "42" "numbers preserved"
              Expect.stringContains normalized "3" "numbers preserved"
              Expect.stringContains normalized "List<int>'" "generics preserved"
          }
          test "normalizeMessage converts backslashes to forward slashes" {
              let text = "see C:\\Users\\me\\Foo.fs"
              let normalized = normalizeMessage aliases text
              Expect.stringContains normalized "C:/Users/me/Foo.fs" "slashes converted"
          }
          test "matchesDeclaredAlias requires exact prefix" {
              Expect.isTrue (matchesDeclaredAlias aliases "/home/me/project/src/Foo.fs") "exact match"
              Expect.isTrue (matchesDeclaredAlias aliases "/home/me/project") "exact root"
              Expect.isFalse (matchesDeclaredAlias aliases "/home/me/project-other/x") "no prefix collision"
              Expect.isFalse (matchesDeclaredAlias aliases "/other/path/x") "different root"
          }
          test "containsUndeclaredAbsolutePath" {
              Expect.isFalse (containsUndeclaredAbsolutePath aliases "/home/me/project/Foo.fs") "alias declared"
              Expect.isTrue (containsUndeclaredAbsolutePath aliases "/other/abs/path") "undeclared"
              Expect.isFalse (containsUndeclaredAbsolutePath aliases "relative/path") "not absolute"
          } ]
