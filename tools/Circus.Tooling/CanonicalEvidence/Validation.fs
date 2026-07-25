module Circus.Tooling.CanonicalEvidence.Validation

// =============================================================================
// Canonical evidence – post-deserialization validation
//
// ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER-FOUNDATION01-CORRECTION01
// Slice 3: validation. The deserializer in Serialization.fs produces
// a strict ``CanonicalEvidence`` value. This module then validates
// the value against the canonical contract:
//
//   * schema_version must equal the supported constant;
//   * provider_name must equal the canonical provider name;
//   * provider_version must match the expected version;
//   * object_format must be ``sha1`` or ``sha256``;
//   * the tested commit and tree OIDs must be full-width for the
//     declared object format – abbreviated OIDs are rejected;
//   * every check id must be in the supported catalog;
//   * every check's status token must be a canonical token;
//   * the semantic sha256 must match the recomputed hash;
//   * the overall_status must agree with the per-check roll-up.
//
// Validation is split into a per-rule ``Issue`` list and a
// composite ``ValidationResult`` that the CLI and the writer both
// consume. ``Validation.validate`` is the single entry point.
// =============================================================================

open System
open System.Text.Json

open Circus.Tooling.CanonicalEvidence.Domain

// -----------------------------------------------------------------------------
// Validation issues
// -----------------------------------------------------------------------------

type ValidationIssue =
    | UnsupportedSchemaVersion of actual: int
    | UnsupportedProviderName of actual: string
    | UnsupportedProviderVersion of actual: string
    | UnsupportedObjectFormat of actual: string
    | InvalidCommitOid of oid: string * objectFormat: string
    | InvalidTreeOid of oid: string * objectFormat: string
    | UnsupportedCheckId of id: string
    | InvalidStatusToken of context: string * token: string
    | SemanticHashMismatch of expected: string * actual: string
    | OverallStatusMismatch of expected: EvidenceStatus * actual: EvidenceStatus
    | EmptyCheckId
    | EmptyCommitOid
    | EmptyTreeOid
    | NegativeDuration of id: string * duration: int64
    | DuplicateCheckId of id: string

let issueToString (i: ValidationIssue) : string =
    match i with
    | UnsupportedSchemaVersion actual -> sprintf "unsupported schema_version: %d" actual
    | UnsupportedProviderName actual -> sprintf "unsupported provider_name: %s" actual
    | UnsupportedProviderVersion actual -> sprintf "unsupported provider_version: %s" actual
    | UnsupportedObjectFormat actual -> sprintf "unsupported object_format: %s" actual
    | InvalidCommitOid (oid, fmt) -> sprintf "invalid commit_oid for %s: %s" fmt oid
    | InvalidTreeOid (oid, fmt) -> sprintf "invalid tree_oid for %s: %s" fmt oid
    | UnsupportedCheckId id -> sprintf "unsupported check id: %s" id
    | InvalidStatusToken (ctx, tok) -> sprintf "%s status token invalid: %s" ctx tok
    | SemanticHashMismatch (expected, actual) ->
        sprintf "semantic_sha256 mismatch: expected=%s actual=%s" expected actual
    | OverallStatusMismatch (expected, actual) ->
        sprintf "overall_status mismatch: expected=%s actual=%s"
            (statusToken expected) (statusToken actual)
    | EmptyCheckId -> "empty check id"
    | EmptyCommitOid -> "empty tested_commit_oid"
    | EmptyTreeOid -> "empty tested_tree_oid"
    | NegativeDuration (id, d) -> sprintf "negative duration_ms for %s: %d" id d
    | DuplicateCheckId id -> sprintf "duplicate check id: %s" id

// -----------------------------------------------------------------------------
// Raw JSON-level field-introspection
//
// ``Validation`` is fed an already-parsed ``CanonicalEvidence`` and
// the raw JSON keys observed next to that parsed value. The raw
// keys are used to reject forbidden identity fields that are not
// part of the schema but might appear in a tampered document.
// -----------------------------------------------------------------------------

type ValidationResult = {
    Evidence: CanonicalEvidence
    Issues: ValidationIssue list
}

let private validateSchemaVersion (e: CanonicalEvidence) (issues: ResizeArray<ValidationIssue>) =
    if e.SchemaVersion <> SchemaVersionValue then
        issues.Add(UnsupportedSchemaVersion e.SchemaVersion)

let private validateProviderName (e: CanonicalEvidence) (issues: ResizeArray<ValidationIssue>) =
    if e.ProviderName <> ProviderNameValue then
        issues.Add(UnsupportedProviderName e.ProviderName)

let private validateProviderVersion (e: CanonicalEvidence) (issues: ResizeArray<ValidationIssue>) =
    if e.ProviderVersion <> ProviderVersionValue then
        issues.Add(UnsupportedProviderVersion e.ProviderVersion)

let private validateObjectFormat (e: CanonicalEvidence) (issues: ResizeArray<ValidationIssue>) =
    if not (Set.contains e.ObjectFormat supportedObjectFormats) then
        issues.Add(UnsupportedObjectFormat e.ObjectFormat)

let private validateOids (e: CanonicalEvidence) (issues: ResizeArray<ValidationIssue>) =
    if String.IsNullOrWhiteSpace e.TestedCommitOid then
        issues.Add EmptyCommitOid
    elif not (isValidOid e.ObjectFormat e.TestedCommitOid) then
        issues.Add(InvalidCommitOid (e.TestedCommitOid, e.ObjectFormat))
    if String.IsNullOrWhiteSpace e.TestedTreeOid then
        issues.Add EmptyTreeOid
    elif not (isValidOid e.ObjectFormat e.TestedTreeOid) then
        issues.Add(InvalidTreeOid (e.TestedTreeOid, e.ObjectFormat))

let private validateChecks (e: CanonicalEvidence) (issues: ResizeArray<ValidationIssue>) =
    let seen = System.Collections.Generic.HashSet<string>()
    for c in e.Checks do
        if String.IsNullOrWhiteSpace c.Id then
            issues.Add EmptyCheckId
        elif not (isSupportedCheckId c.Id) then
            issues.Add(UnsupportedCheckId c.Id)
        if not (seen.Add c.Id) then
            issues.Add(DuplicateCheckId c.Id)
        if c.DurationMilliseconds < 0L then
            issues.Add(NegativeDuration (c.Id, c.DurationMilliseconds))

let private validateSemanticHash (e: CanonicalEvidence) (issues: ResizeArray<ValidationIssue>) =
    let recomputed = computeSemanticHash e
    if recomputed <> e.SemanticSha256 then
        issues.Add(SemanticHashMismatch (recomputed, e.SemanticSha256))

let private validateOverallStatus (e: CanonicalEvidence) (issues: ResizeArray<ValidationIssue>) =
    let expected = computeOverallStatus e.Checks
    if expected <> e.OverallStatus then
        issues.Add(OverallStatusMismatch (expected, e.OverallStatus))

let private validateForbiddenIdentityFields (rawKeys: string list) (issues: ResizeArray<ValidationIssue>) =
    match firstForbiddenIdentityField rawKeys with
    | Some k ->
        // Reuse the existing issue vocabulary so the CLI surface
        // is uniform. The 'context' is the field name itself.
        issues.Add(UnsupportedProviderName (sprintf "forbidden_identity_field:%s" k))
    | None -> ()

// -----------------------------------------------------------------------------
// Public entry point
// -----------------------------------------------------------------------------

let validate (rawJsonKeys: string list) (e: CanonicalEvidence) : ValidationResult =
    let issues = ResizeArray<ValidationIssue>()
    validateSchemaVersion e issues
    validateProviderName e issues
    validateProviderVersion e issues
    validateObjectFormat e issues
    validateOids e issues
    validateChecks e issues
    validateSemanticHash e issues
    validateOverallStatus e issues
    validateForbiddenIdentityFields rawJsonKeys issues
    {
        Evidence = e
        Issues = issues |> Seq.toList
    }

let isValid (r: ValidationResult) : bool =
    List.isEmpty r.Issues

/// Enumerate the document's top-level JSON property names. This
/// runs alongside the deserializer so the field allow-list and the
/// forbidden set can both be enforced.
let collectRawJsonKeys (raw: string) : string list =
    let mutable keys : string list = []
    try
        use doc = JsonDocument.Parse(raw)
        let root = doc.RootElement
        if root.ValueKind = JsonValueKind.Object then
            for p in root.EnumerateObject() do
                keys <- p.Name :: keys
    with _ -> ()
    keys
