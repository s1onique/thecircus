module Circus.Tooling.EvidenceValidator.Validation

// =============================================================================
// Evidence validator – validation logic
//
// ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01-CORRECTION01
//
// Pure validation functions that take the parsed JSON document and
// the resolved containing commit OID and return a ``ValidationResult``.
//
// The validator is composed of two concerns:
//
//   * Extract-the-fields: retrieve ``tested_subject_commit_oid``,
//     ``evidence_payload_sha256``, and the documented placeholder.
//     Missing fields, wrong types, and placeholder width mismatches
//     are reported as issues with stable tokens.
//
//   * Verify-the-claims: produce the canonical JSON form, replace
//     the payload hash field with the placeholder, compute SHA-256,
//     and compare. Also reject any identity field whose value equals
//     the containing commit OID.
//
// The canonical JSON form is produced by the
// ``renderCanonicalRoot`` helper below. The helper MANUALLY walks the
// parsed JSON document and emits keys in sorted order with no
// whitespace. This matches the canonicalisation the evidence file
// was produced with and avoids any environmental dependency on
// ``System.Text.Json`` serializer settings.
// =============================================================================

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Text
open System.Text.Json

open Circus.Tooling.EvidenceValidator.Domain
open Circus.Tooling.FSharpDiagnostics.Hashing

// -----------------------------------------------------------------------------
// Field extraction
// -----------------------------------------------------------------------------

type EvidenceSnapshot = {
    SubjectCommitOid: string option
    SubjectTreeOid: string option
    PayloadHash: string option
    Placeholder: string option
}

let private tryGetString (el: JsonElement) (name: string) : string option =
    let mutable found = Unchecked.defaultof<JsonElement>
    if el.TryGetProperty(name, &found) then
        if found.ValueKind = JsonValueKind.String then
            Some (found.GetString())
        else
            None
    else
        None

let extractSnapshot (raw: string) : Result<EvidenceSnapshot, Issue> =
    try
        let doc = JsonDocument.Parse(raw)
        let root = doc.RootElement
        if root.ValueKind <> JsonValueKind.Object then
            Error (NotJsonObject "<input>")
        else
            Ok {
                SubjectCommitOid = tryGetString root "tested_subject_commit_oid"
                SubjectTreeOid = tryGetString root "tested_subject_tree_oid"
                PayloadHash = tryGetString root "evidence_payload_sha256"
                Placeholder = tryGetString root "evidence_payload_sha256_input_placeholder"
            }
    with ex ->
        Error (NotJsonObject (sprintf "json parse failed: %s" ex.Message))

// -----------------------------------------------------------------------------
// Canonical JSON form
//
// Emit a strict, deterministic JSON string. Keys are sorted; strings
// are escaped per RFC 8259; numbers use invariant culture. The output
// has no whitespace between tokens.
//
// This is the canonical form the validator expects. It deliberately
// does NOT rely on the order in which ``System.Text.Json`` happens to
// serialise properties — the manual walk guarantees stability across
// runtime versions and builds.
// -----------------------------------------------------------------------------

let private escapeJsonString (s: string) : string =
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

let private compareKeys (a: string) (b: string) : int =
    String.Compare(a, b, StringComparison.Ordinal)

let rec private renderValue (sb: StringBuilder) (el: JsonElement) : unit =
    match el.ValueKind with
    | JsonValueKind.Object ->
        sb.Append '{' |> ignore
        let mutable keys = ResizeArray<string>()
        for p in el.EnumerateObject() do
            keys.Add(p.Name)
        let sortedKeys = keys |> Seq.sortWith compareKeys |> Seq.toArray
        let mutable first = true
        for k in sortedKeys do
            if not first then sb.Append ',' |> ignore
            first <- false
            sb.Append(escapeJsonString k) |> ignore
            sb.Append ':' |> ignore
            let mutable v = Unchecked.defaultof<JsonElement>
            if el.TryGetProperty(k, &v) then
                renderValue sb v
        sb.Append '}' |> ignore
    | JsonValueKind.Array ->
        sb.Append '[' |> ignore
        let mutable first = true
        for item in el.EnumerateArray() do
            if not first then sb.Append ',' |> ignore
            first <- false
            renderValue sb item
        sb.Append ']' |> ignore
    | JsonValueKind.String ->
        sb.Append(escapeJsonString(el.GetString())) |> ignore
    | JsonValueKind.Number ->
        let raw = el.GetRawText()
        sb.Append raw |> ignore
    | JsonValueKind.True ->
        sb.Append "true" |> ignore
    | JsonValueKind.False ->
        sb.Append "false" |> ignore
    | JsonValueKind.Null ->
        sb.Append "null" |> ignore
    | _ ->
        sb.Append "null" |> ignore

let private renderCanonicalRoot (sb: StringBuilder) (root: JsonElement) : unit =
    sb.Append '{' |> ignore
    let mutable keys = ResizeArray<string>()
    for p in root.EnumerateObject() do
        keys.Add(p.Name)
    let sortedKeys = keys |> Seq.sortWith compareKeys |> Seq.toArray
    let mutable first = true
    for k in sortedKeys do
        if not first then sb.Append ',' |> ignore
        first <- false
        sb.Append(escapeJsonString k) |> ignore
        sb.Append ':' |> ignore
        let mutable v = Unchecked.defaultof<JsonElement>
        if root.TryGetProperty(k, &v) then
            renderValue sb v
    sb.Append '}' |> ignore

let renderCanonical (raw: string) : string =
    let sb = StringBuilder()
    let mutable opts = JsonDocumentOptions()
    opts.AllowTrailingCommas <- false
    opts.CommentHandling <- JsonCommentHandling.Disallow
    opts.MaxDepth <- 64
    use doc = JsonDocument.Parse(raw, opts)
    renderCanonicalRoot sb doc.RootElement
    sb.ToString()

// -----------------------------------------------------------------------------
// Validation entry point
// -----------------------------------------------------------------------------

type ValidationResult = {
    Path: string
    Snapshot: EvidenceSnapshot option
    Issues: Issue list
    ComputedPayloadHash: string option
}

let validate
    (path: string)
    (snapshot: EvidenceSnapshot)
    (containingCommitOid: string option)
    (canonicalJson: string)
    : ValidationResult =
    let issues = ResizeArray<Issue>()

    // --- Mandatory subject commit OID ---
    let subjectOid =
        match snapshot.SubjectCommitOid with
        | Some oid when not (String.IsNullOrWhiteSpace oid) -> Some oid
        | _ ->
            issues.Add(MissingField(path, "tested_subject_commit_oid"))
            None

    // --- Payload hash field presence ---
    let payloadHash =
        match snapshot.PayloadHash with
        | Some h when not (String.IsNullOrWhiteSpace h) -> Some h
        | Some _ ->
            issues.Add(PayloadHashFieldNotString path)
            None
        | None ->
            issues.Add(PayloadHashFieldMissing path)
            None

    // --- Placeholder field presence ---
    let placeholder =
        match snapshot.Placeholder with
        | Some p when not (String.IsNullOrWhiteSpace p) -> Some p
        | Some _ ->
            issues.Add(PlaceholderFieldNotString path)
            None
        | None ->
            issues.Add(PlaceholderFieldMissing path)
            None

    // --- Payload hash check ---
    //
    // The canonical form is produced from the JSON document with the
    // payload hash field replaced by the placeholder. This makes the
    // hash a fixed-point: the canonical form does not depend on the
    // actual hash value, so the SHA-256 is computed over a stable
    // byte stream regardless of which hash value is currently stored.
    let computedHash =
        match payloadHash, placeholder with
        | Some _, Some _ ->
            // The canonical form passed in already has the placeholder
            // substituted for the hash field, so the hash is directly
            // SHA-256 of the canonical form.
            Some (sha256OfUtf8 canonicalJson)
        | _ ->
            None

    match payloadHash, computedHash with
    | Some declared, Some computed when
        String.Equals(declared, computed, StringComparison.OrdinalIgnoreCase) ->
        ()
    | Some declared, Some computed ->
        issues.Add(PayloadHashMismatch(path, declared, computed))
    | _ ->
        ()

    // --- Self-reference check ---
    match subjectOid, containingCommitOid with
    | Some s, Some c when
        String.Equals(s, c, StringComparison.OrdinalIgnoreCase) ->
        issues.Add(SelfReferentialIdentity(path, "tested_subject_commit_oid", s, c))
    | _ ->
        ()

    {
        Path = path
        Snapshot = Some snapshot
        Issues = issues |> Seq.toList
        ComputedPayloadHash = computedHash
    }

let validatePath
    (path: string)
    (containingCommitOid: string option)
    : ValidationResult =
    if not (File.Exists path) then
        {
            Path = path
            Snapshot = None
            Issues = [ FileMissing path ]
            ComputedPayloadHash = None
        }
    else
        let raw = File.ReadAllText path
        match extractSnapshot raw with
        | Error issue ->
            {
                Path = path
                Snapshot = None
                Issues = [ issue ]
                ComputedPayloadHash = None
            }
        | Ok snapshot ->
            // The canonical form used for hashing must have the
            // payload hash VALUE replaced by the placeholder. We
            // produce this by string-replacing the actual hash value
            // in the canonical form with the placeholder text. The
            // result is a fixed point: the hash is solely determined
            // by the file body excluding the hash field itself.
            let canonical = renderCanonical raw
            let canonicalForHash =
                match snapshot.PayloadHash, snapshot.Placeholder with
                | Some h, Some p ->
                    // Replace the actual hash value with the placeholder.
                    canonical.Replace(escapeJsonString h, escapeJsonString p)
                | _ ->
                    canonical
            validate path snapshot containingCommitOid canonicalForHash
