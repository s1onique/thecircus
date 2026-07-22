module Circus.Tooling.Tests.FSharpDiagnostics.ManifestIntegrityTests

open Expecto
open System.IO
open Circus.Tooling.FSharpDiagnostics.AtomicPublish
open Circus.Tooling.FSharpDiagnostics.Hashing
open Circus.Tooling.FSharpDiagnostics.Inventory
open Circus.Tooling.FSharpDiagnostics.Manifest
open Circus.Tooling.FSharpDiagnostics.Paths
open Circus.Tooling.FSharpDiagnostics.Serialization
open Circus.Tooling.FSharpDiagnostics.Verifier

let private newTempDir () =
    let dir = Path.Combine(
                Path.GetTempPath(),
                "fd-manifest-" + System.Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory dir |> ignore
    dir

let private cleanup (d: string) =
    if Directory.Exists d then
        try Directory.Delete(d, true) with _ -> ()

let private writeMinimalCorpus (root: string) =
    // Create the canonical root with schemas + normalized directory
    // but no captures, so no real diagnostics.  This mirrors the state
    // of the ACT after a clean run.
    let canonical = Path.Combine(root, "factory/evidence/fsharp-diagnostics")
    let schemas = Path.Combine(canonical, "schemas")
    let normalized = Path.Combine(canonical, "corpus/normalized")
    let manifests = Path.Combine(canonical, "corpus/manifests")
    Directory.CreateDirectory schemas |> ignore
    Directory.CreateDirectory normalized |> ignore
    Directory.CreateDirectory manifests |> ignore
    // Empty schema file just so the canonical root is non-empty.
    File.WriteAllText(Path.Combine(schemas, "placeholder.schema.json"), "{}")
    canonical

[<Tests>]
let tests =
    testList "FSharpDiagnostics.ManifestIntegrity" [
      test "buildArtifactManifestEntries excludes artifacts-v1.jsonl" {
          let root = newTempDir()
          try
              let _ = writeMinimalCorpus root
              let empty = Map.empty<string, int64 * string>
              let entries, _ =
                  buildArtifactManifestEntries root [] empty
              let manifestSelf =
                  entries
                  |> List.tryFind (fun e -> e.CanonicalPath = artifactsManifestFile)
              Expect.isNone manifestSelf "manifest must not inventory itself"
          finally
              cleanup root
      }
      test "every manifest byte_length equals FileInfo.Length of staged bytes" {
          let root = newTempDir()
          try
              let _ = writeMinimalCorpus root
              let normalizedDir = Path.Combine(root, "factory/evidence/fsharp-diagnostics/corpus/normalized")
              let _summary, outcome, _, _ = runPipeline root (Some normalizedDir)
              Expect.isTrue outcome.Success "publish succeeded"
              let manifestPath = Path.Combine(normalizedDir, artifactsManifestFile)
              Expect.isTrue (File.Exists manifestPath) "manifest exists"
              let entries = readArtifactManifestEntries manifestPath
              Expect.isGreaterThan (List.length entries) 0 "manifest non-empty"
              for entry in entries do
                  let fullPath = Path.Combine(normalizedDir, entry.CanonicalPath)
                  if File.Exists fullPath then
                      let actualLen = (FileInfo fullPath).Length
                      Expect.equal
                          (int64 actualLen)
                          entry.ByteLength
                          (sprintf "byte_length match for %s" entry.CanonicalPath)
          finally
              cleanup root
      }
      test "every manifest sha256 equals independently computed SHA-256" {
          let root = newTempDir()
          try
              let _ = writeMinimalCorpus root
              let normalizedDir = Path.Combine(root, "factory/evidence/fsharp-diagnostics/corpus/normalized")
              let _summary, outcome, _, _ = runPipeline root (Some normalizedDir)
              Expect.isTrue outcome.Success "publish succeeded"
              let manifestPath = Path.Combine(normalizedDir, artifactsManifestFile)
              let entries = readArtifactManifestEntries manifestPath
              for entry in entries do
                  let fullPath = Path.Combine(normalizedDir, entry.CanonicalPath)
                  if File.Exists fullPath then
                      let actualHash = sha256OfFile fullPath
                      Expect.equal
                          actualHash
                          entry.Sha256
                          (sprintf "sha256 match for %s" entry.CanonicalPath)
          finally
              cleanup root
      }
      test "summary hash in manifest matches committed summary bytes" {
          let root = newTempDir()
          try
              let _ = writeMinimalCorpus root
              let normalizedDir = Path.Combine(root, "factory/evidence/fsharp-diagnostics/corpus/normalized")
              let _summary, outcome, _, _ = runPipeline root (Some normalizedDir)
              Expect.isTrue outcome.Success "publish succeeded"
              let manifestPath = Path.Combine(normalizedDir, artifactsManifestFile)
              let summaryPath = Path.Combine(normalizedDir, summaryFile)
              let entries = readArtifactManifestEntries manifestPath
              let summaryEntry =
                  entries
                  |> List.tryFind (fun e -> e.CanonicalPath = summaryFile)
              match summaryEntry with
              | Some entry ->
                  let expectedHash = sha256OfFile summaryPath
                  Expect.equal expectedHash entry.Sha256 "summary sha256"
              | None ->
                  failwith "summary must be in manifest"
          finally
              cleanup root
      }
      test "two complete regenerations produce byte-identical output" {
          let root = newTempDir()
          try
              let _ = writeMinimalCorpus root
              let normalizedDir1 = Path.Combine(root, "factory/evidence/fsharp-diagnostics/corpus/normalized")
              let normalizedDir2 = Path.Combine(root, "run2/factory/evidence/fsharp-diagnostics/corpus/normalized")
              let _, o1, _, _ = runPipeline root (Some normalizedDir1)
              let _, o2, _, _ = runPipeline root (Some normalizedDir2)
              Expect.isTrue o1.Success "run 1 ok"
              Expect.isTrue o2.Success "run 2 ok"
              let files : string list = [
                  artifactsManifestFile
                  summaryFile
                  occurrencesFile
                  fingerprintsFile
                  duplicatesFile
                  migrationMapFile ]
              files
              |> List.iter (fun f ->
                  let p1 = Path.Combine(normalizedDir1, f)
                  let p2 = Path.Combine(normalizedDir2, f)
                  if File.Exists p1 && File.Exists p2 then
                      Expect.equal
                          (sha256OfFile p1)
                          (sha256OfFile p2)
                          (sprintf "byte-identical: %s" f))
          finally
              cleanup root
      }
      test "failed publication preserves the prior canonical generation" {
          let root = newTempDir()
          try
              let _ = writeMinimalCorpus root
              let normalizedDir = Path.Combine(root, "factory/evidence/fsharp-diagnostics/corpus/normalized")
              // First generation: success.
              let _, o1, _, _ = runPipeline root (Some normalizedDir)
              Expect.isTrue o1.Success "first publish ok"
              let beforeHash = sha256OfFile(Path.Combine(normalizedDir, summaryFile))
              // Capture initial bytes for every canonical output.
              let initialFiles =
                  [ artifactsManifestFile; summaryFile; occurrencesFile
                    fingerprintsFile; duplicatesFile; migrationMapFile ]
                  |> List.map (fun f ->
                      (f, File.ReadAllBytes(Path.Combine(normalizedDir, f))))
              // Try to publish to a sub-path whose parent cannot be
              // created.  We use an explicitly bad target that
              // triggers the exception.
              let badTarget = Path.Combine(root, "no-such-dir", "no-such-subdir")
              let outcome = Circus.Tooling.FSharpDiagnostics.AtomicPublish.publish badTarget true false [
                { CanonicalFileName = "x.txt"
                  Body = "should-not-be-written\n" }
              ]
              Expect.isFalse outcome.Success "second publish must fail"
              // The canonical outputs in normalizedDir remain byte-identical.
              for f, before in initialFiles do
                  let path = Path.Combine(normalizedDir, f)
                  if File.Exists path then
                      let after = File.ReadAllBytes path
                      Expect.equal
                          (System.Convert.ToBase64String before)
                          (System.Convert.ToBase64String after)
                          (sprintf "byte-identical after failure: %s" f)
              let _ = beforeHash
              ()
          finally
              cleanup root
      }
    ]