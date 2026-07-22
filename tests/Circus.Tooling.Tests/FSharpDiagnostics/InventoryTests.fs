module Circus.Tooling.Tests.FSharpDiagnostics.InventoryTests

open Expecto
open System.IO
open Circus.Tooling.FSharpDiagnostics.Domain
open Circus.Tooling.FSharpDiagnostics.Hashing
open Circus.Tooling.FSharpDiagnostics.Inventory
open Circus.Tooling.FSharpDiagnostics.Paths

let private makeRoot () =
    let root = Path.Combine(Path.GetTempPath(), "fd-inv-" + System.Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory root |> ignore
    root

let private cleanup (d: string) =
    if Directory.Exists d then Directory.Delete(d, true)

[<Tests>]
let tests =
    testList "FSharpDiagnostics.Inventory" [
      test "authorityFor canonical root returns canonical_corpus" {
        Expect.equal (authorityFor "factory/evidence/fsharp-diagnostics/x") CanonicalCorpus "canonical"
        Expect.equal (authorityFor ".factory/x") NonAuthoritativeScratch "scratch"
        Expect.equal (authorityFor "src/foo.fs") Unclassified "unclassified"
      }
      test "classifyCanonicalPath classifies correctly" {
        Expect.equal (classifyCanonicalPath "factory/evidence/fsharp-diagnostics/schemas/x.schema.json")
            Derived "schema is derived"
        Expect.equal (classifyCanonicalPath "factory/evidence/fsharp-diagnostics/corpus/raw/cap-1/x.binlog")
            Raw "raw"
        Expect.equal (classifyCanonicalPath "factory/evidence/fsharp-diagnostics/corpus/normalized/occurrences-v1.jsonl")
            Normalized "normalized"
        Expect.equal (classifyCanonicalPath "factory/evidence/fsharp-diagnostics/corpus/manifests/artifacts-v1.jsonl")
            Normalized "manifests normalized"
        Expect.equal (classifyCanonicalPath "factory/evidence/fsharp-diagnostics/fixtures/fsb-0022/x.log")
            SourceSnapshot "fixture snapshot"
      }
      test "mediaTypeFor recognises binlog extension" {
        Expect.equal (mediaTypeFor "x.binlog") "application/x-msbuild-binarylog" "binlog"
        Expect.equal (mediaTypeFor "x.json") "application/json" "json"
        Expect.equal (mediaTypeFor "x.tsv") "text/tab-separated-values" "tsv"
        Expect.equal (mediaTypeFor "x.fs") "text/x-fsharp" "fs"
      }
      test "enumerateCanonical hashes files" {
        let root = makeRoot()
        try
            let canonical = Path.Combine(root, "factory/evidence/fsharp-diagnostics")
            Directory.CreateDirectory(canonical) |> ignore
            let body = "hello, world!\n"
            File.WriteAllText(Path.Combine(canonical, "a.txt"), body)
            File.WriteAllText(Path.Combine(canonical, "b.txt"), body)
            let discovered = enumerateCanonical root
            Expect.equal (List.length discovered) 2 "two files"
            let a = discovered |> List.find (fun d -> d.RelativePath.EndsWith "a.txt")
            let b = discovered |> List.find (fun d -> d.RelativePath.EndsWith "b.txt")
            Expect.equal a.Sha256 b.Sha256 "same content same hash"
            Expect.equal a.ByteLength b.ByteLength "same length"
        finally
            cleanup root
      }
      test "enumeration ignores .factory scratch root" {
        let root = makeRoot()
        try
            // The enumeration under .factory should not include factory canonical.
            let canonical = Path.Combine(root, "factory/evidence/fsharp-diagnostics")
            Directory.CreateDirectory canonical |> ignore
            File.WriteAllText(Path.Combine(canonical, "a.txt"), "x")
            let canonicalDiscovered = enumerateCanonical root
            let factoryDiscovered = enumerateFactoryScratch root
            // canonical doesn't have anything under .factory so it's empty
            Expect.equal (List.length canonicalDiscovered) 1 "one canonical"
            Expect.equal (List.length factoryDiscovered) 0 "no factory"
        finally
            cleanup root
      }
    ]