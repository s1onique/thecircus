module Circus.Tooling.Tests.FSharpDiagnostics.RepairEpisodes.CaptureBindingTests

// =============================================================================
// Strict capture-binding tests
// =============================================================================
//
// Tests cover the public ``bindCapture`` entry point and the internal
// strict ``readCaptureManifest`` reader.  Foundations for the test
// scaffolding are shared with the existing FSharpDiagnostics tests:
//   * actual on-disk SHA-256 computed via ``SHA256.HashData``,
//   * UTF-8 written without BOM,
//   * real temporary files and directories.

open Expecto
open System
open System.IO
open System.Security.Cryptography
open System.Text
open Circus.Tooling.FSharpDiagnostics.Domain
open Circus.Tooling.FSharpDiagnostics.Hashing
open Circus.Tooling.FSharpDiagnostics.Paths
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.CaptureBinding
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.OccurrenceReader
open Circus.Tooling.FSharpDiagnostics.Serialization

// =============================================================================
// Temporary directory helpers
// =============================================================================

let private newTempDir () =
    let dir =
        Path.Combine(
            Path.GetTempPath(),
            "fsharp-diagnostics-capture-binding-" + Guid.NewGuid().ToString("N")
        )

    Directory.CreateDirectory dir |> ignore
    dir

let private cleanup (dir: string) =
    if Directory.Exists dir then
        try
            Directory.Delete(dir, true)
        with _ ->
            ()

let private utf8NoBom : Encoding = new UTF8Encoding(false)

let private writeAllText (path: string) (text: string) =
    File.WriteAllText(path, text, utf8NoBom)

let private writeBytes (path: string) (bytes: byte[]) =
    File.WriteAllBytes(path, bytes)

// =============================================================================
// Canonical corpus helpers
// =============================================================================

let private canonicalCorpus (root: string) =
    Path.Combine(root, canonicalRootRelative)

let private ensureDir (path: string) =
    Directory.CreateDirectory path |> ignore

/// Write a complete capture manifest that includes every required field the
/// strict reader expects.  Fields that are semantically optional are
/// emitted as explicit JSON ``null`` so absent-vs-null remains testable.
let private writeMinimalCaptureManifest
    (captureDir: string)
    (captureId: string)
    (commitOid: string)
    (treeOid: string)
    (rawPaths: string list)
    : unit =
    let raw =
        rawPaths
        |> List.map escapeJsonString
        |> String.concat ","
    let text =
        "{\"schema_version\":\""
        + CaptureManifestSchemaVersion
        + "\",\"capture_id\":\""
        + captureId
        + "\",\"capture_kind\":\"binlog\""
        + ",\"raw_artifacts\":["
        + raw
        + "]"
        + ",\"command\":null"
        + ",\"working_directory\":null"
        + ",\"repository_commit_oid\":\""
        + commitOid
        + "\""
        + ",\"repository_tree_oid\":\""
        + treeOid
        + "\""
        + ",\"working_tree_state\":null"
        + ",\"source_root_aliases\":[]"
        + ",\"dotnet_sdk_version\":null"
        + ",\"msbuild_version\":null"
        + ",\"fsharp_compiler_version\":null"
        + ",\"operating_system\":null"
        + ",\"architecture\":null"
        + ",\"culture\":null"
        + ",\"started_at\":null"
        + ",\"completed_at\":null"
        + ",\"exit_code\":null"
        + ",\"metadata_gaps\":[]}"
    writeAllText (Path.Combine(captureDir, "capture.json")) text

let private writeArtifactManifest
    (root: string)
    (entries: (string * string * int64 * string * string * string) list)
    : unit =
    let normalized = Path.Combine(canonicalCorpus root, normalizedCorpusRelativeSubdir)
    ensureDir normalized
    let lines =
        entries
        |> List.map (fun (canonicalPath, artifactClass, byteLength, sha256, captureId, status) ->
            let captureIdJson =
                if captureId = "" then "null" else escapeJsonString captureId
            "{\"schema_version\":\""
            + ArtifactManifestSchemaVersion
            + "\",\"canonical_path\":"
            + escapeJsonString canonicalPath
            + ",\"original_path\":"
            + escapeJsonString canonicalPath
            + ",\"artifact_class\":"
            + escapeJsonString artifactClass
            + ",\"authority\":\"canonical_corpus\""
            + ",\"status\":"
            + escapeJsonString status
            + ",\"media_type\":\"application/octet-stream\""
            + ",\"byte_length\":"
            + byteLength.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ",\"sha256\":"
            + escapeJsonString sha256
            + ",\"capture_id\":"
            + captureIdJson
            + ",\"supersedes\":null,\"superseded_by\":null,\"metadata_gaps\":[]}"
        )
    let body =
        if List.isEmpty lines then ""
        else String.concat "\n" lines + "\n"
    writeAllText (Path.Combine(normalized, artifactsManifestFile)) body

let private writeOccurrences
    (root: string)
    (lines: string list)
    : unit =
    let normalized = Path.Combine(canonicalCorpus root, normalizedCorpusRelativeSubdir)
    ensureDir normalized
    let text =
        if List.isEmpty lines then "" else String.concat "\n" lines + "\n"
    writeAllText (Path.Combine(normalized, occurrencesFile)) text

let private emptyOccurrences (root: string) =
    writeOccurrences root []

let private sha256OfBytes (bytes: byte[]) : string =
    let sb = StringBuilder()
    for b in (SHA256.HashData(bytes)) do
        sb.Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture)) |> ignore
    sb.ToString()

let private validCommitOid = "1111111111111111111111111111111111111111"
let private validTreeOid = "2222222222222222222222222222222222222222"
let private otherCommitOid = "3333333333333333333333333333333333333333"
let private otherTreeOid = "4444444444444444444444444444444444444444"

/// Build the relative capture directory path that the binding uses for
/// canonical-path computation, mirroring the production
/// ``captureCanonicalDir`` helper.
let private captureCanonicalDirRel (captureId: string) : string =
    canonicalRootRelative + "/corpus/raw/" + captureId

let private captureDirRoot (root: string) (captureId: string) =
    Path.Combine(root, canonicalRootRelative + "/corpus/raw", captureId)

let private makeValidCapture (captureId: string) (rawArtifacts: (string * byte[]) list) =
    let root = newTempDir ()
    let captureDir = captureDirRoot root captureId
    ensureDir captureDir
    let entries =
        rawArtifacts
        |> List.map (fun (name, bytes) ->
            let fullPath = Path.Combine(captureDir, name)
            writeBytes fullPath bytes
            let canonical =
                canonicalise (captureCanonicalDirRel captureId + "/" + name)
            let sha = sha256OfBytes bytes
            (canonical, "raw", int64 bytes.Length, sha, captureId, "present"))
    writeMinimalCaptureManifest captureDir captureId validCommitOid validTreeOid (rawArtifacts |> List.map fst)
    writeArtifactManifest root entries
    emptyOccurrences root
    root, captureDir

// =============================================================================
// Occurrence line helpers
// =============================================================================

let private occurrenceLine
    (captureId: string)
    (ordinal: int64)
    (message: string)
    : string =
    let occ =
        { SchemaVersion = OccurrenceSchemaVersion
          ExtractorVersion = "test-v1"
          CaptureId = captureId
          SourceKind = LegacyText
          EventOrdinal = ordinal
          Severity = Warning
          Subcategory = None
          Code = None
          MessageRaw = message
          MessageNormalized = message
          LocationKind = Source
          SourcePath = None
          ProjectPath = None
          Span = emptySpan
          SenderName = None
          EventTimestamp = None
          BuildContext = None
          LegacySourceLineStart = None
          LegacySourceLineEnd = None }
    renderOccurrence occ

let private captureId = "cap-a"
let private otherCaptureId = "cap-b"

// =============================================================================
// Tests
// =============================================================================

[<Tests>]
let tests =
    testList
        "FSharpDiagnostics.RepairEpisodes.CaptureBinding"
        [ // 1. Complete valid capture binds successfully.
          test "complete valid capture binds successfully" {
              let root, _ = makeValidCapture captureId [ ("build.binlog", [| 0xCAuy; 0xFEuy; 0xBAuy; 0xBEuy |]) ]
              try
                  let request : CaptureBindingRequest =
                      { CaptureId = captureId
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = None }
                  let r = bindCapture root request
                  match r with
                  | Result.Ok bound ->
                      Expect.equal bound.Manifest.CaptureId captureId "captureId"
                      Expect.equal (List.length bound.RawArtifacts) 1 "one raw artifact"
                      Expect.equal (List.length bound.Occurrences) 0 "zero occurrences"
                      Expect.equal (List.head bound.RawArtifacts).ByteLength 4L "byte length"
                  | Result.Error f -> failwithf "expected Ok, got %A" f
              finally
                  cleanup root
          }

          // 2. Capture with zero occurrences binds successfully.
          test "capture with zero occurrences binds successfully" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  let bytes = [| 0x01uy; 0x02uy |]
                  let fullPath = Path.Combine(captureDir, "build.binlog")
                  writeBytes fullPath bytes
                  let canonical =
                      canonicalRootRelative + "/corpus/raw/" + captureId + "/build.binlog"
                  writeMinimalCaptureManifest captureDir captureId validCommitOid validTreeOid [ "build.binlog" ]
                  writeArtifactManifest
                      root
                      [ (canonical, "raw", 2L, sha256OfBytes bytes, captureId, "present") ]
                  emptyOccurrences root
                  let request : CaptureBindingRequest =
                      { CaptureId = captureId
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = None }
                  let r = bindCapture root request
                  match r with
                  | Result.Ok bound ->
                      Expect.isEmpty bound.Occurrences "zero occurrences"
                      Expect.equal (List.length bound.RawArtifacts) 1 "one raw artifact"
                  | Result.Error f -> failwithf "expected Ok, got %A" f
              finally
                  cleanup root
          }

          // 3. Invalid capture ID rejected.
          test "empty capture id rejected" {
              let root = newTempDir ()
              try
                  emptyOccurrences root
                  let request : CaptureBindingRequest =
                      { CaptureId = ""
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = None }
                  let r = bindCapture root request
                  match r with
                  | Result.Ok _ -> failwithf "expected Error"
                  | Result.Error (InvalidCaptureId _) -> ()
                  | Result.Error f -> failwithf "expected InvalidCaptureId, got %A" f
              finally
                  cleanup root
          }

          test "absolute capture id rejected" {
              let root = newTempDir ()
              try
                  emptyOccurrences root
                  let request : CaptureBindingRequest =
                      { CaptureId = "/tmp/cap"
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = None }
                  let r = bindCapture root request
                  match r with
                  | Result.Ok _ -> failwithf "expected Error"
                  | Result.Error (InvalidCaptureId _) -> ()
                  | Result.Error f -> failwithf "expected InvalidCaptureId, got %A" f
              finally
                  cleanup root
          }

          test "capture id with slash rejected" {
              let root = newTempDir ()
              try
                  emptyOccurrences root
                  let request : CaptureBindingRequest =
                      { CaptureId = "nested/cap"
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = None }
                  let r = bindCapture root request
                  match r with
                  | Result.Error (InvalidCaptureId _) -> ()
                  | _ -> failwithf "expected InvalidCaptureId"
              finally
                  cleanup root
          }

          test "capture id equal to dot rejected" {
              let root = newTempDir ()
              try
                  emptyOccurrences root
                  let request : CaptureBindingRequest =
                      { CaptureId = "."
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = None }
                  let r = bindCapture root request
                  match r with
                  | Result.Error (InvalidCaptureId _) -> ()
                  | _ -> failwithf "expected InvalidCaptureId"
              finally
                  cleanup root
          }

          // 4. Missing capture.json.
          test "missing capture.json returns CaptureManifestMissing" {
              let root = newTempDir ()
              try
                  emptyOccurrences root
                  let request : CaptureBindingRequest =
                      { CaptureId = captureId
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = None }
                  let r = bindCapture root request
                  match r with
                  | Result.Error (CaptureManifestMissing _) -> ()
                  | _ -> failwithf "expected CaptureManifestMissing"
              finally
                  cleanup root
          }

          // 5. Manifest capture ID mismatch.
          test "manifest capture id mismatch returns CaptureIdMismatch" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  writeMinimalCaptureManifest
                      captureDir
                      "other-cap"
                      validCommitOid
                      validTreeOid
                      [ "build.binlog" ]
                  writeArtifactManifest root []
                  emptyOccurrences root
                  let request : CaptureBindingRequest =
                      { CaptureId = captureId
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = None }
                  let r = bindCapture root request
                  match r with
                  | Result.Error (CaptureIdMismatch (req, man)) ->
                      Expect.equal req captureId "requested"
                      Expect.equal man "other-cap" "manifest"
                  | _ -> failwithf "expected CaptureIdMismatch"
              finally
                  cleanup root
          }

          // 6. Missing repository commit OID.
          test "missing repository commit oid returns RepositoryCommitOidMissing" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  let bytes = [| 0x01uy |]
                  let fullPath = Path.Combine(captureDir, "art.binlog")
                  writeBytes fullPath bytes
                  let canonical =
                      canonicalRootRelative + "/corpus/raw/" + captureId + "/art.binlog"
                  let text =
                      "{\"schema_version\":\""
                      + CaptureManifestSchemaVersion
                      + "\",\"capture_id\":\""
                      + captureId
                      + "\",\"capture_kind\":\"binlog\""
                      + ",\"raw_artifacts\":[\"art.binlog\"]"
                      + ",\"command\":null,\"working_directory\":null"
                      + ",\"repository_commit_oid\":null"
                      + ",\"repository_tree_oid\":\""
                      + validTreeOid
                      + "\""
                      + ",\"working_tree_state\":null,\"source_root_aliases\":[]"
                      + ",\"dotnet_sdk_version\":null,\"msbuild_version\":null"
                      + ",\"fsharp_compiler_version\":null"
                      + ",\"operating_system\":null,\"architecture\":null,\"culture\":null"
                      + ",\"started_at\":null,\"completed_at\":null,\"exit_code\":null"
                      + ",\"metadata_gaps\":[]}"
                  writeAllText (Path.Combine(captureDir, "capture.json")) text
                  writeArtifactManifest
                      root
                      [ (canonical, "raw", 1L, sha256OfBytes bytes, captureId, "present") ]
                  emptyOccurrences root
                  let request : CaptureBindingRequest =
                      { CaptureId = captureId
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = None }
                  let r = bindCapture root request
                  match r with
                  | Result.Error (RepositoryCommitOidMissing id) ->
                      Expect.equal id captureId "captureId"
                  | _ -> failwithf "expected RepositoryCommitOidMissing"
              finally
                  cleanup root
          }

          // 7. Commit OID mismatch.
          test "commit oid mismatch returns RepositoryCommitOidMismatch" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  let bytes = [| 0x01uy |]
                  let fullPath = Path.Combine(captureDir, "art.binlog")
                  writeBytes fullPath bytes
                  let canonical =
                      canonicalRootRelative + "/corpus/raw/" + captureId + "/art.binlog"
                  writeMinimalCaptureManifest
                      captureDir
                      captureId
                      validCommitOid
                      validTreeOid
                      [ "art.binlog" ]
                  writeArtifactManifest
                      root
                      [ (canonical, "raw", 1L, sha256OfBytes bytes, captureId, "present") ]
                  emptyOccurrences root
                  let request : CaptureBindingRequest =
                      { CaptureId = captureId
                        ResolvedCommitOid = otherCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = None }
                  let r = bindCapture root request
                  match r with
                  | Result.Error (RepositoryCommitOidMismatch (expected, actual)) ->
                      Expect.equal expected otherCommitOid "expected"
                      Expect.equal actual validCommitOid "actual"
                  | _ -> failwithf "expected RepositoryCommitOidMismatch"
              finally
                  cleanup root
          }

          // 8. Missing repository tree OID.
          test "missing repository tree oid returns RepositoryTreeOidMissing" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  let bytes = [| 0x01uy |]
                  let fullPath = Path.Combine(captureDir, "art.binlog")
                  writeBytes fullPath bytes
                  let canonical =
                      canonicalRootRelative + "/corpus/raw/" + captureId + "/art.binlog"
                  let text =
                      "{\"schema_version\":\""
                      + CaptureManifestSchemaVersion
                      + "\",\"capture_id\":\""
                      + captureId
                      + "\",\"capture_kind\":\"binlog\""
                      + ",\"raw_artifacts\":[\"art.binlog\"]"
                      + ",\"command\":null,\"working_directory\":null"
                      + ",\"repository_commit_oid\":\""
                      + validCommitOid
                      + "\""
                      + ",\"repository_tree_oid\":null"
                      + ",\"working_tree_state\":null,\"source_root_aliases\":[]"
                      + ",\"dotnet_sdk_version\":null,\"msbuild_version\":null"
                      + ",\"fsharp_compiler_version\":null"
                      + ",\"operating_system\":null,\"architecture\":null,\"culture\":null"
                      + ",\"started_at\":null,\"completed_at\":null,\"exit_code\":null"
                      + ",\"metadata_gaps\":[]}"
                  writeAllText (Path.Combine(captureDir, "capture.json")) text
                  writeArtifactManifest
                      root
                      [ (canonical, "raw", 1L, sha256OfBytes bytes, captureId, "present") ]
                  emptyOccurrences root
                  let request : CaptureBindingRequest =
                      { CaptureId = captureId
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = None }
                  let r = bindCapture root request
                  match r with
                  | Result.Error (RepositoryTreeOidMissing id) ->
                      Expect.equal id captureId "captureId"
                  | _ -> failwithf "expected RepositoryTreeOidMissing"
              finally
                  cleanup root
          }

          // 9. Resolved tree mismatch.
          test "resolved tree mismatch returns RepositoryTreeOidMismatch" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  let bytes = [| 0x01uy |]
                  let fullPath = Path.Combine(captureDir, "art.binlog")
                  writeBytes fullPath bytes
                  let canonical =
                      canonicalRootRelative + "/corpus/raw/" + captureId + "/art.binlog"
                  writeMinimalCaptureManifest
                      captureDir
                      captureId
                      validCommitOid
                      validTreeOid
                      [ "art.binlog" ]
                  writeArtifactManifest
                      root
                      [ (canonical, "raw", 1L, sha256OfBytes bytes, captureId, "present") ]
                  emptyOccurrences root
                  let request : CaptureBindingRequest =
                      { CaptureId = captureId
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = otherTreeOid
                        ExpectedTreeOid = None }
                  let r = bindCapture root request
                  match r with
                  | Result.Error (RepositoryTreeOidMismatch (expected, actual)) ->
                      Expect.equal expected otherTreeOid "expected"
                      Expect.equal actual validTreeOid "actual"
                  | _ -> failwithf "expected RepositoryTreeOidMismatch"
              finally
                  cleanup root
          }

          // 10. Optional expected-tree assertion mismatch.
          test "expected tree oid mismatch returns ExpectedTreeOidMismatch" {
              let root, _ = makeValidCapture captureId [ ("build.binlog", [| 0x01uy |]) ]
              try
                  let request : CaptureBindingRequest =
                      { CaptureId = captureId
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = Some otherTreeOid }
                  let r = bindCapture root request
                  match r with
                  | Result.Error (ExpectedTreeOidMismatch (expected, resolved)) ->
                      Expect.equal expected otherTreeOid "expected"
                      Expect.equal resolved validTreeOid "resolved"
                  | _ -> failwithf "expected ExpectedTreeOidMismatch"
              finally
                  cleanup root
          }

          // 11. Empty raw artifact list.
          test "empty raw artifact list returns RawArtifactListEmpty" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  writeMinimalCaptureManifest
                      captureDir
                      captureId
                      validCommitOid
                      validTreeOid
                      []
                  writeArtifactManifest root []
                  emptyOccurrences root
                  let request : CaptureBindingRequest =
                      { CaptureId = captureId
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = None }
                  let r = bindCapture root request
                  match r with
                  | Result.Error (RawArtifactListEmpty id) ->
                      Expect.equal id captureId "captureId"
                  | _ -> failwithf "expected RawArtifactListEmpty"
              finally
                  cleanup root
          }

          // 12. Absolute raw artifact path.
          test "absolute raw artifact path returns RawArtifactPathInvalid" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  writeMinimalCaptureManifest
                      captureDir
                      captureId
                      validCommitOid
                      validTreeOid
                      [ "/tmp/abs" ]
                  writeArtifactManifest root []
                  emptyOccurrences root
                  let request : CaptureBindingRequest =
                      { CaptureId = captureId
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = None }
                  let r = bindCapture root request
                  match r with
                  | Result.Error (RawArtifactPathInvalid _) -> ()
                  | _ -> failwithf "expected RawArtifactPathInvalid"
              finally
                  cleanup root
          }

          // 13. `..` traversal raw artifact path.
          test "traversal raw artifact path returns RawArtifactPathInvalid" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  writeMinimalCaptureManifest
                      captureDir
                      captureId
                      validCommitOid
                      validTreeOid
                      [ "../escape.log" ]
                  writeArtifactManifest root []
                  emptyOccurrences root
                  let request : CaptureBindingRequest =
                      { CaptureId = captureId
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = None }
                  let r = bindCapture root request
                  match r with
                  | Result.Error (RawArtifactPathInvalid _) -> ()
                  | _ -> failwithf "expected RawArtifactPathInvalid"
              finally
                  cleanup root
          }

          // 14. Duplicate normalized raw path.
          test "duplicate raw artifact names after normalisation returns DuplicateRawArtifactPath" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  let bytes = [| 0x01uy |]
                  let fullPath = Path.Combine(captureDir, "build.binlog")
                  writeBytes fullPath bytes
                  let canonical =
                      canonicalRootRelative + "/corpus/raw/" + captureId + "/build.binlog"
                  writeMinimalCaptureManifest
                      captureDir
                      captureId
                      validCommitOid
                      validTreeOid
                      [ "build.binlog"; "build.binlog" ]
                  writeArtifactManifest
                      root
                      [ (canonical, "raw", 1L, sha256OfBytes bytes, captureId, "present") ]
                  emptyOccurrences root
                  let request : CaptureBindingRequest =
                      { CaptureId = captureId
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = None }
                  let r = bindCapture root request
                  match r with
                  | Result.Error (DuplicateRawArtifactPath _) -> ()
                  | _ -> failwithf "expected DuplicateRawArtifactPath"
              finally
                  cleanup root
          }

          // 15. Missing raw artifact file.
          test "missing raw artifact file returns RawArtifactMissing" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  writeMinimalCaptureManifest
                      captureDir
                      captureId
                      validCommitOid
                      validTreeOid
                      [ "missing.binlog" ]
                  writeArtifactManifest root []
                  emptyOccurrences root
                  let request : CaptureBindingRequest =
                      { CaptureId = captureId
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = None }
                  let r = bindCapture root request
                  match r with
                  | Result.Error (RawArtifactMissing _) -> ()
                  | _ -> failwithf "expected RawArtifactMissing"
              finally
                  cleanup root
          }

          // 16. Missing artifact-manifest entry.
          test "missing artifact manifest entry returns ArtifactManifestEntryMissing" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  let bytes = [| 0x01uy |]
                  let fullPath = Path.Combine(captureDir, "art.binlog")
                  writeBytes fullPath bytes
                  writeMinimalCaptureManifest
                      captureDir
                      captureId
                      validCommitOid
                      validTreeOid
                      [ "art.binlog" ]
                  writeArtifactManifest root []
                  emptyOccurrences root
                  let request : CaptureBindingRequest =
                      { CaptureId = captureId
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = None }
                  let r = bindCapture root request
                  match r with
                  | Result.Error (ArtifactManifestEntryMissing _) -> ()
                  | _ -> failwithf "expected ArtifactManifestEntryMissing"
              finally
                  cleanup root
          }

          // 17. Duplicate artifact-manifest entry.
          test "duplicate artifact manifest entry returns ArtifactManifestEntryDuplicate" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  let bytes = [| 0x01uy |]
                  let fullPath = Path.Combine(captureDir, "art.binlog")
                  writeBytes fullPath bytes
                  let canonical =
                      canonicalRootRelative + "/corpus/raw/" + captureId + "/art.binlog"
                  writeMinimalCaptureManifest
                      captureDir
                      captureId
                      validCommitOid
                      validTreeOid
                      [ "art.binlog" ]
                  writeArtifactManifest
                      root
                      [ (canonical, "raw", 1L, sha256OfBytes bytes, captureId, "present")
                        (canonical, "raw", 1L, sha256OfBytes bytes, captureId, "present") ]
                  emptyOccurrences root
                  let request : CaptureBindingRequest =
                      { CaptureId = captureId
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = None }
                  let r = bindCapture root request
                  match r with
                  | Result.Error (ArtifactManifestEntryDuplicate _) -> ()
                  | _ -> failwithf "expected ArtifactManifestEntryDuplicate"
              finally
                  cleanup root
          }

          // 18. Manifest entry has wrong capture ID.
          test "artifact manifest entry with wrong capture id returns ArtifactManifestCaptureMismatch" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  let bytes = [| 0x01uy |]
                  let fullPath = Path.Combine(captureDir, "art.binlog")
                  writeBytes fullPath bytes
                  let canonical =
                      canonicalRootRelative + "/corpus/raw/" + captureId + "/art.binlog"
                  writeMinimalCaptureManifest
                      captureDir
                      captureId
                      validCommitOid
                      validTreeOid
                      [ "art.binlog" ]
                  writeArtifactManifest
                      root
                      [ (canonical, "raw", 1L, sha256OfBytes bytes, "other", "present") ]
                  emptyOccurrences root
                  let request : CaptureBindingRequest =
                      { CaptureId = captureId
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = None }
                  let r = bindCapture root request
                  match r with
                  | Result.Error (ArtifactManifestCaptureMismatch _) -> ()
                  | _ -> failwithf "expected ArtifactManifestCaptureMismatch"
              finally
                  cleanup root
          }

          // 19. Manifest entry has wrong class.
          test "artifact manifest entry with wrong class returns ArtifactManifestClassMismatch" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  let bytes = [| 0x01uy |]
                  let fullPath = Path.Combine(captureDir, "art.binlog")
                  writeBytes fullPath bytes
                  let canonical =
                      canonicalRootRelative + "/corpus/raw/" + captureId + "/art.binlog"
                  writeMinimalCaptureManifest
                      captureDir
                      captureId
                      validCommitOid
                      validTreeOid
                      [ "art.binlog" ]
                  writeArtifactManifest
                      root
                      [ (canonical, "normalized", 1L, sha256OfBytes bytes, captureId, "present") ]
                  emptyOccurrences root
                  let request : CaptureBindingRequest =
                      { CaptureId = captureId
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = None }
                  let r = bindCapture root request
                  match r with
                  | Result.Error (ArtifactManifestClassMismatch _) -> ()
                  | _ -> failwithf "expected ArtifactManifestClassMismatch"
              finally
                  cleanup root
          }

          // 20. Manifest entry has non-present status.
          test "artifact manifest entry with non-present status returns ArtifactManifestStatusMismatch" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  let bytes = [| 0x01uy |]
                  let fullPath = Path.Combine(captureDir, "art.binlog")
                  writeBytes fullPath bytes
                  let canonical =
                      canonicalRootRelative + "/corpus/raw/" + captureId + "/art.binlog"
                  writeMinimalCaptureManifest
                      captureDir
                      captureId
                      validCommitOid
                      validTreeOid
                      [ "art.binlog" ]
                  writeArtifactManifest
                      root
                      [ (canonical, "raw", 1L, sha256OfBytes bytes, captureId, "migrated") ]
                  emptyOccurrences root
                  let request : CaptureBindingRequest =
                      { CaptureId = captureId
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = None }
                  let r = bindCapture root request
                  match r with
                  | Result.Error (ArtifactManifestStatusMismatch _) -> ()
                  | _ -> failwithf "expected ArtifactManifestStatusMismatch"
              finally
                  cleanup root
          }

          // 21. Byte-length mismatch.
          test "byte length mismatch returns RawArtifactLengthMismatch" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  let bytes = [| 0x01uy; 0x02uy; 0x03uy |]
                  let fullPath = Path.Combine(captureDir, "art.binlog")
                  writeBytes fullPath bytes
                  let canonical =
                      canonicalRootRelative + "/corpus/raw/" + captureId + "/art.binlog"
                  writeMinimalCaptureManifest
                      captureDir
                      captureId
                      validCommitOid
                      validTreeOid
                      [ "art.binlog" ]
                  writeArtifactManifest
                      root
                      [ (canonical, "raw", 5L, sha256OfBytes bytes, captureId, "present") ]
                  emptyOccurrences root
                  let request : CaptureBindingRequest =
                      { CaptureId = captureId
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = None }
                  let r = bindCapture root request
                  match r with
                  | Result.Error (RawArtifactLengthMismatch (canonical', expected, actual)) ->
                      Expect.equal canonical' canonical "canonical"
                      Expect.equal expected 5L "expected"
                      Expect.equal actual 3L "actual"
                  | _ -> failwithf "expected RawArtifactLengthMismatch"
              finally
                  cleanup root
          }

          // 22. SHA-256 mismatch.
          test "sha256 mismatch returns RawArtifactHashMismatch" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  let bytes = [| 0x01uy; 0x02uy |]
                  let fullPath = Path.Combine(captureDir, "art.binlog")
                  writeBytes fullPath bytes
                  let canonical =
                      canonicalRootRelative + "/corpus/raw/" + captureId + "/art.binlog"
                  writeMinimalCaptureManifest
                      captureDir
                      captureId
                      validCommitOid
                      validTreeOid
                      [ "art.binlog" ]
                  let bogus = String('0', 64)
                  writeArtifactManifest
                      root
                      [ (canonical, "raw", 2L, bogus, captureId, "present") ]
                  emptyOccurrences root
                  let request : CaptureBindingRequest =
                      { CaptureId = captureId
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = None }
                  let r = bindCapture root request
                  match r with
                  | Result.Error (RawArtifactHashMismatch (canonical', expected, actual)) ->
                      Expect.equal canonical' canonical "canonical"
                      Expect.equal expected bogus "expected"
                      Expect.equal actual (sha256OfBytes bytes) "actual"
                  | _ -> failwithf "expected RawArtifactHashMismatch"
              finally
                  cleanup root
          }

          // 23. Malformed artifact-manifest line invalidates binding.
          test "malformed artifact manifest line returns ArtifactManifestReadFailed" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  let bytes = [| 0x01uy |]
                  let fullPath = Path.Combine(captureDir, "art.binlog")
                  writeBytes fullPath bytes
                  let canonical =
                      canonicalRootRelative + "/corpus/raw/" + captureId + "/art.binlog"
                  writeMinimalCaptureManifest
                      captureDir
                      captureId
                      validCommitOid
                      validTreeOid
                      [ "art.binlog" ]
                  let goodLine =
                      "{\"schema_version\":\""
                      + ArtifactManifestSchemaVersion
                      + "\",\"canonical_path\":"
                      + escapeJsonString canonical
                      + ",\"original_path\":"
                      + escapeJsonString canonical
                      + ",\"artifact_class\":\"raw\""
                      + ",\"authority\":\"canonical_corpus\""
                      + ",\"status\":\"present\""
                      + ",\"media_type\":\"application/octet-stream\""
                      + ",\"byte_length\":1"
                      + ",\"sha256\":"
                      + escapeJsonString (sha256OfBytes bytes)
                      + ",\"capture_id\":"
                      + escapeJsonString captureId
                      + ",\"supersedes\":null,\"superseded_by\":null,\"metadata_gaps\":[]}"
                  let body = goodLine + "\n{not valid json\n"
                  let normalizedDir = Path.Combine(canonicalCorpus root, normalizedCorpusRelativeSubdir)
                  ensureDir normalizedDir
                  writeAllText (Path.Combine(normalizedDir, artifactsManifestFile)) body
                  emptyOccurrences root
                  let request : CaptureBindingRequest =
                      { CaptureId = captureId
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = None }
                  let r = bindCapture root request
                  match r with
                  | Result.Error (ArtifactManifestReadFailed (_canonical, line, _)) ->
                      Expect.equal line 2 "line number"
                  | _ -> failwithf "expected ArtifactManifestReadFailed"
              finally
                  cleanup root
          }

          // 24. Strict occurrence-reader failure is preserved.
          test "missing occurrence stream returns OccurrenceStreamFailure" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  let bytes = [| 0x01uy |]
                  let fullPath = Path.Combine(captureDir, "art.binlog")
                  writeBytes fullPath bytes
                  let canonical =
                      canonicalRootRelative + "/corpus/raw/" + captureId + "/art.binlog"
                  writeMinimalCaptureManifest
                      captureDir
                      captureId
                      validCommitOid
                      validTreeOid
                      [ "art.binlog" ]
                  writeArtifactManifest
                      root
                      [ (canonical, "raw", 1L, sha256OfBytes bytes, captureId, "present") ]
                  // No occurrences file.
                  let request : CaptureBindingRequest =
                      { CaptureId = captureId
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = None }
                  let r = bindCapture root request
                  match r with
                  | Result.Error (OccurrenceStreamFailure (FileMissing _)) -> ()
                  | _ -> failwithf "expected OccurrenceStreamFailure"
              finally
                  cleanup root
          }

          // 25. Duplicate occurrence ordinal rejected.
          test "duplicate occurrence ordinal returns DuplicateOccurrenceOrdinal" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  let bytes = [| 0x01uy |]
                  let fullPath = Path.Combine(captureDir, "art.binlog")
                  writeBytes fullPath bytes
                  let canonical =
                      canonicalRootRelative + "/corpus/raw/" + captureId + "/art.binlog"
                  writeMinimalCaptureManifest
                      captureDir
                      captureId
                      validCommitOid
                      validTreeOid
                      [ "art.binlog" ]
                  writeArtifactManifest
                      root
                      [ (canonical, "raw", 1L, sha256OfBytes bytes, captureId, "present") ]
                  writeOccurrences
                      root
                      [ occurrenceLine captureId 1L "first"
                        occurrenceLine captureId 1L "duplicate ordinal" ]
                  let request : CaptureBindingRequest =
                      { CaptureId = captureId
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = None }
                  let r = bindCapture root request
                  match r with
                  | Result.Error (DuplicateOccurrenceOrdinal (id, ordinal)) ->
                      Expect.equal id captureId "captureId"
                      Expect.equal ordinal 1L "ordinal"
                  | _ -> failwithf "expected DuplicateOccurrenceOrdinal"
              finally
                  cleanup root
          }

          // 26. Occurrences for other captures are ignored.
          test "occurrences for other captures are ignored" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  let bytes = [| 0x01uy |]
                  let fullPath = Path.Combine(captureDir, "art.binlog")
                  writeBytes fullPath bytes
                  let canonical =
                      canonicalRootRelative + "/corpus/raw/" + captureId + "/art.binlog"
                  writeMinimalCaptureManifest
                      captureDir
                      captureId
                      validCommitOid
                      validTreeOid
                      [ "art.binlog" ]
                  writeArtifactManifest
                      root
                      [ (canonical, "raw", 1L, sha256OfBytes bytes, captureId, "present") ]
                  writeOccurrences
                      root
                      [ occurrenceLine otherCaptureId 1L "ignored"
                        occurrenceLine captureId 7L "kept" ]
                  let request : CaptureBindingRequest =
                      { CaptureId = captureId
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = None }
                  let r = bindCapture root request
                  match r with
                  | Result.Ok bound ->
                      Expect.equal (List.length bound.Occurrences) 1 "one occurrence"
                      Expect.equal (List.head bound.Occurrences).CaptureId captureId "captureId"
                      Expect.equal (List.head bound.Occurrences).EventOrdinal 7L "ordinal"
                  | Result.Error f -> failwithf "expected Ok, got %A" f
              finally
                  cleanup root
          }

          // 27. Returned raw artifacts are sorted ordinally by canonical path.
          test "returned raw artifacts are sorted by canonical path" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  let bytesA = [| 0x0Auy |]
                  let bytesM = [| 0x0Duy |]
                  let bytesZ = [| 0x1Auy |]
                  let pA = Path.Combine(captureDir, "a.binlog")
                  let pM = Path.Combine(captureDir, "m.binlog")
                  let pZ = Path.Combine(captureDir, "z.binlog")
                  writeBytes pA bytesA
                  writeBytes pM bytesM
                  writeBytes pZ bytesZ
                  let canonicalA = canonicalRootRelative + "/corpus/raw/" + captureId + "/a.binlog"
                  let canonicalM = canonicalRootRelative + "/corpus/raw/" + captureId + "/m.binlog"
                  let canonicalZ = canonicalRootRelative + "/corpus/raw/" + captureId + "/z.binlog"
                  writeMinimalCaptureManifest
                      captureDir
                      captureId
                      validCommitOid
                      validTreeOid
                      [ "z.binlog"; "a.binlog"; "m.binlog" ]
                  writeArtifactManifest
                      root
                      [ (canonicalZ, "raw", 1L, sha256OfBytes bytesZ, captureId, "present")
                        (canonicalM, "raw", 1L, sha256OfBytes bytesM, captureId, "present")
                        (canonicalA, "raw", 1L, sha256OfBytes bytesA, captureId, "present") ]
                  emptyOccurrences root
                  let request : CaptureBindingRequest =
                      { CaptureId = captureId
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = None }
                  let r = bindCapture root request
                  match r with
                  | Result.Ok bound ->
                      Expect.equal (List.length bound.RawArtifacts) 3 "three raw artifacts"
                      Expect.equal
                          (List.map (fun a -> Path.GetFileName a.CanonicalPath) bound.RawArtifacts)
                          [ "a.binlog"; "m.binlog"; "z.binlog" ]
                          "sorted by canonical path"
                  | Result.Error f -> failwithf "expected Ok, got %A" f
              finally
                  cleanup root
          }

          // 28. Returned occurrences preserve canonical stream order.
          test "returned occurrences preserve canonical stream order" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  let bytes = [| 0x01uy |]
                  let fullPath = Path.Combine(captureDir, "art.binlog")
                  writeBytes fullPath bytes
                  let canonical =
                      canonicalRootRelative + "/corpus/raw/" + captureId + "/art.binlog"
                  writeMinimalCaptureManifest
                      captureDir
                      captureId
                      validCommitOid
                      validTreeOid
                      [ "art.binlog" ]
                  writeArtifactManifest
                      root
                      [ (canonical, "raw", 1L, sha256OfBytes bytes, captureId, "present") ]
                  writeOccurrences
                      root
                      [ occurrenceLine captureId 1L "first"
                        occurrenceLine captureId 2L "second"
                        occurrenceLine captureId 3L "third" ]
                  let request : CaptureBindingRequest =
                      { CaptureId = captureId
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = None }
                  let r = bindCapture root request
                  match r with
                  | Result.Ok bound ->
                      let ordinals =
                          bound.Occurrences |> List.map (fun o -> o.EventOrdinal)
                      Expect.equal ordinals [ 1L; 2L; 3L ] "ordinals in order"
                  | Result.Error f -> failwithf "expected Ok, got %A" f
              finally
                  cleanup root
          }

          // 29. Returned canonical path is repository-relative.
          test "returned canonical path is repository-relative" {
              let root, _ = makeValidCapture captureId [ ("build.binlog", [| 0x01uy |]) ]
              try
                  let request : CaptureBindingRequest =
                      { CaptureId = captureId
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = None }
                  let r = bindCapture root request
                  match r with
                  | Result.Ok bound ->
                      let canonical = (List.head bound.RawArtifacts).CanonicalPath
                      Expect.isTrue (canonical.StartsWith "factory/evidence/fsharp-diagnostics") "starts with canonical root"
                      Expect.isFalse (canonical.StartsWith root) "not prefixed with repoRoot"
                  | Result.Error f -> failwithf "expected Ok, got %A" f
              finally
                  cleanup root
          }

          // 30. Returned record does not contain repoRoot.
          test "returned record does not contain repoRoot" {
              let root, _ = makeValidCapture captureId [ ("build.binlog", [| 0x01uy |]) ]
              try
                  let request : CaptureBindingRequest =
                      { CaptureId = captureId
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = None }
                  let r = bindCapture root request
                  match r with
                  | Result.Ok bound ->
                      for raw in bound.RawArtifacts do
                          Expect.isFalse (raw.CanonicalPath.Contains root) "no repoRoot prefix"
                  | Result.Error f -> failwithf "expected Ok, got %A" f
              finally
                  cleanup root
          }

          // 31. Equivalent roots produce identical BoundRawArtifact records.
          test "equivalent roots produce identical BoundRawArtifact records" {
              let buildValid () =
                  let r = newTempDir ()
                  let c = captureDirRoot r captureId
                  ensureDir c
                  let bytes = [| 0xCAuy; 0xFEuy; 0xBAuy; 0xBEuy |]
                  writeBytes (Path.Combine(c, "build.binlog")) bytes
                  let canonical =
                      canonicalRootRelative + "/corpus/raw/" + captureId + "/build.binlog"
                  writeMinimalCaptureManifest
                      c
                      captureId
                      validCommitOid
                      validTreeOid
                      [ "build.binlog" ]
                  writeArtifactManifest
                      r
                      [ (canonical, "raw", 4L, sha256OfBytes bytes, captureId, "present") ]
                  emptyOccurrences r
                  r
              let root1 = buildValid ()
              let root2 = buildValid ()
              try
                  let req : CaptureBindingRequest =
                      { CaptureId = captureId
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = None }
                  let r1 = bindCapture root1 req
                  let r2 = bindCapture root2 req
                  match r1, r2 with
                  | Result.Ok b1, Result.Ok b2 ->
                      Expect.equal b1.RawArtifacts b2.RawArtifacts "raw artifacts equal"
                  | _ -> failwithf "expected both Ok"
              finally
                  cleanup root1
                  cleanup root2
          }

          // 32. Raw-file symlink rejected.
          test "raw-file symlink rejected" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  let srcPath = Path.Combine(captureDir, "real.binlog")
                  let linkPath = Path.Combine(captureDir, "link.binlog")
                  let bytes = [| 0x01uy; 0x02uy |]
                  writeBytes srcPath bytes
                  File.CreateSymbolicLink(linkPath, srcPath) |> ignore
                  let canonical =
                      canonicalRootRelative + "/corpus/raw/" + captureId + "/link.binlog"
                  writeMinimalCaptureManifest
                      captureDir
                      captureId
                      validCommitOid
                      validTreeOid
                      [ "link.binlog" ]
                  writeArtifactManifest
                      root
                      [ (canonical, "raw", 2L, sha256OfBytes bytes, captureId, "present") ]
                  emptyOccurrences root
                  let request : CaptureBindingRequest =
                      { CaptureId = captureId
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = None }
                  let r = bindCapture root request
                  match r with
                  | Result.Error (RawArtifactPathInvalid _) -> ()
                  | _ -> failwithf "expected RawArtifactPathInvalid"
              finally
                  cleanup root
          }

          // 33. Intermediate-directory symlink rejected.
          test "intermediate-directory symlink rejected" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  let realSub = Path.Combine(root, "realsub")
                  ensureDir realSub
                  let linkSub = Path.Combine(captureDir, "linksub")
                  File.CreateSymbolicLink(linkSub, realSub) |> ignore
                  let bytes = [| 0x01uy |]
                  let linkFile = Path.Combine(linkSub, "art.binlog")
                  writeBytes linkFile bytes
                  let canonical =
                      canonicalRootRelative + "/corpus/raw/" + captureId + "/linksub/art.binlog"
                  writeMinimalCaptureManifest
                      captureDir
                      captureId
                      validCommitOid
                      validTreeOid
                      [ "linksub/art.binlog" ]
                  writeArtifactManifest
                      root
                      [ (canonical, "raw", 1L, sha256OfBytes bytes, captureId, "present") ]
                  emptyOccurrences root
                  let request : CaptureBindingRequest =
                      { CaptureId = captureId
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = None }
                  let r = bindCapture root request
                  match r with
                  | Result.Error (RawArtifactPathInvalid _) -> ()
                  | _ -> failwithf "expected RawArtifactPathInvalid"
              finally
                  cleanup root
          }

          // 34. Invalid artifact-manifest UTF-8 rejected.
          test "invalid artifact-manifest UTF-8 rejected" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  let bytes = [| 0x01uy |]
                  writeBytes (Path.Combine(captureDir, "art.binlog")) bytes
                  writeMinimalCaptureManifest
                      captureDir
                      captureId
                      validCommitOid
                      validTreeOid
                      [ "art.binlog" ]
                  let normalizedDir = Path.Combine(canonicalCorpus root, normalizedCorpusRelativeSubdir)
                  ensureDir normalizedDir
                  let bad = Path.Combine(normalizedDir, artifactsManifestFile)
                  // 0xFF 0xFE 0xFD are not valid UTF-8 leading bytes.
                  writeBytes bad [| 0xFFuy; 0xFEuy; 0xFDuy |]
                  emptyOccurrences root
                  let request : CaptureBindingRequest =
                      { CaptureId = captureId
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = None }
                  let r = bindCapture root request
                  match r with
                  | Result.Error (ArtifactManifestReadFailed _) -> ()
                  | _ -> failwithf "expected ArtifactManifestReadFailed"
              finally
                  cleanup root
          }

          // 35. Artifact-manifest BOM rejected.
          test "artifact-manifest BOM rejected" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  let bytes = [| 0x01uy |]
                  writeBytes (Path.Combine(captureDir, "art.binlog")) bytes
                  writeMinimalCaptureManifest
                      captureDir
                      captureId
                      validCommitOid
                      validTreeOid
                      [ "art.binlog" ]
                  let canonical =
                      canonicalRootRelative + "/corpus/raw/" + captureId + "/art.binlog"
                  let line =
                      "{\"schema_version\":\""
                      + ArtifactManifestSchemaVersion
                      + "\",\"canonical_path\":"
                      + escapeJsonString canonical
                      + ",\"original_path\":"
                      + escapeJsonString canonical
                      + ",\"artifact_class\":\"raw\""
                      + ",\"authority\":\"canonical_corpus\""
                      + ",\"status\":\"present\""
                      + ",\"media_type\":\"application/octet-stream\""
                      + ",\"byte_length\":1"
                      + ",\"sha256\":"
                      + escapeJsonString (sha256OfBytes bytes)
                      + ",\"capture_id\":"
                      + escapeJsonString captureId
                      + ",\"supersedes\":null,\"superseded_by\":null,\"metadata_gaps\":[]}"
                  let text = utf8NoBom.GetBytes(line)
                  let bom : byte[] = [| 0xEFuy; 0xBBuy; 0xBFuy |]
                  let combined =
                      Array.append (Array.append bom text) [| 0x0Auy |]
                  let normalizedDir = Path.Combine(canonicalCorpus root, normalizedCorpusRelativeSubdir)
                  ensureDir normalizedDir
                  writeBytes (Path.Combine(normalizedDir, artifactsManifestFile)) combined
                  emptyOccurrences root
                  let request : CaptureBindingRequest =
                      { CaptureId = captureId
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = None }
                  let r = bindCapture root request
                  match r with
                  | Result.Error (ArtifactManifestReadFailed (_, _, detail)) ->
                      Expect.isTrue (detail.Contains "BOM") "BOM detail"
                  | _ -> failwithf "expected ArtifactManifestReadFailed"
              finally
                  cleanup root
          }

          // 36. Artifact-manifest NUL rejected.
          test "artifact-manifest NUL rejected" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  let bytes = [| 0x01uy |]
                  writeBytes (Path.Combine(captureDir, "art.binlog")) bytes
                  writeMinimalCaptureManifest
                      captureDir
                      captureId
                      validCommitOid
                      validTreeOid
                      [ "art.binlog" ]
                  let canonical =
                      canonicalRootRelative + "/corpus/raw/" + captureId + "/art.binlog"
                  let line =
                      "{\"schema_version\":\""
                      + ArtifactManifestSchemaVersion
                      + "\",\"canonical_path\":"
                      + escapeJsonString canonical
                      + ",\"original_path\":"
                      + escapeJsonString canonical
                      + ",\"artifact_class\":\"raw\""
                      + ",\"authority\":\"canonical_corpus\""
                      + ",\"status\":\"present\""
                      + ",\"media_type\":\"application/octet-stream\""
                      + ",\"byte_length\":1"
                      + ",\"sha256\":"
                      + escapeJsonString (sha256OfBytes bytes)
                      + ",\"capture_id\":"
                      + escapeJsonString captureId
                      + ",\"supersedes\":null,\"superseded_by\":null,\"metadata_gaps\":[]}"
                  let bytes2 = Array.append (utf8NoBom.GetBytes(line)) [| 0x00uy |]
                  let normalizedDir = Path.Combine(canonicalCorpus root, normalizedCorpusRelativeSubdir)
                  ensureDir normalizedDir
                  writeBytes (Path.Combine(normalizedDir, artifactsManifestFile)) bytes2
                  emptyOccurrences root
                  let request : CaptureBindingRequest =
                      { CaptureId = captureId
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = None }
                  let r = bindCapture root request
                  match r with
                  | Result.Error (ArtifactManifestReadFailed _) -> ()
                  | _ -> failwithf "expected ArtifactManifestReadFailed"
              finally
                  cleanup root
          }

          // 37. Artifact-manifest schema mismatch rejected.
          test "artifact-manifest schema mismatch rejected" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  let bytes = [| 0x01uy |]
                  writeBytes (Path.Combine(captureDir, "art.binlog")) bytes
                  writeMinimalCaptureManifest
                      captureDir
                      captureId
                      validCommitOid
                      validTreeOid
                      [ "art.binlog" ]
                  let canonical =
                      canonicalRootRelative + "/corpus/raw/" + captureId + "/art.binlog"
                  let line =
                      "{\"schema_version\":\"wrong-version\""
                      + ",\"canonical_path\":"
                      + escapeJsonString canonical
                      + ",\"original_path\":"
                      + escapeJsonString canonical
                      + ",\"artifact_class\":\"raw\""
                      + ",\"authority\":\"canonical_corpus\""
                      + ",\"status\":\"present\""
                      + ",\"media_type\":\"application/octet-stream\""
                      + ",\"byte_length\":1"
                      + ",\"sha256\":"
                      + escapeJsonString (sha256OfBytes bytes)
                      + ",\"capture_id\":"
                      + escapeJsonString captureId
                      + ",\"supersedes\":null,\"superseded_by\":null,\"metadata_gaps\":[]}"
                  let normalizedDir = Path.Combine(canonicalCorpus root, normalizedCorpusRelativeSubdir)
                  ensureDir normalizedDir
                  writeAllText (Path.Combine(normalizedDir, artifactsManifestFile)) (line + "\n")
                  emptyOccurrences root
                  let request : CaptureBindingRequest =
                      { CaptureId = captureId
                        ResolvedCommitOid = validCommitOid
                        ResolvedTreeOid = validTreeOid
                        ExpectedTreeOid = None }
                  let r = bindCapture root request
                  match r with
                  | Result.Error (ArtifactManifestReadFailed (_canonical, lineNo, _)) ->
                      Expect.equal lineNo 1 "line number"
                  | _ -> failwithf "expected ArtifactManifestReadFailed"
              finally
                  cleanup root
          }

          // ---- Capture-manifest strictness tests ----
          // These tests exercise the strict ``readCaptureManifest`` reader
          // directly.  They prove the reader rejects malformed input that
          // the foundation ``Manifest.readCaptureManifest`` reader would
          // silently accept.

          test "capture-manifest strictness: duplicate property rejected" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  let text =
                      "{\"schema_version\":\""
                      + CaptureManifestSchemaVersion
                      + "\",\"capture_id\":\""
                      + captureId
                      + "\",\"capture_id\":\"other\""
                      + ",\"capture_kind\":\"binlog\""
                      + ",\"raw_artifacts\":[\"x.binlog\"]"
                      + ",\"command\":null,\"working_directory\":null"
                      + ",\"repository_commit_oid\":null"
                      + ",\"repository_tree_oid\":null"
                      + ",\"working_tree_state\":null,\"source_root_aliases\":[]"
                      + ",\"dotnet_sdk_version\":null,\"msbuild_version\":null"
                      + ",\"fsharp_compiler_version\":null"
                      + ",\"operating_system\":null,\"architecture\":null,\"culture\":null"
                      + ",\"started_at\":null,\"completed_at\":null,\"exit_code\":null"
                      + ",\"metadata_gaps\":[]}"
                  writeAllText (Path.Combine(captureDir, "capture.json")) text
                  let r = readCaptureManifest (Path.Combine(captureDir, "capture.json"))
                  match r with
                  | Result.Error (CaptureManifestReadFailure.DuplicateField (_, _, field)) ->
                      Expect.equal field "capture_id" "duplicate field name"
                  | _ -> failwithf "expected DuplicateField"
              finally
                  cleanup root
          }

          test "capture-manifest strictness: escaped-equivalent duplicate property rejected" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  let text =
                      "{\"schema_version\":\""
                      + CaptureManifestSchemaVersion
                      + "\",\"capture_id\":\""
                      + captureId
                      + "\",\"\\u0063apture_id\":\"other\""
                      + ",\"capture_kind\":\"binlog\""
                      + ",\"raw_artifacts\":[\"x.binlog\"]"
                      + ",\"command\":null,\"working_directory\":null"
                      + ",\"repository_commit_oid\":null"
                      + ",\"repository_tree_oid\":null"
                      + ",\"working_tree_state\":null,\"source_root_aliases\":[]"
                      + ",\"dotnet_sdk_version\":null,\"msbuild_version\":null"
                      + ",\"fsharp_compiler_version\":null"
                      + ",\"operating_system\":null,\"architecture\":null,\"culture\":null"
                      + ",\"started_at\":null,\"completed_at\":null,\"exit_code\":null"
                      + ",\"metadata_gaps\":[]}"
                  writeAllText (Path.Combine(captureDir, "capture.json")) text
                  let r = readCaptureManifest (Path.Combine(captureDir, "capture.json"))
                  match r with
                  | Result.Error (CaptureManifestReadFailure.DuplicateField (_, _, _)) -> ()
                  | _ -> failwithf "expected DuplicateField"
              finally
                  cleanup root
          }

          test "capture-manifest strictness: unknown property rejected" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  let text =
                      "{\"schema_version\":\""
                      + CaptureManifestSchemaVersion
                      + "\",\"capture_id\":\""
                      + captureId
                      + "\",\"capture_kind\":\"binlog\""
                      + ",\"raw_artifacts\":[\"x.binlog\"]"
                      + ",\"command\":null,\"working_directory\":null"
                      + ",\"repository_commit_oid\":null"
                      + ",\"repository_tree_oid\":null"
                      + ",\"working_tree_state\":null,\"source_root_aliases\":[]"
                      + ",\"dotnet_sdk_version\":null,\"msbuild_version\":null"
                      + ",\"fsharp_compiler_version\":null"
                      + ",\"operating_system\":null,\"architecture\":null,\"culture\":null"
                      + ",\"started_at\":null,\"completed_at\":null,\"exit_code\":null"
                      + ",\"metadata_gaps\":[]"
                      + ",\"extra_field\":\"oops\"}"
                  writeAllText (Path.Combine(captureDir, "capture.json")) text
                  let r = readCaptureManifest (Path.Combine(captureDir, "capture.json"))
                  match r with
                  | Result.Error (CaptureManifestReadFailure.UnknownField (_p, _l, field)) ->
                      Expect.equal field "extra_field" "unknown field name"
                  | _ -> failwithf "expected UnknownField"
              finally
                  cleanup root
          }

          test "capture-manifest strictness: wrong JSON kind rejected" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  let text =
                      "{\"schema_version\":\""
                      + CaptureManifestSchemaVersion
                      + "\",\"capture_id\":42"
                      + ",\"capture_kind\":\"binlog\""
                      + ",\"raw_artifacts\":[\"x.binlog\"]"
                      + ",\"command\":null,\"working_directory\":null"
                      + ",\"repository_commit_oid\":null"
                      + ",\"repository_tree_oid\":null"
                      + ",\"working_tree_state\":null,\"source_root_aliases\":[]"
                      + ",\"dotnet_sdk_version\":null,\"msbuild_version\":null"
                      + ",\"fsharp_compiler_version\":null"
                      + ",\"operating_system\":null,\"architecture\":null,\"culture\":null"
                      + ",\"started_at\":null,\"completed_at\":null,\"exit_code\":null"
                      + ",\"metadata_gaps\":[]}"
                  writeAllText (Path.Combine(captureDir, "capture.json")) text
                  let r = readCaptureManifest (Path.Combine(captureDir, "capture.json"))
                  match r with
                  | Result.Error (CaptureManifestReadFailure.WrongJsonKind (_p, _l, field)) ->
                      Expect.equal field "capture_id" "wrong-kind field"
                  | _ -> failwithf "expected WrongJsonKind"
              finally
                  cleanup root
          }

          test "capture-manifest strictness: invalid UTF-8 rejected" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  writeBytes (Path.Combine(captureDir, "capture.json")) [| 0xFFuy; 0xFEuy; 0xFDuy |]
                  let r = readCaptureManifest (Path.Combine(captureDir, "capture.json"))
                  match r with
                  | Result.Error (CaptureManifestReadFailure.InvalidByteSequence _) -> ()
                  | _ -> failwithf "expected InvalidByteSequence"
              finally
                  cleanup root
          }

          test "capture-manifest strictness: wrong schema version rejected" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  let text =
                      "{\"schema_version\":\"wrong-version\""
                      + ",\"capture_id\":\""
                      + captureId
                      + "\",\"capture_kind\":\"binlog\""
                      + ",\"raw_artifacts\":[\"x.binlog\"]"
                      + ",\"command\":null,\"working_directory\":null"
                      + ",\"repository_commit_oid\":null"
                      + ",\"repository_tree_oid\":null"
                      + ",\"working_tree_state\":null,\"source_root_aliases\":[]"
                      + ",\"dotnet_sdk_version\":null,\"msbuild_version\":null"
                      + ",\"fsharp_compiler_version\":null"
                      + ",\"operating_system\":null,\"architecture\":null,\"culture\":null"
                      + ",\"started_at\":null,\"completed_at\":null,\"exit_code\":null"
                      + ",\"metadata_gaps\":[]}"
                  writeAllText (Path.Combine(captureDir, "capture.json")) text
                  let r = readCaptureManifest (Path.Combine(captureDir, "capture.json"))
                  match r with
                  | Result.Error (CaptureManifestReadFailure.SchemaVersionMismatch (_p, _l, actual)) ->
                      Expect.equal actual "wrong-version" "schema version"
                  | _ -> failwithf "expected SchemaVersionMismatch"
              finally
                  cleanup root
          }
          // ---- Source-root alias tests ----
          // These tests prove the strict capture-manifest reader can
          // parse a non-empty source_root_aliases array, and rejects
          // malformed alias entries.

          test "source-root alias: one valid alias round-trips exactly" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  let text =
                      "{\"schema_version\":\""
                      + CaptureManifestSchemaVersion
                      + "\",\"capture_id\":\""
                      + captureId
                      + "\",\"capture_kind\":\"binlog\""
                      + ",\"raw_artifacts\":[\"x.binlog\"]"
                      + ",\"command\":null,\"working_directory\":null"
                      + ",\"repository_commit_oid\":null"
                      + ",\"repository_tree_oid\":null"
                      + ",\"working_tree_state\":null"
                      + ",\"source_root_aliases\":[{\"absolute_root\":\"/abs\",\"canonical_root\":\"/can\"}]"
                      + ",\"dotnet_sdk_version\":null,\"msbuild_version\":null"
                      + ",\"fsharp_compiler_version\":null"
                      + ",\"operating_system\":null,\"architecture\":null,\"culture\":null"
                      + ",\"started_at\":null,\"completed_at\":null,\"exit_code\":null"
                      + ",\"metadata_gaps\":[]}"
                  writeAllText (Path.Combine(captureDir, "capture.json")) text
                  let r = readCaptureManifest (Path.Combine(captureDir, "capture.json"))
                  match r with
                  | Result.Ok manifest ->
                      Expect.equal (List.length manifest.SourceRootAliases) 1 "one alias"
                      Expect.equal (List.head manifest.SourceRootAliases).AbsoluteRoot "/abs" "absolute_root"
                      Expect.equal (List.head manifest.SourceRootAliases).CanonicalRoot "/can" "canonical_root"
                  | Result.Error f -> failwithf "expected Ok, got %A" f
              finally
                  cleanup root
          }

          test "source-root aliases: multiple aliases preserve order" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  let text =
                      "{\"schema_version\":\""
                      + CaptureManifestSchemaVersion
                      + "\",\"capture_id\":\""
                      + captureId
                      + "\",\"capture_kind\":\"binlog\""
                      + ",\"raw_artifacts\":[\"x.binlog\"]"
                      + ",\"command\":null,\"working_directory\":null"
                      + ",\"repository_commit_oid\":null"
                      + ",\"repository_tree_oid\":null"
                      + ",\"working_tree_state\":null"
                      + ",\"source_root_aliases\":["
                      + "{\"absolute_root\":\"/abs-1\",\"canonical_root\":\"/can-1\"},"
                      + "{\"absolute_root\":\"/abs-2\",\"canonical_root\":\"/can-2\"},"
                      + "{\"absolute_root\":\"/abs-3\",\"canonical_root\":\"/can-3\"}"
                      + "]"
                      + ",\"dotnet_sdk_version\":null,\"msbuild_version\":null"
                      + ",\"fsharp_compiler_version\":null"
                      + ",\"operating_system\":null,\"architecture\":null,\"culture\":null"
                      + ",\"started_at\":null,\"completed_at\":null,\"exit_code\":null"
                      + ",\"metadata_gaps\":[]}"
                  writeAllText (Path.Combine(captureDir, "capture.json")) text
                  let r = readCaptureManifest (Path.Combine(captureDir, "capture.json"))
                  match r with
                  | Result.Ok manifest ->
                      Expect.equal (List.length manifest.SourceRootAliases) 3 "three aliases"
                      Expect.equal
                          (List.map (fun a -> a.AbsoluteRoot) manifest.SourceRootAliases)
                          [ "/abs-1"; "/abs-2"; "/abs-3" ]
                          "absolute_root order"
                  | Result.Error f -> failwithf "expected Ok, got %A" f
              finally
                  cleanup root
          }

          test "source-root alias: unknown alias field rejected" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  let text =
                      "{\"schema_version\":\""
                      + CaptureManifestSchemaVersion
                      + "\",\"capture_id\":\""
                      + captureId
                      + "\",\"capture_kind\":\"binlog\""
                      + ",\"raw_artifacts\":[\"x.binlog\"]"
                      + ",\"command\":null,\"working_directory\":null"
                      + ",\"repository_commit_oid\":null"
                      + ",\"repository_tree_oid\":null"
                      + ",\"working_tree_state\":null"
                      + ",\"source_root_aliases\":[{\"absolute_root\":\"/abs\",\"canonical_root\":\"/can\",\"oops\":42}]"
                      + ",\"dotnet_sdk_version\":null,\"msbuild_version\":null"
                      + ",\"fsharp_compiler_version\":null"
                      + ",\"operating_system\":null,\"architecture\":null,\"culture\":null"
                      + ",\"started_at\":null,\"completed_at\":null,\"exit_code\":null"
                      + ",\"metadata_gaps\":[]}"
                  writeAllText (Path.Combine(captureDir, "capture.json")) text
                  let r = readCaptureManifest (Path.Combine(captureDir, "capture.json"))
                  match r with
                  | Result.Error (CaptureManifestReadFailure.UnknownField (_, _, field)) ->
                      Expect.equal field "oops" "unknown field"
                  | _ -> failwithf "expected UnknownField"
              finally
                  cleanup root
          }

          test "source-root alias: duplicate alias field rejected" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  let text =
                      "{\"schema_version\":\""
                      + CaptureManifestSchemaVersion
                      + "\",\"capture_id\":\""
                      + captureId
                      + "\",\"capture_kind\":\"binlog\""
                      + ",\"raw_artifacts\":[\"x.binlog\"]"
                      + ",\"command\":null,\"working_directory\":null"
                      + ",\"repository_commit_oid\":null"
                      + ",\"repository_tree_oid\":null"
                      + ",\"working_tree_state\":null"
                      + ",\"source_root_aliases\":[{\"absolute_root\":\"/abs\",\"canonical_root\":\"/can\",\"absolute_root\":\"/abs2\"}]"
                      + ",\"dotnet_sdk_version\":null,\"msbuild_version\":null"
                      + ",\"fsharp_compiler_version\":null"
                      + ",\"operating_system\":null,\"architecture\":null,\"culture\":null"
                      + ",\"started_at\":null,\"completed_at\":null,\"exit_code\":null"
                      + ",\"metadata_gaps\":[]}"
                  writeAllText (Path.Combine(captureDir, "capture.json")) text
                  let r = readCaptureManifest (Path.Combine(captureDir, "capture.json"))
                  match r with
                  | Result.Error (CaptureManifestReadFailure.DuplicateField (_, _, field)) ->
                      Expect.equal field "absolute_root" "duplicate field"
                  | _ -> failwithf "expected DuplicateField"
              finally
                  cleanup root
          }

          test "source-root alias: escaped-equivalent duplicate alias field rejected" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  let text =
                      "{\"schema_version\":\""
                      + CaptureManifestSchemaVersion
                      + "\",\"capture_id\":\""
                      + captureId
                      + "\",\"capture_kind\":\"binlog\""
                      + ",\"raw_artifacts\":[\"x.binlog\"]"
                      + ",\"command\":null,\"working_directory\":null"
                      + ",\"repository_commit_oid\":null"
                      + ",\"repository_tree_oid\":null"
                      + ",\"working_tree_state\":null"
                      + ",\"source_root_aliases\":[{\"absolute_root\":\"/abs\",\"canonical_root\":\"/can\",\"\\u0061bsolute_root\":\"/abs2\"}]"
                      + ",\"dotnet_sdk_version\":null,\"msbuild_version\":null"
                      + ",\"fsharp_compiler_version\":null"
                      + ",\"operating_system\":null,\"architecture\":null,\"culture\":null"
                      + ",\"started_at\":null,\"completed_at\":null,\"exit_code\":null"
                      + ",\"metadata_gaps\":[]}"
                  writeAllText (Path.Combine(captureDir, "capture.json")) text
                  let r = readCaptureManifest (Path.Combine(captureDir, "capture.json"))
                  match r with
                  | Result.Error (CaptureManifestReadFailure.DuplicateField _) -> ()
                  | _ -> failwithf "expected DuplicateField"
              finally
                  cleanup root
          }

          test "source-root alias: missing absolute_root rejected" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  let text =
                      "{\"schema_version\":\""
                      + CaptureManifestSchemaVersion
                      + "\",\"capture_id\":\""
                      + captureId
                      + "\",\"capture_kind\":\"binlog\""
                      + ",\"raw_artifacts\":[\"x.binlog\"]"
                      + ",\"command\":null,\"working_directory\":null"
                      + ",\"repository_commit_oid\":null"
                      + ",\"repository_tree_oid\":null"
                      + ",\"working_tree_state\":null"
                      + ",\"source_root_aliases\":[{\"canonical_root\":\"/can\"}]"
                      + ",\"dotnet_sdk_version\":null,\"msbuild_version\":null"
                      + ",\"fsharp_compiler_version\":null"
                      + ",\"operating_system\":null,\"architecture\":null,\"culture\":null"
                      + ",\"started_at\":null,\"completed_at\":null,\"exit_code\":null"
                      + ",\"metadata_gaps\":[]}"
                  writeAllText (Path.Combine(captureDir, "capture.json")) text
                  let r = readCaptureManifest (Path.Combine(captureDir, "capture.json"))
                  match r with
                  | Result.Error (CaptureManifestReadFailure.MissingField (_, _, field)) ->
                      Expect.equal field "absolute_root" "missing field"
                  | _ -> failwithf "expected MissingField"
              finally
                  cleanup root
          }

          test "source-root alias: missing canonical_root rejected" {
              let root = newTempDir ()
              try
                  let captureDir = captureDirRoot root captureId
                  ensureDir captureDir
                  let text =
                      "{\"schema_version\":\""
                      + CaptureManifestSchemaVersion
                      + "\",\"capture_id\":\""
                      + captureId
                      + "\",\"capture_kind\":\"binlog\""
                      + ",\"raw_artifacts\":[\"x.binlog\"]"
                      + ",\"command\":null,\"working_directory\":null"
                      + ",\"repository_commit_oid\":null"
                      + ",\"repository_tree_oid\":null"
                      + ",\"working_tree_state\":null"
                      + ",\"source_root_aliases\":[{\"absolute_root\":\"/abs\"}]"
                      + ",\"dotnet_sdk_version\":null,\"msbuild_version\":null"
                      + ",\"fsharp_compiler_version\":null"
                      + ",\"operating_system\":null,\"architecture\":null,\"culture\":null"
                      + ",\"started_at\":null,\"completed_at\":null,\"exit_code\":null"
                      + ",\"metadata_gaps\":[]}"
                  writeAllText (Path.Combine(captureDir, "capture.json")) text
                  let r = readCaptureManifest (Path.Combine(captureDir, "capture.json"))
                  match r with
                  | Result.Error (CaptureManifestReadFailure.MissingField (_, _, field)) ->
                      Expect.equal field "canonical_root" "missing field"
                  | _ -> failwithf "expected MissingField"
              finally
                  cleanup root
          }
]
