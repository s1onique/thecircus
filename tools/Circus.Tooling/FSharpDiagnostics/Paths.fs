module Circus.Tooling.FSharpDiagnostics.Paths

/// Convert any path separator to forward slash.
let toPosix (path: string) : string =
    path.Replace('\\', '/')

/// Extract the filename from a repository-relative posix path.
let filenameOf (path: string) : string =
    let n = toPosix path
    let i = n.LastIndexOf '/'
    if i < 0 then n else n.Substring(i + 1)

/// Extract the lowercase file extension (including the leading dot).
let extensionOf (path: string) : string =
    let name = filenameOf path
    let i = name.LastIndexOf '.'
    if i <= 0 then "" else name.Substring(i).ToLowerInvariant()

/// Canonical, deterministic, repository-relative path with forward slashes.
let canonicalise (relativePath: string) : string =
    let pieces =
        (toPosix relativePath).Split([| '/' |], System.StringSplitOptions.RemoveEmptyEntries)
    let stack = System.Collections.Generic.Stack<string>()
    for piece in pieces do
        if piece = "." then ()
        elif piece = ".." then
            if stack.Count > 0 then stack.Pop() |> ignore
        else stack.Push piece
    let ordered = stack.ToArray() |> Array.rev
    String.concat "/" ordered

/// True when the path is absolute (uses System.IO.Path.IsPathRooted).
let isAbsolute (path: string) : bool =
    System.IO.Path.IsPathRooted path

/// Canonical root for the tracked F# diagnostics corpus.
let CanonicalCorpusRoot = "factory/evidence/fsharp-diagnostics"

/// Canonical subdirectory for raw captures.
let rawSubdir = CanonicalCorpusRoot + "/corpus/raw"

/// Canonical subdirectory for normalized outputs.
let normalizedSubdir = CanonicalCorpusRoot + "/corpus/normalized"

/// Canonical subdirectory for manifests.
let manifestsSubdir = CanonicalCorpusRoot + "/corpus/manifests"

/// Canonical subdirectory for schemas.
let schemasSubdir = CanonicalCorpusRoot + "/schemas"

/// Canonical subdirectory for fixtures.
let fixturesSubdir = CanonicalCorpusRoot + "/fixtures"

/// Canonical file name for the artifact manifest (jsonl).
let artifactsManifestFile = "artifacts-v1.jsonl"

/// Canonical file name for the migration map (tsv).
let migrationMapFile = "migration-map-v1.tsv"

/// Canonical file name for occurrences jsonl.
let occurrencesFile = "occurrences-v1.jsonl"

/// Canonical file name for fingerprints tsv.
let fingerprintsFile = "exact-fingerprints-v1.tsv"

/// Canonical file name for duplicates tsv.
let duplicatesFile = "duplicate-occurrences-v1.tsv"

/// Canonical file name for corpus summary json.
let summaryFile = "corpus-summary-v1.json"

/// Combine repository root with a repository-relative posix path.
let repoRelative (repoRoot: string) (relativePath: string) : string =
    System.IO.Path.GetFullPath(
        System.IO.Path.Combine(repoRoot, (toPosix relativePath).TrimStart('/')))

/// True when `relative` lives inside the canonical corpus root.
let isUnderCanonicalCorpus (relative: string) : bool =
    let n = toPosix relative
    n = CanonicalCorpusRoot
    || n.StartsWith(CanonicalCorpusRoot + "/", System.StringComparison.Ordinal)

/// Non-authoritative scratch root that must be ignored.
let FactoryScratchRoot = ".factory"

/// True when `relative` lives inside the factory scratch root.
let isUnderFactoryScratch (relative: string) : bool =
    let n = toPosix relative
    n = FactoryScratchRoot
    || n.StartsWith(FactoryScratchRoot + "/", System.StringComparison.Ordinal)
