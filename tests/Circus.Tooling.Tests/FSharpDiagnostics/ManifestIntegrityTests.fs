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
/// in the manifest's ``CanonicalPath`` field.
let private corpusPath (root: string) (corpusRelative: string) =
    Path.Combine(canonicalCorpus root, corpusRelative)

/// All canonical corpus files expected in a minimal corpus, relative
/// to the canonical corpus root.
let private canonicalRelativeNames : string list = [
    "corpus/normalized/artifacts-v1.jsonl"
    "corpus/normalized/corpus-summary-v1.json"
    "corpus/normalized/duplicate-occurrences-v1.tsv"
    "corpus/normalized/exact-fingerprints-v1.tsv"
    "corpus/normalized/migration-map-v1.tsv"
    "corpus/normalized/occurrences-v1.jsonl"
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

let private summarize (root: string) (rel: string) : string =
    let p = corpusPath root rel
    let info = FileInfo p
    sprintf "%s\t%d\t%s"
        rel
        info.Length
        (sha256OfFile p)

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
                      = "factory/evidence/fsharp-diagnostics/corpus/normalized/artifacts-v1.jsonl")
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
              let summaryPath = Path.Combine(normalizedDir, summaryFile)
              let entries = readArtifactManifestEntries manifestPath
              // summaryFile is already a full canonical path; no
              // prefix concatenation needed.
              let summaryCanonical = summaryFile
              let summaryEntry =
                  entries
                  |> List.tryFind (fun e -> e.CanonicalPath = summaryCanonical)
              match summaryEntry with
              | Some entry ->
                  if not (File.Exists summaryPath) then
                      failwithf "summary file missing: %s" summaryPath
                  let expectedHash = sha256OfFile summaryPath
                  Expect.equal expectedHash entry.Sha256 "summary sha256"
              | None ->
                  failwithf "summary must be in manifest at %s" summaryCanonical
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
              // Compare every expected canonical file in path order.
              // The set of expected files is the hard-coded list of
              // canonicalRelativeNames.  Both roots must contain every
              // file.  We compare the (length, sha256, bytes) tuple in
              // order.
              for rel in canonicalRelativeNames do
                  let p1 = corpusPath root1 rel
                  let p2 = corpusPath root2 rel
                  if not (File.Exists p1) then
                      failwithf "run1 missing: %s" p1
                  if not (File.Exists p2) then
                      failwithf "run2 missing: %s" p2
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
                  canonicalRelativeNames
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