module Circus.Tooling.Tests.FSharpDiagnostics.HashingTests

open Expecto
open Circus.Tooling.FSharpDiagnostics.Hashing

[<Tests>]
let tests =
    testList "FSharpDiagnostics.Hashing" [
      test "sha256OfFile returns lowercase hex" {
        let text = "hello, world!\n"
        let path = System.IO.Path.GetTempFileName()
        System.IO.File.WriteAllText(path, text)
        try
            let hash = sha256OfFile path
            Expect.equal (hash.Length) 64 "hash is 64 chars"
            Expect.isFalse (hash.Contains " ") "no spaces"
            // known: SHA-256 of "hello, world!\n"
            Expect.equal hash "4dca0fd5f424a31b03ab807cbae77eb32bf2d089eed1cee154b3afed458de0dc" "known hash"
        finally
            System.IO.File.Delete path
      }
      test "sha256OfUtf8 is deterministic" {
        let a = sha256OfUtf8 "abc"
        let b = sha256OfUtf8 "abc"
        Expect.equal a b "deterministic"
      }
      test "sha256Hex lowercases output" {
        let bytes = System.Text.Encoding.UTF8.GetBytes "X"
        let hash = sha256Hex bytes
        let lower = hash.ToLowerInvariant()
        Expect.equal hash lower "lowercase"
      }
    ]