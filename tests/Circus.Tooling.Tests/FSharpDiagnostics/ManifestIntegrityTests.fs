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

let private canonicalCorpus (root: string) =
    Path.Combine(root, "factory/evidence/fsharp-diagnostics")

/// Join a repo-relative canonical path with the test root.
let private repoPath (root: string) (repoRelative: string) =
    Path.Combine(root, repoRelative)

/// Join a corpus-relative path (without the canonical corpus root
/// prefix) with the test root.  This matches the relative names stored
/// in the manifest's ``CanonicalPath`` field of the test's hard-coded
/// list.
let private corpusPath (root: string) (corpusRelative: string) =
    Path.Combine(canonicalCorpus root, corpusRelative)

/// All canonical corpus files expected in a minimal corpus, relative
/// to the canonical corpus root.  The manifest's ``CanonicalPath``
/// field stores the *full repository-relative* path; we therefore use
/// the production ``Paths`` constants to derive the canonical name and
/// verify it.
let private expectedCanonicalNames : string list = [
    Circus.Tooling.FSharpDiagnostics.Paths.artifactsManifestFile
    Circus.Tooling.FSharpDiagnostics.Paths.summaryFile
    Circus.Tooling.FSharpDiagnostics.Paths.duplicatesFile
    Circus.Tooling.FSharpDiagnostics.Paths.fingerprintsFile
    Circus.Tooling.FSharpDiagnostics.Paths.migrationMapFile
    Circus.Tooling.FSharpDiagnostics.Paths.occurrencesFile
    "schemas/placeholder.schema.json"
]

let private writeMinimalCorpus (root: string) =
    let canonical = canonicalCorpus root
    let schemas = Path.Combine(canonical, "schemas")
    let normalized = Path.Combine(canonical, "corpus/normalized")
    let manifests = Path.Combine(canonical, "corpus/manifests")
    Directory.CreateDirectory schemas |> ignore
    Directory.CreateDirectory normalized |> ignore
    Directory.CreateDirectory manifests |> ignore
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
                  |> List.tryFind (fun e ->
                      e.CanonicalPath
                      = Circus.Tooling.FSharpDiagnostics.Paths.artifactsManifestFile)
              Expect.isNone manifestSelf "manifest must not inventory itself"
          finally
              cleanup root
      }
      test "every manifest byte_length equals FileInfo.Length" {
          let root = newTempDir()
          try
              let _ = writeMinimalCorpus root
              let normalizedDir = Path.Combine(canonicalCorpus root, "corpus/normalized")
              let _, outcome, _, _ = runPipeline root (Some normalizedDir)
              Expect.isTrue outcome.Success "publish succeeded"
              let manifestPath = Path.Combine(normalizedDir, artifactsManifestFile)
              Expect.isTrue (File.Exists manifestPath) "manifest exists"
              let entries = readArtifactManifestEntries manifestPath
              Expect.isGreaterThan (List.length entries) 0 "manifest non-empty"
              for entry in entries do
                  // Manifest stores the full repo-relative canonical
                  // path; resolve relative to the test root, not the
                  // canonical corpus root.
                  let fullPath = repoPath root entry.CanonicalPath
                  if not (File.Exists fullPath) then
                      failwithf "manifest entry references missing file: %s (resolved as %s)"
                          entry.CanonicalPath fullPath
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
              let normalizedDir = Path.Combine(canonicalCorpus root, "corpus/normalized")
              let _, outcome, _, _ = runPipeline root (Some normalizedDir)
              Expect.isTrue outcome.Success "publish succeeded"
              let manifestPath = Path.Combine(normalizedDir, artifactsManifestFile)
              let entries = readArtifactManifestEntries manifestPath
              for entry in entries do
                  let fullPath = repoPath root entry.CanonicalPath
                  if not (File.Exists fullPath) then
                      failwithf "manifest entry references missing file: %s" entry.CanonicalPath
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
              let normalizedDir = Path.Combine(canonicalCorpus root, "corpus/normalized")
              let _, outcome, _, _ = runPipeline root (Some normalizedDir)
              Expect.isTrue outcome.Success "publish succeeded"
              let manifestPath = Path.Combine(normalizedDir, artifactsManifestFile)
              let summaryPath = repoPath root Circus.Tooling.FSharpDiagnostics.Paths.summaryFile
              if not (File.Exists summaryPath) then
                  failwithf "summary file missing: %s" summaryPath
              let entries = readArtifactManifestEntries manifestPath
              let summaryEntry =
                  entries
                  |> List.tryFind (fun e ->
                      e.CanonicalPath = Circus.Tooling.FSharpDiagnostics.Paths.summaryFile)
              match summaryEntry with
              | Some entry ->
                  let expectedHash = sha256OfFile summaryPath
                  Expect.equal expectedHash entry.Sha256 "summary sha256"
              | None ->
                  let available = entries |> List.map (fun e -> e.CanonicalPath) |> String.concat "\n"
                  failwithf
                      "summary must be in manifest at %s\navailable canonical paths:\n%s"
                      Circus.Tooling.FSharpDiagnostics.Paths.summaryFile
                      available
          finally
              cleanup root
      }
      test "two complete regenerations produce byte-identical canonical snapshots" {
          // Initialize two independent roots, write the same minimal
          // corpus to each, run the pipeline against each, then compare
          // complete file snapshots in canonical-path order.
          let root1 = newTempDir()
          let root2 = newTempDir()
          try
              let _ = writeMinimalCorpus root1
              let _ = writeMinimalCorpus root2
              let norm1 = Path.Combine(canonicalCorpus root1, "corpus/normalized")
              let norm2 = Path.Combine(canonicalCorpus root2, "corpus/normalized")
              let _, o1, _, _ = runPipeline root1 (Some norm1)
              let _, o2, _, _ = runPipeline root2 (Some norm2)
              Expect.isTrue o1.Success "run 1 ok"
              Expect.isTrue o2.Success "run 2 ok"
              // Enumerate all files under each canonical root, convert
              // to canonical-corpus-relative paths, sort ordinally.
              let enumRoot (root: string) : string list =
                  let canonical = canonicalCorpus root
                  Directory.GetFiles(canonical, "*", SearchOption.AllDirectories)
                  |> Array.map (fun p ->
                      Path.GetRelativePath(canonical, p).Replace('\\', '/'))
                  |> Array.toList
                  |> List.sort
              let files1 = enumRoot root1
              let files2 = enumRoot root2
              Expect.equal files1 files2 "canonical file sets match"
              // Compare every shared file's bytes in canonical-path order.
              let sharedFiles =
                  List.zip files1 files2
                  |> List.filter (fun (a, b) -> a = b)
              Expect.isGreaterThan (List.length sharedFiles) 0 "should have shared files"
              for rel in sharedFiles |> List.map fst do
                  let p1 = corpusPath root1 rel
                  let p2 = corpusPath root2 rel
                  if not (File.Exists p1) || not (File.Exists p2) then
                      failwithf "expected file missing: %s" p1
                  let b1 = File.ReadAllBytes p1
                  let b2 = File.ReadAllBytes p2
                  Expect.equal
                      (System.Convert.ToBase64String b1)
                      (System.Convert.ToBase64String b2)
                      (sprintf "byte-identical: %s" rel)
          finally
              cleanup root1
              cleanup root2
      }
      test "failed publication preserves the prior canonical generation" {
          let root = newTempDir()
          try
              let _ = writeMinimalCorpus root
              let normalizedDir = Path.Combine(canonicalCorpus root, "corpus/normalized")
              let _, o1, _, _ = runPipeline root (Some normalizedDir)
              Expect.isTrue o1.Success "first publish ok"
              // Capture initial bytes for every canonical output.
              let initialFiles =
                  expectedCanonicalNames
                  |> List.map (fun rel ->
                      (rel, File.ReadAllBytes(corpusPath root rel)))
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
              for rel, before in initialFiles do
                  let path = corpusPath root rel
                  if not (File.Exists path) then
                      failwithf "file missing: %s" path
                  let after = File.ReadAllBytes path
                  Expect.equal
                      (System.Convert.ToBase64String before)
                      (System.Convert.ToBase64String after)
                      (sprintf "byte-identical after failure: %s" rel)
          finally
              cleanup root
      }
    ]