module Circus.Tooling.CanonicalEvidence.Domain

// =============================================================================
// Canonical evidence – pure domain
//
// ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER-FOUNDATION01-CORRECTION01
// Slice 1: types and pure functions only.
//
// This module has no dependencies on System.Diagnostics.Process, no
// filesystem IO, no subprocess execution. Every value here is a pure
// function of its inputs so the slice compiles before the execution
// adapter is added.
// =============================================================================

open System
open System.Globalization
open System.Text

open Circus.Tooling.FSharpDiagnostics.Hashing

// -----------------------------------------------------------------------------
// Status
// -----------------------------------------------------------------------------

type EvidenceStatus =
    | Pass
    | Fail
    | Unavailable

let statusToken (s: EvidenceStatus) : string =
    match s with
    | Pass -> "pass"
    | Fail -> "fail"
    | Unavailable -> "unavailable"

let tryParseStatus (token: string) : EvidenceStatus option =
    match token with
    | "pass" -> Some Pass
    | "fail" -> Some Fail
    | "unavailable" -> Some Unavailable
    | _ -> None

// -----------------------------------------------------------------------------
// Check definitions
// -----------------------------------------------------------------------------

type EvidenceCheckDefinition = {
    Id: string
    Executable: string
    Arguments: string list
    WorkingDirectory: string
    Required: bool
    Timeout: TimeSpan
    StdoutLimitBytes: int
    StderrLimitBytes: int
}

// -----------------------------------------------------------------------------
// Check results
// -----------------------------------------------------------------------------

type EvidenceCheckResult = {
    Id: string
    CommandArgv: string list
    WorkingDirectory: string
    DurationMilliseconds: int64
    ExitCode: int option
    Status: EvidenceStatus
    StdoutSha256: string option
    StderrSha256: string option
    FailureKind: string option
}

// -----------------------------------------------------------------------------
// Canonical evidence
// -----------------------------------------------------------------------------

type CanonicalEvidence = {
    SchemaVersion: int
    ProviderName: string
    ProviderVersion: string
    TestedCommitOid: string
    TestedTreeOid: string
    ObjectFormat: string
    Checks: EvidenceCheckResult list
    OverallStatus: EvidenceStatus
    SemanticSha256: string
}

// -----------------------------------------------------------------------------
// Constants
// -----------------------------------------------------------------------------

[<Literal>]
let SchemaVersionValue : int = 1

[<Literal>]
let ProviderNameValue : string = "circus-canonical-evidence"

[<Literal>]
let ProviderVersionValue : string = "1.0.0"

// -----------------------------------------------------------------------------
// Supported check IDs (the canonical check set)
//
// Any check not in this set is rejected by validation. The order of
// this list is the canonical registration order; checks in the wire
// document are sorted by ID regardless.
// -----------------------------------------------------------------------------

let SupportedCheckIds : string list =
    [
        "tooling-build"
        "tooling-tests-build"
        "bounded-process-tests"
        "git-adapter-tests"
        "repair-episodes-tests"
        "fsharp-diagnostics-tests"
        "repair-episodes-gate"
        "committed-range-diff-check"
        "protected-scope"
    ]

let SupportedCheckIdSet : Set<string> =
    Set.ofList SupportedCheckIds

let isSupportedCheckId (id: string) : bool =
    Set.contains id SupportedCheckIdSet

// -----------------------------------------------------------------------------
// OID validation
// -----------------------------------------------------------------------------

[<Literal>]
let Sha1Width : int = 40

[<Literal>]
let Sha256Width : int = 64

let supportedObjectFormats : Set<string> =
    Set.ofList [ "sha1"; "sha256" ]

let parseObjectFormat (token: string) : string option =
    match token with
    | "sha1" -> Some "sha1"
    | "sha256" -> Some "sha256"
    | _ -> None

let objectFormatWidth (objectFormat: string) : int =
    match objectFormat with
    | "sha1" -> Sha1Width
    | "sha256" -> Sha256Width
    | _ -> -1

let private isLowerHex (c: char) : bool =
    (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f')

let isValidOid (objectFormat: string) (oid: string) : bool =
    if isNull oid then false
    elif String.IsNullOrWhiteSpace oid then false
    else
        let expectedWidth = objectFormatWidth objectFormat
        if expectedWidth < 0 then false
        elif oid.Length <> expectedWidth then false
        else
            let mutable ok = true
            for c in oid do
                if not (isLowerHex c) then
                    ok <- false
            ok

// -----------------------------------------------------------------------------
// Overall status
//
// Required fail/unavailable propagates to overall fail. Optional
// unavailability must NOT be papered over into a pass; the overall
// verdict becomes fail when ANY check is unavailable (the optional
// requirement is that providers do not pretend that an unavailable
// check is a clean pass).
// -----------------------------------------------------------------------------

let computeOverallStatus (checks: EvidenceCheckResult list) : EvidenceStatus =
    let mutable failed = false
    let mutable unavailable = false
    for c in checks do
        match c.Status with
        | Fail -> failed <- true
        | Unavailable -> unavailable <- true
        | Pass -> ()
    if failed || unavailable then Fail
    else Pass

// -----------------------------------------------------------------------------
// Deterministic ordering
// -----------------------------------------------------------------------------

let sortChecksDeterministic (checks: EvidenceCheckResult list) : EvidenceCheckResult list =
    checks
    |> List.sortBy (fun c -> c.Id, c.CommandArgv)

// -----------------------------------------------------------------------------
// Self-referential / post-publication fields
//
// The canonical evidence schema is intentionally narrow. Any field
// in the wire document not accepted by the schema is rejected during
// deserialization. The list below records the names of fields that
// MUST NOT appear in a pre-publication canonical evidence document.
// Their presence indicates post-publication evidence bundled into a
// pre-publication file.
// -----------------------------------------------------------------------------

let ForbiddenIdentityFields : string list =
    [
        "tag_object_oid"
        "tag_target_oid"
        "tag_target_tree_oid"
        "tag_peeled_commit_oid"
        "tag_peeled_tree_oid"
        "remote_tag_object_oid"
        "push_target_oid"
        "publication_oid"
        "transcript_blob_oid"
        "correction02_commit_oid"
        "correction02_tree_oid"
        "final_head_oid"
        "origin_main_oid"
    ]

let ForbiddenIdentityFieldSet : Set<string> =
    Set.ofList ForbiddenIdentityFields

let firstForbiddenIdentityField (jsonKeys: string list) : string option =
    let mutable found : string option = None
    for k in jsonKeys do
        match found with
        | None when Set.contains k ForbiddenIdentityFieldSet -> found <- Some k
        | _ -> ()
    found

// -----------------------------------------------------------------------------
// Deterministic JSON helpers (used by the semantic hash and pre-canonical
// rendering in tests; the wire serializer lives in Serialization.fs).
// -----------------------------------------------------------------------------

let internal escapeJsonString (s: string) : string =
    let sb = StringBuilder(s.Length + 2)
    sb.Append '"' |> ignore
    for c in s do
        if c = '\\' then sb.Append("\\\\") |> ignore
        elif c = '"' then sb.Append("\\\"") |> ignore
        elif c = '\n' then sb.Append("\\n") |> ignore
        elif c = '\r' then sb.Append("\\r") |> ignore
        elif c = '\t' then sb.Append("\\t") |> ignore
        elif int c < 0x20 then
            sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", int c) |> ignore
        else sb.Append c |> ignore
    sb.Append '"' |> ignore
    sb.ToString()

let internal intStr (v: int) : string =
    v.ToString(CultureInfo.InvariantCulture)

let internal int64Str (v: int64) : string =
    v.ToString(CultureInfo.InvariantCulture)

let internal optStr (v: string option) : string =
    match v with
    | None -> "null"
    | Some s -> escapeJsonString s

let internal optIntStr (v: int option) : string =
    match v with
    | None -> "null"
    | Some n -> n.ToString(CultureInfo.InvariantCulture)

let internal optInt64Str (v: int64 option) : string =
    match v with
    | None -> "null"
    | Some n -> n.ToString(CultureInfo.InvariantCulture)

let internal strListJson (vs: string list) : string =
    "[" + (vs |> List.map escapeJsonString |> String.concat ",") + "]"

let internal renderStatusToken (s: EvidenceStatus) : string =
    escapeJsonString (statusToken s)

let internal renderCanonicalisationForm (e: CanonicalEvidence) : string =
    let sb = StringBuilder()
    sb.Append "{" |> ignore
    sb.Append "\"schema_version\":" |> ignore
    sb.Append(intStr e.SchemaVersion) |> ignore
    sb.Append ",\"provider_name\":" |> ignore
    sb.Append(escapeJsonString e.ProviderName) |> ignore
    sb.Append ",\"provider_version\":" |> ignore
    sb.Append(escapeJsonString e.ProviderVersion) |> ignore
    sb.Append ",\"tested_commit_oid\":" |> ignore
    sb.Append(escapeJsonString e.TestedCommitOid) |> ignore
    sb.Append ",\"tested_tree_oid\":" |> ignore
    sb.Append(escapeJsonString e.TestedTreeOid) |> ignore
    sb.Append ",\"object_format\":" |> ignore
    sb.Append(escapeJsonString e.ObjectFormat) |> ignore
    sb.Append ",\"checks\":[" |> ignore
    let sortedChecks =
        e.Checks |> List.sortBy (fun c -> c.Id, c.CommandArgv) |> List.toSeq
    let mutable first = true
    for c in sortedChecks do
        if first then first <- false
        else sb.Append "," |> ignore
        sb.Append "{\"id\":" |> ignore
        sb.Append(escapeJsonString c.Id) |> ignore
        sb.Append ",\"command_argv\":" |> ignore
        sb.Append(strListJson c.CommandArgv) |> ignore
        sb.Append ",\"working_directory\":" |> ignore
        sb.Append(escapeJsonString c.WorkingDirectory) |> ignore
        sb.Append ",\"duration_ms\":" |> ignore
        sb.Append(int64Str c.DurationMilliseconds) |> ignore
        sb.Append ",\"exit_code\":" |> ignore
        sb.Append(optIntStr c.ExitCode) |> ignore
        sb.Append ",\"status\":" |> ignore
        sb.Append(renderStatusToken c.Status) |> ignore
        sb.Append ",\"stdout_sha256\":" |> ignore
        sb.Append(optStr c.StdoutSha256) |> ignore
        sb.Append ",\"stderr_sha256\":" |> ignore
        sb.Append(optStr c.StderrSha256) |> ignore
        sb.Append ",\"failure_kind\":" |> ignore
        sb.Append(optStr c.FailureKind) |> ignore
        sb.Append "}" |> ignore
    sb.Append "]" |> ignore
    sb.Append ",\"overall_status\":" |> ignore
    sb.Append(renderStatusToken e.OverallStatus) |> ignore
    sb.Append "}" |> ignore
    sb.ToString()

// -----------------------------------------------------------------------------
// Semantic hash
//
// The semantic hash is a SHA-256 of the canonicalisation form above.
// The canonicalisation form deliberately excludes the
// ``semantic_sha256`` field itself so the hash is well-defined and
// self-consistent. The form has no volatile timestamp fields – the
// canonical evidence schema is intentionally time-free – so the
// hash is fully deterministic across hosts.
// -----------------------------------------------------------------------------

let computeSemanticHash (e: CanonicalEvidence) : string =
    let canon = renderCanonicalisationForm e
    sha256OfUtf8 canon

let withSemanticHash (e: CanonicalEvidence) : CanonicalEvidence =
    { e with SemanticSha256 = computeSemanticHash e }
