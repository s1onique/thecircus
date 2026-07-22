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

/// Repository-relative root for the tracked F# diagnostics corpus.
let canonicalRootRelative = "factory/evidence/fsharp-diagnostics"

/// Backwards-compatible name for the repository-relative canonical corpus root.
let CanonicalCorpusRoot = canonicalRootRelative

/// Canonical subdirectory for raw captures, relative to the repository root.
let rawSubdir = canonicalRootRelative + "/corpus/raw"

/// Normalized-output directory relative to the canonical corpus root.
let normalizedCorpusRelativeSubdir = "corpus/normalized"

/// Canonical subdirectory for normalized outputs, relative to the repository root.
let normalizedSubdir = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir

/// Canonical subdirectory for manifests, relative to the repository root.
let manifestsSubdir = canonicalRootRelative + "/corpus/manifests"

/// Canonical subdirectory for schemas, relative to the repository root.
let schemasSubdir = canonicalRootRelative + "/schemas"

/// Canonical subdirectory for fixtures, relative to the repository root.
let fixturesSubdir = canonicalRootRelative + "/fixtures"

/// Leaf filename for the artifact manifest (JSONL).
let artifactsManifestFile = "artifacts-v1.jsonl"

/// Artifact-manifest path relative to the canonical corpus root.
let artifactsManifestCorpusRelativePath =
    normalizedCorpusRelativeSubdir + "/" + artifactsManifestFile

/// Artifact-manifest canonical path relative to the repository root.
let artifactsManifestCanonicalPath =
    canonicalRootRelative + "/" + artifactsManifestCorpusRelativePath

/// Leaf filename for the migration map (TSV).
let migrationMapFile = "migration-map-v1.tsv"

/// Migration-map path relative to the canonical corpus root.
let migrationMapCorpusRelativePath =
    normalizedCorpusRelativeSubdir + "/" + migrationMapFile

/// Migration-map canonical path relative to the repository root.
let migrationMapCanonicalPath =
    canonicalRootRelative + "/" + migrationMapCorpusRelativePath

/// Leaf filename for diagnostic occurrences (JSONL).
let occurrencesFile = "occurrences-v1.jsonl"

/// Diagnostic-occurrences path relative to the canonical corpus root.
let occurrencesCorpusRelativePath =
    normalizedCorpusRelativeSubdir + "/" + occurrencesFile

/// Diagnostic-occurrences canonical path relative to the repository root.
let occurrencesCanonicalPath =
    canonicalRootRelative + "/" + occurrencesCorpusRelativePath

/// Leaf filename for exact fingerprints (TSV).
let fingerprintsFile = "exact-fingerprints-v1.tsv"

/// Exact-fingerprints path relative to the canonical corpus root.
let fingerprintsCorpusRelativePath =
    normalizedCorpusRelativeSubdir + "/" + fingerprintsFile

/// Exact-fingerprints canonical path relative to the repository root.
let fingerprintsCanonicalPath =
    canonicalRootRelative + "/" + fingerprintsCorpusRelativePath

/// Leaf filename for duplicate occurrences (TSV).
let duplicatesFile = "duplicate-occurrences-v1.tsv"

/// Duplicate-occurrences path relative to the canonical corpus root.
let duplicatesCorpusRelativePath =
    normalizedCorpusRelativeSubdir + "/" + duplicatesFile

/// Duplicate-occurrences canonical path relative to the repository root.
let duplicatesCanonicalPath =
    canonicalRootRelative + "/" + duplicatesCorpusRelativePath

/// Leaf filename for the corpus summary (JSON).
let summaryFile = "corpus-summary-v1.json"

/// Corpus-summary path relative to the canonical corpus root.
let summaryCorpusRelativePath =
    normalizedCorpusRelativeSubdir + "/" + summaryFile

/// Corpus-summary canonical path relative to the repository root.
let summaryCanonicalPath =
    canonicalRootRelative + "/" + summaryCorpusRelativePath

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
