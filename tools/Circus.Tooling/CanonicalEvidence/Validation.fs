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
    | InvalidScopeOid of field: string * oid: string * objectFormat: string
    | EmptyScopeField of field: string
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
    | InvalidScopeOid (field, oid, fmt) -> sprintf "invalid %s for %s: %s" field fmt oid
    | EmptyScopeField field -> sprintf "empty canonical scope field: %s" field
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

let private validateScopeBindingFields (e: CanonicalEvidence) (issues: ResizeArray<ValidationIssue>) =
    for field, value in
        [ "active_scope_act_id", e.ActiveScopeActId
          "scope_declaration_path", e.ScopeDeclarationPath ] do
        if String.IsNullOrWhiteSpace value then
            issues.Add(EmptyScopeField field)

    for field, oid in
        [ "active_scope_pointer_blob_oid", e.ActiveScopePointerBlobOid
          "declaration_blob_oid", e.DeclarationBlobOid
          "baseline_commit_oid", e.BaselineCommitOid ] do
        if not (isValidOid e.ObjectFormat oid) then
            issues.Add(InvalidScopeOid(field, oid, e.ObjectFormat))

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
    validateScopeBindingFields e issues
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

// -----------------------------------------------------------------------------
// Compatibility projection comparison (for staged validation)
// -----------------------------------------------------------------------------

[<RequireQualifiedAccess>]
type CompatibilityCheckDifference =
    | Id of expected: string * actual: string
    | CommandArgv of expected: string list * actual: string list
    | WorkingDirectory of expected: string * actual: string
    | DurationMilliseconds of expected: int64 * actual: int64
    | ExitCode of expected: int option * actual: int option
    | Status of expected: EvidenceStatus * actual: EvidenceStatus
    | StdoutSha256 of expected: string option * actual: string option
    | StderrSha256 of expected: string option * actual: string option
    | FailureKind of expected: string option * actual: string option

[<RequireQualifiedAccess>]
type CompatibilityDifference =
    | SchemaVersion of expected: int * actual: int
    | ProviderName of expected: string * actual: string
    | ProviderVersion of expected: string * actual: string
    | TestedCommitOid of expected: string * actual: string
    | TestedTreeOid of expected: string * actual: string
    | ObjectFormat of expected: string * actual: string
    | ActiveScopeActId of expected: string * actual: string
    | ActiveScopePointerBlobOid of expected: string * actual: string
    | ScopeDeclarationPath of expected: string * actual: string
    | DeclarationBlobOid of expected: string * actual: string
    | BaselineCommitOid of expected: string * actual: string
    | OverallStatus of expected: EvidenceStatus * actual: EvidenceStatus
    | SemanticSha256 of expected: string * actual: string
    | CheckCount of expected: int * actual: int
    | MissingCheck of checkId: string
    | UnknownCheck of checkId: string
    | CheckDifference of checkId: string * difference: CompatibilityCheckDifference
    | DuplicateExpectedCheckId of checkId: string * count: int
    | DuplicateActualCheckId of checkId: string * count: int

/// Compare two evidence check results field by field
let compareCompatibilityCheck (expected: EvidenceCheckResult) (actual: EvidenceCheckResult) : CompatibilityCheckDifference list =
    let diffs = ResizeArray()
    if expected.Id <> actual.Id then diffs.Add(CompatibilityCheckDifference.Id(expected.Id, actual.Id))
    if expected.CommandArgv <> actual.CommandArgv then diffs.Add(CompatibilityCheckDifference.CommandArgv(expected.CommandArgv, actual.CommandArgv))
    if expected.WorkingDirectory <> actual.WorkingDirectory then diffs.Add(CompatibilityCheckDifference.WorkingDirectory(expected.WorkingDirectory, actual.WorkingDirectory))
    if expected.DurationMilliseconds <> actual.DurationMilliseconds then diffs.Add(CompatibilityCheckDifference.DurationMilliseconds(expected.DurationMilliseconds, actual.DurationMilliseconds))
    if expected.ExitCode <> actual.ExitCode then diffs.Add(CompatibilityCheckDifference.ExitCode(expected.ExitCode, actual.ExitCode))
    if expected.Status <> actual.Status then diffs.Add(CompatibilityCheckDifference.Status(expected.Status, actual.Status))
    if expected.StdoutSha256 <> actual.StdoutSha256 then diffs.Add(CompatibilityCheckDifference.StdoutSha256(expected.StdoutSha256, actual.StdoutSha256))
    if expected.StderrSha256 <> actual.StderrSha256 then diffs.Add(CompatibilityCheckDifference.StderrSha256(expected.StderrSha256, actual.StderrSha256))
    if expected.FailureKind <> actual.FailureKind then diffs.Add(CompatibilityCheckDifference.FailureKind(expected.FailureKind, actual.FailureKind))
    List.ofSeq diffs

/// Compare two complete compatibility documents field by field
/// Returns a list of differences for validation reporting
let compareCompatibilityProjection (expected: CanonicalEvidence) (actual: CanonicalEvidence) : CompatibilityDifference list =
    let diffs = ResizeArray()
    
    // Top-level field comparisons
    if expected.SchemaVersion <> actual.SchemaVersion then
        diffs.Add(CompatibilityDifference.SchemaVersion(expected.SchemaVersion, actual.SchemaVersion))
    if expected.ProviderName <> actual.ProviderName then
        diffs.Add(CompatibilityDifference.ProviderName(expected.ProviderName, actual.ProviderName))
    if expected.ProviderVersion <> actual.ProviderVersion then
        diffs.Add(CompatibilityDifference.ProviderVersion(expected.ProviderVersion, actual.ProviderVersion))
    if expected.TestedCommitOid <> actual.TestedCommitOid then
        diffs.Add(CompatibilityDifference.TestedCommitOid(expected.TestedCommitOid, actual.TestedCommitOid))
    if expected.TestedTreeOid <> actual.TestedTreeOid then
        diffs.Add(CompatibilityDifference.TestedTreeOid(expected.TestedTreeOid, actual.TestedTreeOid))
    if expected.ObjectFormat <> actual.ObjectFormat then
        diffs.Add(CompatibilityDifference.ObjectFormat(expected.ObjectFormat, actual.ObjectFormat))
    if expected.ActiveScopeActId <> actual.ActiveScopeActId then
        diffs.Add(CompatibilityDifference.ActiveScopeActId(expected.ActiveScopeActId, actual.ActiveScopeActId))
    if expected.ActiveScopePointerBlobOid <> actual.ActiveScopePointerBlobOid then
        diffs.Add(CompatibilityDifference.ActiveScopePointerBlobOid(expected.ActiveScopePointerBlobOid, actual.ActiveScopePointerBlobOid))
    if expected.ScopeDeclarationPath <> actual.ScopeDeclarationPath then
        diffs.Add(CompatibilityDifference.ScopeDeclarationPath(expected.ScopeDeclarationPath, actual.ScopeDeclarationPath))
    if expected.DeclarationBlobOid <> actual.DeclarationBlobOid then
        diffs.Add(CompatibilityDifference.DeclarationBlobOid(expected.DeclarationBlobOid, actual.DeclarationBlobOid))
    if expected.BaselineCommitOid <> actual.BaselineCommitOid then
        diffs.Add(CompatibilityDifference.BaselineCommitOid(expected.BaselineCommitOid, actual.BaselineCommitOid))
    if expected.OverallStatus <> actual.OverallStatus then
        diffs.Add(CompatibilityDifference.OverallStatus(expected.OverallStatus, actual.OverallStatus))
    if expected.SemanticSha256 <> actual.SemanticSha256 then
        diffs.Add(CompatibilityDifference.SemanticSha256(expected.SemanticSha256, actual.SemanticSha256))
    
    // Check count comparison (always report)
    if expected.Checks.Length <> actual.Checks.Length then
        diffs.Add(CompatibilityDifference.CheckCount(expected.Checks.Length, actual.Checks.Length))
    
    // Detect duplicate IDs in expected (before Set/Map construction)
    let expectedIdGroups = expected.Checks |> List.groupBy (fun c -> c.Id)
    for checkId, group in expectedIdGroups do
        if group.Length > 1 then
            diffs.Add(CompatibilityDifference.DuplicateExpectedCheckId(checkId, group.Length))
    
    // Detect duplicate IDs in actual (before Set/Map construction)
    let actualIdGroups = actual.Checks |> List.groupBy (fun c -> c.Id)
    for checkId, group in actualIdGroups do
        if group.Length > 1 then
            diffs.Add(CompatibilityDifference.DuplicateActualCheckId(checkId, group.Length))
    
    // Build ID sets for bijection check (deduplicated for set operations)
    let expectedIds = expected.Checks |> List.map (fun c -> c.Id) |> Set.ofList
    let actualIds = actual.Checks |> List.map (fun c -> c.Id) |> Set.ofList
    
    // Find missing checks (in expected but not in actual) - always run
    let missingChecks = expectedIds - actualIds
    for missingId in missingChecks do
        diffs.Add(CompatibilityDifference.MissingCheck(missingId))
    
    // Find unknown checks (in actual but not in expected) - always run
    let unknownChecks = actualIds - expectedIds
    for unknownId in unknownChecks do
        diffs.Add(CompatibilityDifference.UnknownCheck(unknownId))
    
    // Compare matched checks by ID (bijection)
    let expectedById = expected.Checks |> List.map (fun c -> c.Id, c) |> Map.ofList
    let actualById = actual.Checks |> List.map (fun c -> c.Id, c) |> Map.ofList
    
    for expectedId in expectedIds do
        match Map.tryFind expectedId actualById with
        | None -> () // Already reported as missing/unknown
        | Some actualCheck ->
            match Map.tryFind expectedId expectedById with
            | None -> ()
            | Some expectedCheck ->
                let checkDiffs = compareCompatibilityCheck expectedCheck actualCheck
                for diff in checkDiffs do
                    diffs.Add(CompatibilityDifference.CheckDifference(expectedId, diff))
    
    List.ofSeq diffs

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
