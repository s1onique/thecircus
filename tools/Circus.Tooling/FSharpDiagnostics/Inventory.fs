module Circus.Tooling.FSharpDiagnostics.Inventory

open System.IO
open Circus.Tooling.FSharpDiagnostics.Domain
open Circus.Tooling.FSharpDiagnostics.Hashing
open Circus.Tooling.FSharpDiagnostics.Paths

/// One discovered file together with classification metadata.
type Discovered = {
    RelativePath: string
    FullPath: string
    ByteLength: int64
    Sha256: string
}

/// Enumerate the canonical corpus root, returning only files (recursively).
let enumerateFiles (repoRoot: string) (relativeRoot: string) : Discovered list =
    let root = repoRelative repoRoot relativeRoot
    if not (Directory.Exists root) then []
    else
        let results = System.Collections.Generic.List<Discovered>()
        let stack = System.Collections.Generic.Stack<string>()
        stack.Push root
        while stack.Count > 0 do
            let dir = stack.Pop()
            for entry in Directory.EnumerateFiles dir do
                let info = FileInfo entry
                let rel =
                    info.FullName.Substring(repoRoot.Length).TrimStart('/', '\\')
                    |> toPosix
                    |> canonicalise
                let sha = sha256OfFile info.FullName
                results.Add(
                    { RelativePath = rel
                      FullPath = info.FullName
                      ByteLength = info.Length
                      Sha256 = sha })
            for sub in Directory.EnumerateDirectories dir do
                stack.Push sub
        results
        |> Seq.sortBy (fun d -> d.RelativePath)
        |> Seq.toList

/// Enumerate the canonical corpus root.
let enumerateCanonical (repoRoot: string) : Discovered list =
    enumerateFiles repoRoot CanonicalCorpusRoot

/// Enumerate the .factory scratch root (for visibility into non-authoritative
/// scratch). Used only by inventory/verify to record that these files exist
/// without treating them as evidence.
let enumerateFactoryScratch (repoRoot: string) : Discovered list =
    enumerateFiles repoRoot FactoryScratchRoot

/// Legacy authoritative locations.  In this ACT there are no such locations
/// tracked; we surface this as an empty list.  Inventory code uses this hook
/// so that any future legacy authoritative location can be added without
/// rewriting downstream code.
let enumerateLegacyAuthoritative (repoRoot: string) : Discovered list =
    // ACT-CIRCUS-FSHARP-DIAGNOSTIC-CORPUS-FOUNDATION01 establishes the
    // canonical root; historical FSB artefacts were not tracked in this
    // repository.  The hook remains for future corrections.
    []

/// Media type heuristic from the file extension.  Conservative: returns
/// "application/octet-stream" for unknown extensions.
let mediaTypeFor (relativePath: string) : string =
    match extensionOf relativePath with
    | ".json" -> "application/json"
    | ".jsonl" -> "application/x-ndjson"
    | ".tsv" -> "text/tab-separated-values"
    | ".txt" -> "text/plain"
    | ".log" -> "text/plain"
    | ".binlog" -> "application/x-msbuild-binarylog"
    | ".bin" -> "application/octet-stream"
    | ".yaml" -> "application/yaml"
    | ".yml" -> "application/yaml"
    | ".fs" -> "text/x-fsharp"
    | ".fsproj" -> "text/x-fsharp-project"
    | _ -> "application/octet-stream"

/// Classify one canonical artefact relative to its path inside the corpus.
/// Heuristic only; callers may override.
let classifyCanonicalPath (relativePath: string) : ArtifactClass =
    let normalized = toPosix relativePath
    let underRaw = normalized.Contains("/corpus/raw/")
    let underNormalized = normalized.Contains("/corpus/normalized/")
    let underManifests = normalized.Contains("/corpus/manifests/")
    let underSchemas = normalized.Contains("/schemas/")
    let underFixtures = normalized.Contains("/fixtures/")
    if underSchemas then ArtifactClass.Derived
    elif underRaw then ArtifactClass.Raw
    elif underNormalized then ArtifactClass.Normalized
    elif underManifests then ArtifactClass.Normalized
    elif underFixtures then ArtifactClass.SourceSnapshot
    else ArtifactClass.Derived

/// Authority for one path relative to the repo root.  The canonical root
/// is canonical corpus authority; the .factory root is non-authoritative
/// scratch; everything else is currently unclassified.
let authorityFor (relativePath: string) : ArtifactAuthority =
    if isUnderCanonicalCorpus relativePath then CanonicalCorpus
    elif isUnderFactoryScratch relativePath then NonAuthoritativeScratch
    else Unclassified

/// True when `path` looks like a captured binlog artefact (binlog extension
/// under the canonical raw directory or a "build.binlog" file name).
let isBinlogArtefact (path: string) : bool =
    let ext = extensionOf path
    let name = filenameOf path
    ext = ".binlog" || name = "build.binlog"

/// True when `path` looks like a captured text artefact (".log" / ".txt" /
/// captured stdout/stderr binaries).
let isLegacyTextArtefact (path: string) : bool =
    let ext = extensionOf path
    ext = ".log" || ext = ".txt" || ext = ".out"

/// Discover capture directories under the canonical corpus raw root.
/// A capture is any directory under `corpus/raw/<capture-id>/` that
/// contains a `capture.json` manifest.
let discoverCaptures (repoRoot: string) : string list =
    let rawRoot = repoRelative repoRoot rawSubdir
    if not (Directory.Exists rawRoot) then []
    else
        Directory.EnumerateDirectories rawRoot
        |> Seq.filter (fun d -> File.Exists(Path.Combine(d, "capture.json")))
        |> Seq.map (fun d ->
            let rel =
                d.Substring(repoRoot.Length).TrimStart('/', '\\')
                |> toPosix
                |> canonicalise
            rel)
        |> Seq.sortBy id
        |> Seq.toList