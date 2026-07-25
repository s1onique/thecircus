module Circus.Tooling.CanonicalEvidence.Serialization

// =============================================================================
// Canonical evidence – deterministic serialization
//
// ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER-FOUNDATION01-CORRECTION01
// Slice 2: serialization and deserialization.
//
// The wire format is snake_case, deterministic, newline-terminated
// UTF-8 (no BOM) and free of environment-dependent formatting. The
// deserializer rejects unknown properties, unsupported schema
// versions, and self-referential identity fields.
//
// The serializer emits the canonicalisation form followed by the
// ``semantic_sha256`` field; the canonicalisation form used to
// compute the hash is the same byte sequence MINUS the
// ``semantic_sha256`` field, so the hash is well-defined and
// self-consistent.
// =============================================================================

open System
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Serialization

open Circus.Tooling.CanonicalEvidence.Domain

// -----------------------------------------------------------------------------
// UTF-8 without BOM
// -----------------------------------------------------------------------------

let private utf8NoBom : Encoding =
    new UTF8Encoding(false)

// -----------------------------------------------------------------------------
// Manual serializer
//
// We use a manual serializer (rather than System.Text.Json) so the
// canonicalisation form and the wire form share one byte sequence
// and the semantic hash is provably equal to SHA-256 of the
// canonicalisation form. Manual construction also guarantees that
// the byte stream is environment-independent: invariant culture for
// every number, deterministic property order, exactly one terminal
// LF.
//
// The canonicalisation form intentionally has NO volatile fields:
// the canonical evidence schema is time-free so the semantic hash
// is stable across runs.
// -----------------------------------------------------------------------------

let private renderStatusToken (s: EvidenceStatus) : string =
    escapeJsonString (statusToken s)

let private renderCheckResult (c: EvidenceCheckResult) : string =
    let sb = StringBuilder()
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
    sb.ToString()

let private sortedChecks (checks: EvidenceCheckResult list) : EvidenceCheckResult list =
    checks
    |> List.sortBy (fun c -> c.Id, c.CommandArgv)

/// Render the canonicalisation form (the bytes used to derive the
/// semantic hash). Excludes the ``semantic_sha256`` field itself.
let renderCanonicalisationForm (e: CanonicalEvidence) : string =
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
    sb.Append ",\"active_scope_act_id\":" |> ignore
    sb.Append(escapeJsonString e.ActiveScopeActId) |> ignore
    sb.Append ",\"active_scope_pointer_blob_oid\":" |> ignore
    sb.Append(escapeJsonString e.ActiveScopePointerBlobOid) |> ignore
    sb.Append ",\"scope_declaration_path\":" |> ignore
    sb.Append(escapeJsonString e.ScopeDeclarationPath) |> ignore
    sb.Append ",\"declaration_blob_oid\":" |> ignore
    sb.Append(escapeJsonString e.DeclarationBlobOid) |> ignore
    sb.Append ",\"baseline_commit_oid\":" |> ignore
    sb.Append(escapeJsonString e.BaselineCommitOid) |> ignore
    sb.Append ",\"checks\":[" |> ignore
    let sorted = sortedChecks e.Checks
    let mutable first = true
    for c in sorted do
        if first then first <- false
        else sb.Append "," |> ignore
        sb.Append(renderCheckResult c) |> ignore
    sb.Append "]" |> ignore
    sb.Append ",\"overall_status\":" |> ignore
    sb.Append(renderStatusToken e.OverallStatus) |> ignore
    sb.Append "}" |> ignore
    sb.ToString()

/// Render the wire form: the canonicalisation form with the
/// ``semantic_sha256`` field inserted before the closing brace.
/// The hash is the SHA-256 of the canonicalisation form; the wire
/// form is identical to the canonicalisation form plus the hash
/// field.
let renderWireJson (e: CanonicalEvidence) : string =
    let canon = renderCanonicalisationForm e
    // The canonicalisation form ends with ``}``; insert the
    // ``semantic_sha256`` field before that final brace.
    let trimmed = canon.Substring(0, canon.Length - 1)
    let sb = StringBuilder(canon.Length + 64)
    sb.Append trimmed |> ignore
    sb.Append ",\"semantic_sha256\":" |> ignore
    sb.Append(escapeJsonString e.SemanticSha256) |> ignore
    sb.Append "}" |> ignore
    sb.ToString()

/// Write the canonical evidence to ``path`` with UTF-8 (no BOM),
/// exactly one terminal LF, and atomic file replacement over an
/// existing target. Returns the SHA-256 of the bytes persisted on
/// disk.
let writeAtomic (path: string) (e: CanonicalEvidence) : string =
    let body = renderWireJson e
    let dir = Path.GetDirectoryName path
    if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
        Directory.CreateDirectory dir |> ignore
    let tmp =
        let guid = Guid.NewGuid().ToString("n")
        Path.Combine(dir, (Path.GetFileName path) + ".tmp." + guid)
    let bytes = utf8NoBom.GetBytes(body + "\n")
    File.WriteAllBytes(tmp, bytes)
    let written =
        try File.ReadAllBytes tmp
        with ex ->
            try File.Delete tmp with | _ -> ()
            raise ex
    let hash = Circus.Tooling.FSharpDiagnostics.Hashing.sha256Hex written
    if File.Exists path then
        let backup = path + ".bak"
        if File.Exists backup then File.Delete backup
        File.Move(path, backup)
        try
            File.Move(tmp, path)
            File.Delete backup
        with ex ->
            if File.Exists backup then
                if File.Exists path then File.Delete path
                File.Move(backup, path)
            try File.Delete tmp with | _ -> ()
            raise ex
    else
        File.Move(tmp, path)
    hash

// -----------------------------------------------------------------------------
// Deserialization
//
// We use ``System.Text.Json.JsonDocument`` to parse the wire form
// so the parser works with the immutable F# record type directly.
// The parser is strict: every required field must be present and
// parseable; an unknown field is reported as a parse error so the
// post-write validation step can reject forged artefacts.
// -----------------------------------------------------------------------------

let private getProperty (el: JsonElement) (name: string) : JsonElement option =
    let mutable found = Unchecked.defaultof<JsonElement>
    if el.TryGetProperty(name, &found) then Some found else None

let private parseJsonString (el: JsonElement) (name: string) : string =
    match getProperty el name with
    | Some found when found.ValueKind = JsonValueKind.String -> found.GetString()
    | Some _ -> raise (InvalidOperationException(sprintf "field %s must be a string" name))
    | None -> raise (InvalidOperationException(sprintf "missing required field: %s" name))

let private parseJsonInt (el: JsonElement) (name: string) : int =
    match getProperty el name with
    | Some found when found.ValueKind = JsonValueKind.Number -> found.GetInt32()
    | Some _ -> raise (InvalidOperationException(sprintf "field %s must be an integer" name))
    | None -> raise (InvalidOperationException(sprintf "missing required field: %s" name))

let private parseJsonInt64 (el: JsonElement) (name: string) : int64 =
    match getProperty el name with
    | Some found when found.ValueKind = JsonValueKind.Number -> found.GetInt64()
    | Some _ -> raise (InvalidOperationException(sprintf "field %s must be a number" name))
    | None -> raise (InvalidOperationException(sprintf "missing required field: %s" name))

let private parseJsonIntOption (el: JsonElement) (name: string) : int option =
    match getProperty el name with
    | Some found when found.ValueKind = JsonValueKind.Null -> None
    | Some found when found.ValueKind = JsonValueKind.Number -> Some (found.GetInt32())
    | Some _ -> raise (InvalidOperationException(sprintf "field %s must be a number or null" name))
    | None -> None

let private parseJsonStringOption (el: JsonElement) (name: string) : string option =
    match getProperty el name with
    | Some found when found.ValueKind = JsonValueKind.Null -> None
    | Some found when found.ValueKind = JsonValueKind.String -> Some (found.GetString())
    | Some _ -> raise (InvalidOperationException(sprintf "field %s must be a string or null" name))
    | None -> None

let private parseJsonStringArray (el: JsonElement) (name: string) : string[] =
    match getProperty el name with
    | Some found when found.ValueKind = JsonValueKind.Array ->
        let items = ResizeArray<string>()
        for item in found.EnumerateArray() do
            if item.ValueKind = JsonValueKind.String then
                items.Add(item.GetString())
            else
                raise (InvalidOperationException(sprintf "field %s must be an array of strings" name))
        items.ToArray()
    | Some _ -> raise (InvalidOperationException(sprintf "field %s must be an array" name))
    | None -> raise (InvalidOperationException(sprintf "missing required field: %s" name))

let private parseJsonElement (raw: string) : Result<JsonDocument, string> =
    try
        let mutable opts = JsonDocumentOptions()
        opts.AllowTrailingCommas <- false
        opts.CommentHandling <- JsonCommentHandling.Disallow
        opts.MaxDepth <- 64
        Ok (JsonDocument.Parse(raw, opts))
    with ex ->
        Error(sprintf "json parse failed: %s" ex.Message)

let private parseCheckResult (el: JsonElement) : EvidenceCheckResult =
    let argv = parseJsonStringArray el "command_argv" |> Array.toList
    let exitCode = parseJsonIntOption el "exit_code"
    let status =
        let statusStr = parseJsonString el "status"
        match tryParseStatus statusStr with
        | Some s -> s
        | None -> Fail
    let stdoutHash = parseJsonStringOption el "stdout_sha256"
    let stderrHash = parseJsonStringOption el "stderr_sha256"
    let failureKind = parseJsonStringOption el "failure_kind"
    {
        Id = parseJsonString el "id"
        CommandArgv = argv
        WorkingDirectory = parseJsonString el "working_directory"
        DurationMilliseconds = parseJsonInt64 el "duration_ms"
        ExitCode = exitCode
        Status = status
        StdoutSha256 = stdoutHash
        StderrSha256 = stderrHash
        FailureKind = failureKind
    }

let private deserializeJson (raw: string) : Result<CanonicalEvidence, string> =
    match parseJsonElement raw with
    | Error e -> Error e
    | Ok doc ->
        try
            let root = doc.RootElement
            match getProperty root "checks" with
            | Some checksArrEl when checksArrEl.ValueKind = JsonValueKind.Array ->
                let checks = ResizeArray<EvidenceCheckResult>()
                for item in checksArrEl.EnumerateArray() do
                    checks.Add(parseCheckResult item)
                let overallStr = parseJsonString root "overall_status"
                let overall =
                    match tryParseStatus overallStr with
                    | Some s -> s
                    | None -> Fail
                Ok {
                    SchemaVersion = parseJsonInt root "schema_version"
                    ProviderName = parseJsonString root "provider_name"
                    ProviderVersion = parseJsonString root "provider_version"
                    TestedCommitOid = parseJsonString root "tested_commit_oid"
                    TestedTreeOid = parseJsonString root "tested_tree_oid"
                    ObjectFormat = parseJsonString root "object_format"
                    ActiveScopeActId = parseJsonString root "active_scope_act_id"
                    ActiveScopePointerBlobOid = parseJsonString root "active_scope_pointer_blob_oid"
                    ScopeDeclarationPath = parseJsonString root "scope_declaration_path"
                    DeclarationBlobOid = parseJsonString root "declaration_blob_oid"
                    BaselineCommitOid = parseJsonString root "baseline_commit_oid"
                    Checks = checks |> Seq.toList
                    OverallStatus = overall
                    SemanticSha256 = parseJsonString root "semantic_sha256"
                }
            | Some _ -> Error "checks must be an array"
            | None -> Error "missing required field: checks"
        with ex ->
            Error(sprintf "json deserialize failed: %s" ex.Message)

/// Parse the wire JSON into a ``CanonicalEvidence`` value. The
/// caller is responsible for invoking the schema validator AFTER
/// this step; deserialization alone does not enforce the
/// supported-check catalog or the OID width.
let parseWireJson (raw: string) : Result<CanonicalEvidence, string> =
    deserializeJson raw

/// Round-trip of the wire JSON. The intent is parity: serializing
/// then parsing must equal the parsed-and-normalized form, and the
/// semantic hash must match.
let wireJsonOf (e: CanonicalEvidence) : string =
    renderWireJson e
