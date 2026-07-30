module Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Episodes

open System.IO
open System.Text
open Circus.Tooling.FSharpDiagnostics.Domain
open Circus.Tooling.FSharpDiagnostics.Hashing
open Circus.Tooling.FSharpDiagnostics.LegacyTextExtractor
open Circus.Tooling.FSharpDiagnostics.Manifest
open Circus.Tooling.FSharpDiagnostics.OccurrenceIdentity
open Circus.Tooling.FSharpDiagnostics.Paths
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.OccurrenceReader
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Paths

// =============================================================================
// Capture loading and command-contract fingerprinting
// =============================================================================

type CaptureSummary =
    { CaptureId: string
      CaptureRelativeDir: string
      Manifest: CaptureManifest
      Occurrences: DiagnosticOccurrence list
      BinlogReplayFailure: string option
      LegacyParseResult: LegacyParseResult option
      RawArtifactHashes: Map<string, string> }

/// Compute a deterministic command contract fingerprint from evidence-backed
/// fields.  Unknown fields cause the contract to be ``unknown``.
let commandContract (m: CaptureManifest) : string =
    let missing =
        [ if m.Command.IsNone then
              yield "command"
          if m.WorkingDirectory.IsNone then
              yield "working_directory"
          if m.DotnetSdkVersion.IsNone then
              yield "dotnet_sdk_version"
          if m.MsbuildVersion.IsNone then
              yield "msbuild_version"
          if m.FsharpCompilerVersion.IsNone then
              yield "fsharp_compiler_version"
          if m.OperatingSystem.IsNone then
              yield "operating_system"
          if m.Architecture.IsNone then
              yield "architecture"
          if m.Culture.IsNone then
              yield "culture" ]

    if not (List.isEmpty missing) then
        "unknown:" + (String.concat "," missing)
    else
        // The contract is constructed by hashing the normalised concatenation
        // of fields using length-prefixed framing.
        let sb = StringBuilder()

        let prefix (s: string) =
            sb.Append(s.Length.ToString("x8", System.Globalization.CultureInfo.InvariantCulture))
            |> ignore

            sb.Append(':') |> ignore
            sb.Append s |> ignore

        prefix "command-contract-v1"
        prefix (Option.get m.Command)
        prefix (canonicalise (Option.get m.WorkingDirectory))
        prefix (Option.get m.DotnetSdkVersion)
        prefix (Option.get m.MsbuildVersion)
        prefix (Option.get m.FsharpCompilerVersion)
        prefix (Option.get m.OperatingSystem)
        prefix (Option.get m.Architecture)
        prefix (Option.get m.Culture)
        sha256OfUtf8 (sb.ToString())

/// Hash every raw artefact under the capture directory and return a
/// (relative-raw-artifact-name -> sha256) map.
let private rawArtifactHashes
    (repoRoot: string)
    (captureRelativeDir: string)
    (m: CaptureManifest)
    : Map<string, string> =
    let captureFullDir = repoRelative repoRoot captureRelativeDir
    let dict = System.Collections.Generic.Dictionary<string, string>()

    for raw in m.RawArtifacts do
        let p = repoRelative repoRoot (captureRelativeDir + "/" + raw)

        if File.Exists p then
            dict.[raw] <- sha256OfFile p

    dict |> Seq.toList |> List.map (fun kv -> kv.Key, kv.Value) |> Map.ofList

/// Load occurrence artifacts declared in the capture manifest.
/// For legacy captures, this reads the capture-specific occurrence file
/// (e.g., legacy-occurrences.jsonl) if declared in raw_artifacts.
let private loadCaptureOccurrences
    (repoRoot: string)
    (captureId: string)
    (manifest: CaptureManifest)
    : DiagnosticOccurrence list =
    let captureRelativeDir = rawSubdir + "/" + captureId
    let mutable occurrences: DiagnosticOccurrence list = []

    for rawArtifact in manifest.RawArtifacts do
        // Look for occurrence artifacts by extension
        let fullPath = repoRelative repoRoot (captureRelativeDir + "/" + rawArtifact)

        if
            rawArtifact.EndsWith("-occurrences.jsonl", System.StringComparison.OrdinalIgnoreCase)
            && File.Exists fullPath
        then
            match readOccurrences fullPath with
            | Result.Ok occs ->
                // Filter to only occurrences for this capture
                let filtered = occs |> List.filter (fun o -> o.CaptureId = captureId)
                occurrences <- occurrences @ filtered
            | Result.Error _ ->
                // If we can't read the occurrence file, continue without occurrences
                // The failure will be caught by the binding layer
                ()

    occurrences

/// Load one capture manifest by its capture id.  Returns ``None`` when the
/// capture directory or its manifest is absent.
let tryLoadCapture (repoRoot: string) (captureId: string) : CaptureSummary option =
    let captureRelativeDir = rawSubdir + "/" + captureId
    let manifestPath = repoRelative repoRoot (captureRelativeDir + "/capture.json")

    if not (File.Exists manifestPath) then
        None
    else
        let manifest = readCaptureManifest manifestPath
        let hashes = rawArtifactHashes repoRoot captureRelativeDir manifest
        let occurrences = loadCaptureOccurrences repoRoot captureId manifest

        Some
            { CaptureId = captureId
              CaptureRelativeDir = captureRelativeDir
              Manifest = manifest
              Occurrences = occurrences
              BinlogReplayFailure = None
              LegacyParseResult = None
              RawArtifactHashes = hashes }

// =============================================================================
// Declaration IO
// =============================================================================

let private loadDeclarationsFromDir (repoRoot: string) (dir: string) : (string * string) list =
    let fullDir = repoRelative repoRoot dir

    if not (Directory.Exists fullDir) then
        []
    else
        Directory.EnumerateFiles(fullDir, "*.json", SearchOption.TopDirectoryOnly)
        |> Seq.toList
        |> List.map (fun p -> p, File.ReadAllText p)

let enumerateDeclarationPaths (repoRoot: string) : string list =
    Directory.EnumerateFiles(repoRelative repoRoot declarationDirCanonicalPath, "*.json", SearchOption.TopDirectoryOnly)
    |> Seq.toList
    |> List.map (fun p ->
        p.Substring(repoRoot.Length).TrimStart('/', '\\')
        |> Circus.Tooling.FSharpDiagnostics.Paths.toPosix
        |> Circus.Tooling.FSharpDiagnostics.Paths.canonicalise)
    |> List.sort

let readDeclaration (path: string) : string = File.ReadAllText path

/// Compute the canonical episode identity SHA-256 over a length-prefixed
/// encoding of the binding inputs (excluding notes, generation time, etc.).
let computeEpisodeId
    (beforeCaptureId: string)
    (afterCaptureId: string)
    (beforeTreeOid: string)
    (afterTreeOid: string)
    (changeSetId: string)
    : string =
    let sb = StringBuilder()

    let prefix (s: string) =
        sb.Append(s.Length.ToString("x8", System.Globalization.CultureInfo.InvariantCulture))
        |> ignore

        sb.Append(':') |> ignore
        sb.Append s |> ignore

    prefix RepairEpisodeSchemaVersion
    prefix beforeCaptureId
    prefix afterCaptureId
    prefix beforeTreeOid
    prefix afterTreeOid
    prefix changeSetId
    sha256OfUtf8 (sb.ToString())
