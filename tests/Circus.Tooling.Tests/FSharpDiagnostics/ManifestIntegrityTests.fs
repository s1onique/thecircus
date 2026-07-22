module Circus.Tooling.Tests.FSharpDiagnostics.ManifestIntegrityTests

open Expecto
open System.IO
open Circus.Tooling.FSharpDiagnostics.AtomicPublish
open Circus.Tooling.FSharpDiagnostics.Domain
open Circus.Tooling.FSharpDiagnostics.Hashing
open Circus.Tooling.FSharpDiagnostics.Manifest
open Circus.Tooling.FSharpDiagnostics.Paths
open Circus.Tooling.FSharpDiagnostics.Verifier

let private newTempDir () =
    let dir =
        Path.Combine(Path.GetTempPath(), "fd-manifest-" + System.Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory dir |> ignore
    dir

let private cleanup (directory: string) =
    if Directory.Exists directory then
        try
            Directory.Delete(directory, true)
        with _ ->
            ()

let private canonicalCorpus (root: string) =
    Path.Combine(root, canonicalRootRelative)

/// Join a repository-relative canonical path with the test repository root.
let private repoPath (root: string) (repositoryRelative: string) = Path.Combine(root, repositoryRelative)

/// Join a canonical-corpus-relative path with the test corpus root.
let private corpusPath (root: string) (corpusRelative: string) =
    Path.Combine(canonicalCorpus root, corpusRelative)

/// Every file expected in the minimal canonical corpus, expressed only in the
/// canonical-corpus-relative path domain.
let private expectedCorpusRelativePaths: string list =
    [ artifactsManifestCorpusRelativePath
      summaryCorpusRelativePath
      duplicatesCorpusRelativePath
      fingerprintsCorpusRelativePath
      migrationMapCorpusRelativePath
      occurrencesCorpusRelativePath
      "schemas/placeholder.schema.json" ]

let private readRequiredBytes (path: string) (description: string) =
    Expect.isTrue (File.Exists path) (sprintf "%s must exist at %s" description path)
    File.ReadAllBytes path

let private selfReferenceAssertionPasses (entries: ArtifactManifestEntry list) =
    entries
    |> List.forall (fun entry -> entry.CanonicalPath <> artifactsManifestCanonicalPath)

let private writeMinimalCorpus (root: string) =
    let canonical = canonicalCorpus root
    let schemas = Path.Combine(canonical, "schemas")
    let normalized = Path.Combine(canonical, normalizedCorpusRelativeSubdir)
    let manifests = Path.Combine(canonical, "corpus/manifests")
    Directory.CreateDirectory schemas |> ignore
    Directory.CreateDirectory normalized |> ignore
    Directory.CreateDirectory manifests |> ignore
    File.WriteAllText(Path.Combine(schemas, "placeholder.schema.json"), "{}")
    canonical

[<Tests>]
let tests =
    testList
        "FSharpDiagnostics.ManifestIntegrity"
        [ test "buildArtifactManifestEntries excludes artifacts-v1.jsonl" {
              let root = newTempDir ()

              try
                  let _ = writeMinimalCorpus root

                  let normalizedDir =
                      Path.Combine(canonicalCorpus root, normalizedCorpusRelativeSubdir)

                  // Seed the canonical location so the digest presented to the
                  // production builder contains the manifest's own path.
                  File.WriteAllText(Path.Combine(normalizedDir, artifactsManifestFile), "stale manifest candidate\n")

                  let _, outcome, _, _ = runPipeline root (Some normalizedDir)
                  Expect.isTrue outcome.Success "publish succeeded"

                  let manifestPath = Path.Combine(normalizedDir, artifactsManifestFile)
                  let entries = readArtifactManifestEntries manifestPath
                  Expect.isGreaterThan (List.length entries) 0 "parsed manifest is non-empty"

                  Expect.isTrue
                      (selfReferenceAssertionPasses entries)
                      "manifest must not inventory its repository-relative canonical path"

                  // Mutation regression: insert the exact prohibited path into
                  // the parsed manifest and prove the same assertion rejects it.
                  let prohibitedPath =
                      "factory/evidence/fsharp-diagnostics/corpus/normalized/artifacts-v1.jsonl"

                  let sourceEntry = List.head entries

                  let mutatedEntries =
                      { sourceEntry with
                          CanonicalPath = prohibitedPath
                          OriginalPath = prohibitedPath }
                      :: entries

                  Expect.isFalse
                      (selfReferenceAssertionPasses mutatedEntries)
                      "self-reference mutation must make the assertion fail"

                  // The two zero-record outputs have one unambiguous wire
                  // representation: exactly one LF byte (not an empty file).
                  let expectedZeroRecordBytes = [| 0x0Auy |]

                  let occurrenceBytes =
                      readRequiredBytes (Path.Combine(normalizedDir, occurrencesFile)) "zero-record occurrences output"

                  let migrationMapBytes =
                      readRequiredBytes
                          (Path.Combine(normalizedDir, migrationMapFile))
                          "zero-record migration-map output"

                  Expect.equal
                      occurrenceBytes
                      expectedZeroRecordBytes
                      "zero-record occurrences output is exactly one LF"

                  Expect.equal
                      migrationMapBytes
                      expectedZeroRecordBytes
                      "zero-record migration-map output is exactly one LF"
              finally
                  cleanup root
          }
          test "every manifest byte_length equals FileInfo.Length" {
              let root = newTempDir ()

              try
                  let _ = writeMinimalCorpus root

                  let normalizedDir =
                      Path.Combine(canonicalCorpus root, normalizedCorpusRelativeSubdir)

                  let _, outcome, _, _ = runPipeline root (Some normalizedDir)
                  Expect.isTrue outcome.Success "publish succeeded"
                  let manifestPath = Path.Combine(normalizedDir, artifactsManifestFile)
                  Expect.isTrue (File.Exists manifestPath) "manifest exists"
                  let entries = readArtifactManifestEntries manifestPath
                  Expect.isGreaterThan (List.length entries) 0 "manifest non-empty"

                  for entry in entries do
                      let fullPath = repoPath root entry.CanonicalPath

                      Expect.isTrue
                          (File.Exists fullPath)
                          (sprintf "manifest path must resolve: %s" entry.CanonicalPath)

                      let actualLength = (FileInfo fullPath).Length

                      Expect.equal
                          actualLength
                          entry.ByteLength
                          (sprintf "byte_length match for %s" entry.CanonicalPath)
              finally
                  cleanup root
          }
          test "every manifest sha256 equals independently computed SHA-256" {
              let root = newTempDir ()

              try
                  let _ = writeMinimalCorpus root

                  let normalizedDir =
                      Path.Combine(canonicalCorpus root, normalizedCorpusRelativeSubdir)

                  let _, outcome, _, _ = runPipeline root (Some normalizedDir)
                  Expect.isTrue outcome.Success "publish succeeded"
                  let manifestPath = Path.Combine(normalizedDir, artifactsManifestFile)
                  let entries = readArtifactManifestEntries manifestPath
                  Expect.isGreaterThan (List.length entries) 0 "manifest non-empty"

                  for entry in entries do
                      let fullPath = repoPath root entry.CanonicalPath

                      Expect.isTrue
                          (File.Exists fullPath)
                          (sprintf "manifest path must resolve: %s" entry.CanonicalPath)

                      let actualHash = sha256OfFile fullPath

                      Expect.equal actualHash entry.Sha256 (sprintf "sha256 match for %s" entry.CanonicalPath)
              finally
                  cleanup root
          }
          test "summary hash in manifest matches committed summary bytes" {
              let root = newTempDir ()

              try
                  let _ = writeMinimalCorpus root

                  let normalizedDir =
                      Path.Combine(canonicalCorpus root, normalizedCorpusRelativeSubdir)

                  let _, outcome, _, _ = runPipeline root (Some normalizedDir)
                  Expect.isTrue outcome.Success "publish succeeded"
                  let manifestPath = Path.Combine(normalizedDir, artifactsManifestFile)
                  let entries = readArtifactManifestEntries manifestPath

                  let summaryEntry =
                      entries
                      |> List.tryFind (fun entry -> entry.CanonicalPath = summaryCanonicalPath)

                  let availablePaths =
                      entries |> List.map (fun entry -> entry.CanonicalPath) |> String.concat "\n"

                  Expect.isSome
                      summaryEntry
                      (sprintf
                          "summary must be in manifest at %s\navailable canonical paths:\n%s"
                          summaryCanonicalPath
                          availablePaths)

                  let entry = Option.get summaryEntry
                  let summaryPath = repoPath root entry.CanonicalPath

                  Expect.isTrue
                      (File.Exists summaryPath)
                      (sprintf "summary manifest path must resolve: %s" entry.CanonicalPath)

                  let expectedHash = sha256OfFile summaryPath
                  Expect.equal expectedHash entry.Sha256 "summary sha256"
              finally
                  cleanup root
          }
          test "two complete regenerations produce byte-identical canonical snapshots" {
              let root1 = newTempDir ()
              let root2 = newTempDir ()

              try
                  let canonical1 = writeMinimalCorpus root1
                  let canonical2 = writeMinimalCorpus root2
                  let normalized1 = Path.Combine(canonical1, normalizedCorpusRelativeSubdir)
                  let normalized2 = Path.Combine(canonical2, normalizedCorpusRelativeSubdir)
                  let _, outcome1, _, _ = runPipeline root1 (Some normalized1)
                  let _, outcome2, _, _ = runPipeline root2 (Some normalized2)
                  Expect.isTrue outcome1.Success "run 1 succeeded"
                  Expect.isTrue outcome2.Success "run 2 succeeded"

                  let enumerateCorpusRelativePaths (canonical: string) =
                      Directory.GetFiles(canonical, "*", SearchOption.AllDirectories)
                      |> Array.map (fun path -> Path.GetRelativePath(canonical, path).Replace('\\', '/'))
                      |> Array.toList
                      |> List.sort

                  let files1 = enumerateCorpusRelativePaths canonical1
                  let files2 = enumerateCorpusRelativePaths canonical2
                  let expectedPaths = List.sort expectedCorpusRelativePaths
                  Expect.equal files1 expectedPaths "run 1 canonical file set is exact"
                  Expect.equal files2 expectedPaths "run 2 canonical file set is exact"
                  Expect.equal files1 files2 "independent canonical file sets match"

                  for corpusRelative in files1 do
                      let path1 = Path.Combine(canonical1, corpusRelative)
                      let path2 = Path.Combine(canonical2, corpusRelative)
                      let bytes1 = readRequiredBytes path1 (sprintf "run 1 file %s" corpusRelative)
                      let bytes2 = readRequiredBytes path2 (sprintf "run 2 file %s" corpusRelative)

                      Expect.equal bytes1 bytes2 (sprintf "independent bytes match for %s" corpusRelative)
              finally
                  cleanup root1
                  cleanup root2
          }
          test "failed publication preserves the prior canonical generation" {
              let root = newTempDir ()

              try
                  let _ = writeMinimalCorpus root

                  let normalizedDir =
                      Path.Combine(canonicalCorpus root, normalizedCorpusRelativeSubdir)

                  let _, firstOutcome, _, _ = runPipeline root (Some normalizedDir)
                  Expect.isTrue firstOutcome.Success "first publish succeeded"

                  let initialFiles =
                      expectedCorpusRelativePaths
                      |> List.map (fun corpusRelative ->
                          let path = corpusPath root corpusRelative

                          corpusRelative, readRequiredBytes path (sprintf "initial file %s" corpusRelative))

                  let badTarget = Path.Combine(root, "no-such-dir", "no-such-subdir")

                  let failedOutcome =
                      publish
                          badTarget
                          true
                          false
                          [ { CanonicalFileName = "x.txt"
                              Body = "should-not-be-written\n" } ]

                  Expect.isFalse failedOutcome.Success "second publish must fail"

                  Expect.isTrue
                      failedOutcome.CanonicalByteIdenticalAfterFailure
                      "publisher reports prior canonical bytes preserved"

                  for corpusRelative, before in initialFiles do
                      let path = corpusPath root corpusRelative

                      let after =
                          readRequiredBytes path (sprintf "file after failed publish %s" corpusRelative)

                      Expect.equal after before (sprintf "bytes preserved after failure for %s" corpusRelative)
              finally
                  cleanup root
          } ]
