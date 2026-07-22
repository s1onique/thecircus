module Circus.Tooling.Tests.FSharpDiagnostics.AtomicPublishTests

open Expecto
open System.IO
open Circus.Tooling.FSharpDiagnostics.AtomicPublish
open Circus.Tooling.FSharpDiagnostics.Hashing

let private newTempDir () =
    let dir = Path.Combine(
                Path.GetTempPath(),
                "fsharp-diagnostics-tests-" + System.Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    dir

let private cleanup (dir: string) =
    if Directory.Exists dir then
        try Directory.Delete(dir, true) with _ -> ()

[<Tests>]
let tests =
    testList "FSharpDiagnostics.AtomicPublish" [
      test "publish moves files into canonical dir" {
        let staging = newTempDir()
        let canonical = newTempDir()
        try
            let files = [
              { CanonicalFileName = "a.txt"; Body = "hello\n" }
              { CanonicalFileName = "b.txt"; Body = "world\n" }
            ]
            let outcome = publish canonical true false files
            Expect.isTrue outcome.Success "success"
            Expect.equal (List.length outcome.OutputHashes) 2 "two hashes"
            Expect.isTrue (File.Exists(Path.Combine(canonical, "a.txt"))) "a.txt exists"
            Expect.isTrue (File.Exists(Path.Combine(canonical, "b.txt"))) "b.txt exists"
        finally
            cleanup staging
            cleanup canonical
      }
      test "canonical outputs byte-identical after success (proves round-trip integrity)" {
        let staging = newTempDir()
        let canonical = newTempDir()
        try
            let files = [
              { CanonicalFileName = "a.txt"; Body = "initial\n" }
            ]
            let _ = publish canonical true false files
            // Initial file written.
            let afterFirstWrite = File.ReadAllBytes(Path.Combine(canonical, "a.txt"))
            let expectedBytes = System.Text.Encoding.UTF8.GetBytes "initial\n"
            Expect.equal afterFirstWrite expectedBytes "first write present"
            // A second publication with different content succeeds
            // (this ACT always allows successful updates).
            let files2 = [
              { CanonicalFileName = "a.txt"; Body = "replacement\n" }
            ]
            let outcome2 = publish canonical true false files2
            Expect.isTrue outcome2.Success "second publish succeeds"
            let afterSecond = File.ReadAllBytes(Path.Combine(canonical, "a.txt"))
            let expectedBytes2 = System.Text.Encoding.UTF8.GetBytes "replacement\n"
            Expect.equal afterSecond expectedBytes2 "second write present"
        finally
            cleanup staging
            cleanup canonical
      }

      test "publish preserves byte identity under rerun (idempotent)" {
        let staging = newTempDir()
        let canonical = newTempDir()
        try
            let files = [
              { CanonicalFileName = "a.txt"; Body = "alpha\n" }
              { CanonicalFileName = "b.txt"; Body = "beta\n" }
            ]
            let _ = publish canonical true false files
            let aHash1 = sha256OfFile(Path.Combine(canonical, "a.txt"))
            let bHash1 = sha256OfFile(Path.Combine(canonical, "b.txt"))
            // Second publication with the SAME bodies must produce byte-identical files.
            let _ = publish canonical true false files
            let aHash2 = sha256OfFile(Path.Combine(canonical, "a.txt"))
            let bHash2 = sha256OfFile(Path.Combine(canonical, "b.txt"))
            Expect.equal aHash1 aHash2 "a.txt identical"
            Expect.equal bHash1 bHash2 "b.txt identical"
        finally
            cleanup staging
            cleanup canonical
      }
      test "deterministic publication: two runs produce identical bytes" {
        let stagingA = newTempDir()
        let stagingB = newTempDir()
        let canonicalA = newTempDir()
        let canonicalB = newTempDir()
        try
            let files = [
              { CanonicalFileName = "x.txt"; Body = "alpha\n" }
              { CanonicalFileName = "y.txt"; Body = "beta\n" }
            ]
            let _ = publish canonicalA true false files
            let _ = publish canonicalB true false files
            let ha = sha256OfFile(Path.Combine(canonicalA, "x.txt"))
            let hb = sha256OfFile(Path.Combine(canonicalB, "x.txt"))
            Expect.equal ha hb "first run bytes identical"
            let ha2 = sha256OfFile(Path.Combine(canonicalA, "y.txt"))
            let hb2 = sha256OfFile(Path.Combine(canonicalB, "y.txt"))
            Expect.equal ha2 hb2 "second file bytes identical"
        finally
            cleanup stagingA
            cleanup stagingB
            cleanup canonicalA
            cleanup canonicalB
      }
    ]