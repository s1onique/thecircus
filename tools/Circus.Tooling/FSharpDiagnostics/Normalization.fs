module Circus.Tooling.FSharpDiagnostics.Normalization

open System.Text
open Circus.Tooling.FSharpDiagnostics.Domain

// =============================================================================
// Message normalization (pure)
// =============================================================================

/// Convert CRLF and lone CR sequences to LF. Tab is preserved as-is.
let private normalizeLineEndings (text: string) : string =
    if text.Contains "\r" then
        text.Replace("\r\n", "\n").Replace('\r', '\n')
    else
        text

/// Replace declared repository-root aliases with the canonical alias token
/// "<REPO>".  Aliases are compared using ordinal, case-sensitive matching and
/// must match either exactly or be followed by '/' to avoid prefix collisions
/// like "/home/me" matching "/home/me-other".  Returns the substituted text
/// and a boolean indicating whether substitution happened.  Unknown absolute
/// paths are left untouched so extraction can fail closed later.
let private applyAliases
    (aliases: SourceRootAlias list)
    (text: string)
    : string =
    let mutable result = text
    for alias in aliases do
        let abs = alias.AbsoluteRoot
        let absWithSep =
            if abs.EndsWith("/") then abs else abs + "/"
        let absExact = abs
        // Replace alias-prefixed occurrences only; standalone exact occurrences
        // are replaced too but only when the result still begins with "/"
        // (i.e. it is an absolute path).
        if result.Contains(absWithSep) then
            result <- result.Replace(absWithSep, "<REPO>/")
        if result.StartsWith(absExact, System.StringComparison.Ordinal) then
            // Exact match (no trailing separator) — replace only the prefix.
            result <- "<REPO>" + result.Substring(absExact.Length)
    result

/// Convert path separators to '/' only in substrings that look like paths
/// containing directory components (i.e. contain at least one '/').  All
/// backslashes in such substrings are replaced.
let private normalizePathSeparators (text: string) : string =
    if text.Contains("\\") then
        text.Replace('\\', '/')
    else
        text

/// Pure message normalization pipeline. The pipeline performs ONLY:
///   1. CRLF and CR → LF
///   2. Repository-root alias substitution
///   3. Path separator normalization ('\\' → '/') in the whole string
///
/// It does NOT lowercase, trim, replace numbers, remove punctuation, or
/// alter diagnostic semantics.
let normalizeMessage
    (aliases: SourceRootAlias list)
    (raw: string)
    : string =
    raw
    |> normalizeLineEndings
    |> applyAliases aliases
    |> normalizePathSeparators

// =============================================================================
// Absolute path resolution
// =============================================================================

/// True when `path` begins with one of the alias absolute roots. Matches
/// either exactly or followed by '/' so that "/home/me" does not match
/// "/home/me-other".
let matchesDeclaredAlias (aliases: SourceRootAlias list) (path: string) : bool =
    let mutable found = false
    for alias in aliases do
        let abs = alias.AbsoluteRoot
        if path = abs then
            found <- true
        elif path.StartsWith(abs, System.StringComparison.Ordinal) then
            let after = path.Substring(abs.Length)
            if after.StartsWith("/") || after.StartsWith(System.IO.Path.DirectorySeparatorChar) then
                found <- true
    found

/// Resolve `path` through declared aliases. Returns the substituted path or
/// the original path. When the path is absolute and not declared, the
/// original is returned unchanged so callers can fail closed.
let resolveThroughAliases (aliases: SourceRootAlias list) (path: string) : string =
    if matchesDeclaredAlias aliases path then
        normalizeMessage aliases path
    else
        path

/// True when `path` contains an absolute path that is not declared in
/// `aliases`. Used to fail closed on undeclared absolute paths in legacy
/// captures.
let containsUndeclaredAbsolutePath (aliases: SourceRootAlias list) (path: string) : bool =
    if not (System.IO.Path.IsPathRooted path) then false
    elif matchesDeclaredAlias aliases path then false
    else true

// =============================================================================
// Source-root alias declarations are themselves normalized: absolute paths
// have their separators converted to '/' so the manifest is canonical.
// =============================================================================

let canonicalizeAlias (alias: SourceRootAlias) : SourceRootAlias =
    { AbsoluteRoot = alias.AbsoluteRoot.Replace('\\', '/')
      CanonicalRoot = alias.CanonicalRoot.Replace('\\', '/') }