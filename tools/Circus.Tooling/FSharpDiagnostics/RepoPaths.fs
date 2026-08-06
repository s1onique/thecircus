module Circus.Tooling.FSharpDiagnostics.RepoPaths

open System

// =============================================================================
// Repository path normalization
// =============================================================================
//
// Single shared authority for normalizing repository-prefixed paths.
//
// The original ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-EXTRACTION01
// duplicated `Substring(7)` across the Classification, Selection, and Engine
// modules.  The duplication let each consumer drift independently.  This
// module is the only place where the `<REPO>` prefix constant, its measured
// length, and the comparison logic live.  Every consumer (grouping,
// classification, selection, identity, and verification) must call
// `normalizeRepositoryPath` instead of inlining its own copy.

/// The literal prefix used to mark repository-relative paths.
let [<Literal>] repositoryPathPrefix = "<REPO>"

/// The measured length of `repositoryPathPrefix` plus the path separator.
/// The literal must remain `<REPO>/`; do not introduce a typo here.
let [<Literal>] repositoryPathPrefixLength = 7

/// Returns true when the path begins with the canonical `<REPO>/` prefix.
/// Ordinal comparison is mandatory so the test never changes on locale.
let hasRepositoryPrefix (path: string) : bool =
    if String.length path < repositoryPathPrefixLength then
        false
    else
        String.CompareOrdinal(path.Substring(0, repositoryPathPrefixLength), repositoryPathPrefix + "/") = 0

/// Strip the `<REPO>/` prefix when present.  Otherwise return the input
/// unchanged.  The operation is idempotent and never alters a path that
/// lacks the exact `<REPO>/` prefix.
///
/// Specifically:
///   * `<REPO>/a.fs`        -> `a.fs`
///   * `a.fs`               -> `a.fs`
///   * `<REPO>`             -> `<REPO>`     (no separator → untouched)
///   * `<REPOSITORY>/a.fs`  -> `<REPOSITORY>/a.fs`  (different prefix → untouched)
///   * `""`                 -> `""`
let normalizeRepositoryPath (path: string) : string =
    if hasRepositoryPrefix path then
        path.Substring(repositoryPathPrefixLength)
    else
        path
