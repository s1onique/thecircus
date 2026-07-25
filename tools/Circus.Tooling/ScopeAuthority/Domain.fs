module Circus.Tooling.ScopeAuthority.Domain

// =============================================================================
// ACT-scope authority — pure domain
//
// ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01-CORRECTION02
//
// The ACT-scope authority is the canonical reference for the
// ``protected-scope`` check.  The authority is resolved from one of
// two sources, in priority order:
//
//   1. The ``--scope-declaration <path>`` CLI argument supplied to
//      ``canonical-evidence regenerate`` or
//      ``canonical-evidence verify``.
//
//   2. The tracked repository pointer ``.factory/active-scope.json``
//      which contains a strict JSON object with the validated
//      fields listed below.
//
// If neither source is present, the protected-scope check is
// unavailable and any canonical evidence regeneration must fail
// closed; the provider does NOT silently reuse a predecessor ACT's
// scope declaration.
//
// Validated fields of the active-scope pointer:
//
//   * ``act_id``                       — opaque string identity of the
//                                         active ACT
//   * ``declaration_path``              — repository-relative POSIX
//                                         path to the ACT-scope
//                                         declaration JSON file
//   * ``declaration_blob_oid``          — full OID of the declaration
//                                         file as it appears in the
//                                         active ACT's commit
//   * ``baseline_commit_oid``           — full OID of the active
//                                         ACT's baseline commit
//
// All four fields are mandatory.  Any missing field, wrong type, or
// malformed value fails the canonical evidence regeneration with a
// non-zero exit code.
// =============================================================================

open System

// -----------------------------------------------------------------------------
// Raw pointer (parsed from JSON)
// -----------------------------------------------------------------------------

type RawActiveScope = {
    ActId: string
    DeclarationPath: string
    DeclarationBlobOid: string
    BaselineCommitOid: string
}

// -----------------------------------------------------------------------------
// Resolution result
// -----------------------------------------------------------------------------

type ScopeResolution =
    | ResolvedFromCli of declarationPath: string * activeScope: RawActiveScope option
    | ResolvedFromPointer of declarationPath: string * activeScope: RawActiveScope
    | MissingActiveScope
    | AmbiguousActiveScope of detail: string

// -----------------------------------------------------------------------------
// Parse errors
// -----------------------------------------------------------------------------

type ScopePointerError =
    | FileMissing of path: string
    | NotJsonObject of path: string
    | JsonParseFailed of message: string
    | MissingField of name: string
    | InvalidField of name: string * detail: string
    | InvalidOid of name: string * value: string
    | InvalidRelativePath of name: string * value: string
    | PathNotNormalized of name: string * value: string

let scopePointerErrorToString (e: ScopePointerError) : string =
    match e with
    | FileMissing p -> sprintf "active-scope file not found: %s" p
    | NotJsonObject p -> sprintf "active-scope file is not a JSON object: %s" p
    | JsonParseFailed msg -> sprintf "active-scope JSON parse failed: %s" msg
    | MissingField name -> sprintf "active-scope missing required field: %s" name
    | InvalidField (name, detail) -> sprintf "active-scope invalid field %s: %s" name detail
    | InvalidOid (name, value) -> sprintf "active-scope invalid OID for %s: %s" name value
    | InvalidRelativePath (name, value) -> sprintf "active-scope invalid relative path for %s: %s" name value
    | PathNotNormalized (name, value) -> sprintf "active-scope non-normalized path for %s: %s" name value

// -----------------------------------------------------------------------------
// Validation
// -----------------------------------------------------------------------------

let Sha1Width = 40
let Sha256Width = 64

let isLikelyValidOid (value: string) : bool =
    let len = value.Length
    (len = Sha1Width || len = Sha256Width)
    && value |> Seq.forall (fun c -> System.Char.IsLetterOrDigit c)

let private isNormalizedRelativePosixPath (path: string) : bool =
    if String.IsNullOrEmpty path then
        false
    elif path.Contains("\\\\") || path.Contains("\\") then
        // backslashes are forbidden; only forward slashes
        false
    elif path.StartsWith "/" then
        // absolute path is forbidden
        false
    elif System.IO.Path.IsPathRooted(path) then
        // any rooted path is forbidden
        false
    else
        // no backslash, no leading slash, no dot-segments, no empty
        // segments
        let segments = path.Split('/')
        let mutable ok = true
        for seg in segments do
            if seg = "." || seg = ".." || String.IsNullOrEmpty seg then
                ok <- false
        ok

let validateRawPointer (raw: RawActiveScope) : Result<unit, ScopePointerError> =
    if String.IsNullOrEmpty raw.ActId then
        Error (MissingField "act_id")
    elif not (isLikelyValidOid raw.DeclarationBlobOid) then
        Error (InvalidOid ("declaration_blob_oid", raw.DeclarationBlobOid))
    elif not (isLikelyValidOid raw.BaselineCommitOid) then
        Error (InvalidOid ("baseline_commit_oid", raw.BaselineCommitOid))
    elif String.IsNullOrEmpty raw.DeclarationPath then
        Error (MissingField "declaration_path")
    elif not (isNormalizedRelativePosixPath raw.DeclarationPath) then
        Error (PathNotNormalized ("declaration_path", raw.DeclarationPath))
    else
        Ok ()
