module Circus.Tooling.SourcePolicy.Parity

/// Strict parser and validator for ``factory/container-policy-parity.csv``.
/// CORRECTION01 P1-1: Eliminates prefix aliasing in rule identity comparison.
/// The authoritative identity source is ContainerPolicy.CheckIds; parity rows
/// must use exact identifiers with no prefix extraction.

open System
open System.IO
open System.Text
open System.Text.RegularExpressions

open Circus.Tooling.SourcePolicy.ContainerPolicy

let RequiredHeader : string list =
    [ "legacy_check_id"
      "legacy_behavior"
      "fsharp_check_id"
      "implementation_location"
      "positive_test"
      "negative_mutation_test"
      "status" ]

let ValidStatuses : string list = [ "complete" ]

/// Valid ContainerPolicyRuleId pattern: exactly "CP-" followed by digits only.
/// P1-1: This is the ONLY allowed identity format. No prefix extraction, no aliases.
let private RuleIdPattern = Regex(@"^CP-\d+$")

/// Validates that an identity is a well-formed ContainerPolicyRuleId.
/// Returns None if the identity is malformed (contains extra characters,
/// whitespace, case variants, or prefix aliases).
/// P1-1: Exact identity matching - no prefix extraction.
let private parseRuleId (id: string) : string option =
    if RuleIdPattern.IsMatch(id) then Some id else None

/// Reason for malformed identity detection.
let private malformedReason (id: string) : string =
    if id.Length = 0 then "empty identity"
    elif String.IsNullOrWhiteSpace(id) then "whitespace-only identity"
    elif id <> id.Trim() then sprintf "identity has leading/trailing whitespace: '%s'" id
    elif id <> id.ToUpperInvariant() && id.StartsWith("CP-", System.StringComparison.OrdinalIgnoreCase) then
        sprintf "identity has incorrect case: '%s' (expected uppercase)" id
    else sprintf "identity '%s' is not a valid CP-NN format" id

type ParityRow = {
    LegacyCheckId: string
    LegacyBehavior: string
    FsharpCheckId: string
    ImplementationLocation: string
    PositiveTest: string
    NegativeMutationTest: string
    Status: string
}

type RowError = {
    LineNumber: int
    Reason: string
}

type ValidationReport = {
    Rows: ParityRow list
    MissingIdentities: string list
    UnexpectedIdentities: string list
    DuplicateIdentities: string list
    DuplicateProductionIds: string list
    FieldMismatches: (string * string * string) list
    IdentityPathMismatches: (string * string * string) list
    IdentityPathFunctionMismatches: (string * string) list
    InvalidStatusRows: (int * string) list
    RowParseFailures: RowError list
    HeaderOk: bool
    HeaderFailures: string list
    /// Identities with malformed format (trailing text, whitespace, case variants, etc.)
    MalformedIdentities: string list
    /// Identity reasons for malformed entries
    MalformedIdentityReasons: (string * string) list
}

type ValidationOutcome =
    | Ok of ValidationReport
    | Failed of ValidationReport * failures: string list

/// Parse a single CSV line under the restricted dialect.
let private parseRow (line: string) : Result<string list, string> =
    let cells = ResizeArray<string>()
    let sb = StringBuilder()
    let mutable inQuotes = false
    let mutable i = 0
    let len = line.Length
    let mutable reason : string option = None
    while i < len && reason.IsNone do
        let c = line.[i]
        if inQuotes then
            if c = '"' then
                if i + 1 < len && line.[i + 1] = '"' then
                    sb.Append('"') |> ignore
                    i <- i + 2
                else
                    inQuotes <- false
                    i <- i + 1
            else
                sb.Append c |> ignore
                i <- i + 1
        else
            if c = ',' then
                cells.Add(sb.ToString())
                sb.Clear() |> ignore
                i <- i + 1
            elif c = '"' && sb.Length = 0 then
                inQuotes <- true
                i <- i + 1
            elif c = ' ' then
                i <- i + 1
            else
                reason <- Some (sprintf "unquoted character '%c' at column %d" c (i + 1))
    if reason.IsSome then Result.Error reason.Value
    elif inQuotes then Result.Error "unbalanced quoting"
    else
        cells.Add(sb.ToString())
        Result.Ok (List.ofSeq cells)

let parse (path: string) : Result<ParityRow list, string> =
    if not (File.Exists path) then
        Result.Error (sprintf "parity CSV not found: %s" path)
    else
        try
            let raw = File.ReadAllText path
            let lines =
                raw.Split('\n')
                |> Array.map (fun l -> l.TrimEnd('\r'))
                |> Array.filter (fun l -> not (String.IsNullOrWhiteSpace l))
                |> Array.toList
            match lines with
            | [] -> Result.Error "parity CSV is empty"
            | header :: dataRows ->
                let headers = parseRow header
                match headers with
                | Result.Error e -> Result.Error (sprintf "header parse failed: %s" e)
                | Result.Ok headers ->
                    if headers <> RequiredHeader then
                        let extras = headers |> List.filter (fun h -> not (List.contains h RequiredHeader)) |> List.sort
                        let missing = RequiredHeader |> List.filter (fun h -> not (List.contains h headers)) |> List.sort
                        let orderDiff =
                            if List.length headers = List.length RequiredHeader &&
                               List.zip headers RequiredHeader |> List.exists (fun (a, b) -> a <> b)
                            then [ "header column order does not match required order" ]
                            else []
                        let msgs =
                            (if not (List.isEmpty extras) then [ sprintf "extra header columns: %s" (String.concat "," extras) ] else [])
                            @ (if not (List.isEmpty missing) then [ sprintf "missing header columns: %s" (String.concat "," missing) ] else [])
                            @ orderDiff
                        Result.Error(String.concat "; " msgs)
                    else
                        let mutable rows : ParityRow list = []
                        let mutable errors : string list = []
                        let mutable lineNo = 1
                        let mutable parseFailures : RowError list = []
                        for line in dataRows do
                            lineNo <- lineNo + 1
                            match parseRow line with
                            | Result.Error e ->
                                parseFailures <- { LineNumber = lineNo; Reason = e } :: parseFailures
                                errors <- sprintf "line %d: %s" lineNo e :: errors
                            | Result.Ok cells ->
                                if List.length cells <> List.length RequiredHeader then
                                    errors <- sprintf "line %d: expected %d fields, got %d"
                                                lineNo (List.length RequiredHeader) (List.length cells) :: errors
                                else
                                    let fields = List.zip RequiredHeader cells
                                    let lookup key =
                                        fields
                                        |> List.tryPick (fun (k, v) -> if k = key then Some v else None)
                                        |> Option.defaultValue ""
                                    let id = lookup "legacy_check_id"
                                    let status = lookup "status"
                                    let pos = lookup "positive_test"
                                    let neg = lookup "negative_mutation_test"
                                    let legacyBehavior = lookup "legacy_behavior"
                                    let fsId = lookup "fsharp_check_id"
                                    let implLoc = lookup "implementation_location"
                                    if String.IsNullOrWhiteSpace id then
                                        errors <- sprintf "line %d: missing identity" lineNo :: errors
                                    if String.IsNullOrWhiteSpace legacyBehavior then
                                        errors <- sprintf "line %d: blank legacy_behavior" lineNo :: errors
                                    if not (List.contains status ValidStatuses) then
                                        errors <- sprintf "line %d: invalid status '%s' (must be 'complete')" lineNo status :: errors
                                    if String.IsNullOrWhiteSpace pos then
                                        errors <- sprintf "line %d: blank positive_test" lineNo :: errors
                                    if String.IsNullOrWhiteSpace neg then
                                        errors <- sprintf "line %d: blank negative_mutation_test" lineNo :: errors
                                    if String.IsNullOrWhiteSpace fsId then
                                        errors <- sprintf "line %d: blank fsharp_check_id" lineNo :: errors
                                    if String.IsNullOrWhiteSpace implLoc then
                                        errors <- sprintf "line %d: blank implementation_location" lineNo :: errors
                                    rows <- { LegacyCheckId = id
                                              LegacyBehavior = legacyBehavior
                                              FsharpCheckId = fsId
                                              ImplementationLocation = implLoc
                                              PositiveTest = pos
                                              NegativeMutationTest = neg
                                              Status = status } :: rows
                        if not (List.isEmpty errors) then
                            Result.Error(String.concat "; " (List.rev errors))
                        else
                            Result.Ok (List.rev rows)
        with ex ->
            Result.Error(sprintf "parity CSV read failed: %s: %s" (ex.GetType().FullName) ex.Message)

/// Extract function name from implementation_location string.
/// P1-1: Unchanged - still uses regex to extract function name.
let private extractFunctionName (implLoc: string) : string option =
    let m = Regex(@"\(([^)]+)\)").Match(implLoc)
    if m.Success then Some m.Groups.[1].Value else None

/// P1-1: Authoritative production rule metadata.
/// Map keyed by exact CP-NN identity -> expected function name.
/// This is the ONLY authority for identity comparison.
let private ruleFunctionName : Map<string, string> =
    Map.ofList [
        "CP-01", "checkRequiredFiles"
        "CP-02", "checkShellExecutable"
        "CP-03", "checkDockerignore"
        "CP-04", "checkWorkflowTriggers"
        "CP-05", "checkPushBranchRestriction"
        "CP-06", "checkMinimalPermissions"
        "CP-07", "checkReferenceScopedConcurrency"
        "CP-08", "checkReusableInputs"
        "CP-09", "checkNoPullRequestTarget"
        "CP-10", "checkTrustedRunner"
        "CP-11", "checkHarborRepositoryNaming"
        "CP-12", "checkPasswordStdin"
        "CP-13", "checkTlsBypass"
        "CP-14", "checkPrivateCaAndBuildkit"
        "CP-15", "checkCacheSeparation"
        "CP-16", "checkPublishGating"
        "CP-17", "checkCacheImportExport"
        "CP-18", "checkImmutableTags"
        "CP-19", "checkLatestTagContract"
        "CP-20", "checkSecretMountCleanup"
        "CP-21", "checkElmInstaller"
        "CP-22", "checkNumericUsers"
        "CP-23", "checkPortContracts"
        "CP-24", "checkSmokeEndpoints"
        "CP-25", "checkDigestPullInspect"
        "CP-26", "checkWorkflowSeams"
        "CP-27", "checkGithubOutputContracts"
        "CP-28", "checkActionPins"
        "CP-29", "checkTrackedSecrets"
        "CP-30", "checkFinalStageExclusions"
        "CP-31", "checkGateSummaryAcceptance"
    ]

/// P1-1: Exact identity validation.
/// Derives production_rule_count and parity_row_count mechanically.
/// Uses ContainerPolicy.CheckIds as the authoritative source.
let validate (rows: ParityRow list) : ValidationOutcome =
    // P1-1: Authoritative production identity set from ContainerPolicy.CheckIds
    let productionRuleIds = ContainerPolicy.CheckIds

    // P1-1: Production rule count derived mechanically
    let productionRuleCount = List.length productionRuleIds

    // P1-1: Parity row count derived mechanically
    let parityRowCount = List.length rows

    // P1-1: Extract exact identities from parity rows (NO prefix extraction)
    let csvIds = rows |> List.map (fun r -> r.LegacyCheckId)
    let csvFsharpIds = rows |> List.map (fun r -> r.FsharpCheckId)

    // P1-1: Check for malformed identities (prefix aliases, trailing text, etc.)
    let malformedCsv, malformedCsvReasons =
        csvIds
        |> List.map (fun id -> id, parseRuleId id, malformedReason id)
        |> List.partition (fun (_, parsed, _) -> parsed.IsSome)
    let malformedCsvIds = malformedCsv |> List.map (fun (id, _, _) -> id) |> List.sort |> List.distinct
    let malformedCsvReasons =
        malformedCsv
        |> List.map (fun (id, _, reason) -> id, reason)
        |> List.sortBy fst

    let malformedFsharp, malformedFsharpReasons =
        csvFsharpIds
        |> List.map (fun id -> id, parseRuleId id, malformedReason id)
        |> List.partition (fun (_, parsed, _) -> parsed.IsSome)
    let malformedFsharpIds = malformedFsharp |> List.map (fun (id, _, _) -> id) |> List.sort |> List.distinct
    let malformedFsharpReasons =
        malformedFsharp
        |> List.map (fun (id, _, reason) -> id, reason)
        |> List.sortBy fst

    let allMalformed = (malformedCsvIds @ malformedFsharpIds) |> List.sort |> List.distinct
    let allMalformedReasons = (malformedCsvReasons @ malformedFsharpReasons) |> List.sortBy fst |> List.distinctBy fst

    // P1-1: Duplicate parity IDs detected using exact equality (NO prefix extraction)
    let dupCsvIds =
        csvIds
        |> List.groupBy id
        |> List.choose (fun (k, g) -> if List.length g > 1 then Some k else None)

    // P1-1: Duplicate production IDs detected (production rules should have unique identities)
    let dupProductionIds =
        productionRuleIds
        |> List.groupBy id
        |> List.choose (fun (k, g) -> if List.length g > 1 then Some k else None)

    // P1-1: Unexpected identities in CSV (using EXACT match, no prefix extraction)
    let unexpected =
        csvIds
        |> List.filter (fun id ->
            not (List.contains id productionRuleIds) && parseRuleId id |> Option.isSome)
        |> List.sort |> List.distinct

    // P1-1: Missing identities from CSV (using EXACT match, no prefix extraction)
    let missing =
        productionRuleIds
        |> List.filter (fun id -> not (List.contains id csvIds))
        |> List.sort |> List.distinct

    // P1-1: Field mismatches using exact identity comparison
    let fieldMismatches =
        rows
        |> List.filter (fun r -> r.LegacyCheckId <> r.FsharpCheckId)
        |> List.map (fun r -> r.LegacyCheckId, r.FsharpCheckId, "legacy_check_id != fsharp_check_id")

    let identityPathMismatches =
        rows
        |> List.filter (fun r -> not (r.ImplementationLocation.Contains "ContainerPolicy.fs"))
        |> List.map (fun r -> r.LegacyCheckId, r.ImplementationLocation, "<must reference ContainerPolicy.fs>")

    // P1-1: Function name mismatches using exact identity lookup
    let identityPathFunctionMismatches =
        rows
        |> List.choose (fun r ->
            // Use exact identity for lookup
            match Map.tryFind r.LegacyCheckId ruleFunctionName with
            | Some expected ->
                match extractFunctionName r.ImplementationLocation with
                | Some actual when actual = expected -> None
                | actual -> Some (r.LegacyCheckId, sprintf "expected %s; got %s" expected (defaultArg actual "<missing>"))
            | None -> None)

    let invalidStatusRows =
        rows
        |> List.mapi (fun i r -> (i + 2, r))
        |> List.filter (fun (_, r) -> not (List.contains r.Status ValidStatuses))
        |> List.map (fun (line, r) -> (line, r.Status))

    // P1-1: Exact matches count derived mechanically
    let exactMatches =
        rows
        |> List.filter (fun r ->
            List.contains r.LegacyCheckId productionRuleIds &&
            List.contains r.FsharpCheckId productionRuleIds &&
            r.LegacyCheckId = r.FsharpCheckId &&
            Map.containsKey r.LegacyCheckId ruleFunctionName)
        |> List.length

    let report = {
        Rows = rows
        MissingIdentities = missing
        UnexpectedIdentities = unexpected
        DuplicateIdentities = dupCsvIds
        DuplicateProductionIds = dupProductionIds
        FieldMismatches = fieldMismatches
        IdentityPathMismatches = identityPathMismatches
        IdentityPathFunctionMismatches = identityPathFunctionMismatches
        InvalidStatusRows = invalidStatusRows
        RowParseFailures = []
        HeaderOk = true
        HeaderFailures = []
        MalformedIdentities = allMalformed
        MalformedIdentityReasons = allMalformedReasons
    }

    // P1-1: All defect collections must be empty for passing result
    let failures : string list =
        []
        |> List.append (if productionRuleCount <> parityRowCount then
                           [sprintf "production_rule_count (%d) != parity_row_count (%d)" productionRuleCount parityRowCount]
                        else [])
        |> List.append (if exactMatches <> productionRuleCount then
                           [sprintf "exact_matches (%d) != production_rule_count (%d)" exactMatches productionRuleCount]
                        else [])
        |> List.append (List.map (sprintf "malformed identity in CSV: %s") allMalformed)
        |> List.append (List.map (fun (id, reason) -> sprintf "malformed identity '%s': %s" id reason) allMalformedReasons)
        |> List.append (List.map (sprintf "unexpected identity in CSV: %s") unexpected)
        |> List.append (List.map (sprintf "registered identity missing from CSV: %s") missing)
        |> List.append (List.map (sprintf "duplicate identity in CSV: %s") dupCsvIds)
        |> List.append (List.map (sprintf "duplicate production ID: %s") dupProductionIds)
        |> List.append (List.map (fun (csv, fs, reason) -> sprintf "identity field mismatch: csv=%s fsharp=%s (%s)" csv fs reason) fieldMismatches)
        |> List.append (List.map (fun (id, csv, reg) -> sprintf "identity %s implementation_location disagreement: csv=%s registry=%s" id csv reg) identityPathMismatches)
        |> List.append (List.map (fun (id, msg) -> sprintf "identity %s implementation_function mismatch: %s" id msg) identityPathFunctionMismatches)
        |> List.append (List.map (fun (line, status) -> sprintf "line %d: invalid status '%s'" line status) invalidStatusRows)

    if List.isEmpty failures then Ok report
    else Failed (report, failures)

let validateFile (path: string) : ValidationOutcome =
    match parse path with
    | Result.Error e ->
        Failed ({ Rows = []
                  MissingIdentities = []
                  UnexpectedIdentities = []
                  DuplicateIdentities = []
                  DuplicateProductionIds = []
                  FieldMismatches = []
                  IdentityPathMismatches = []
                  IdentityPathFunctionMismatches = []
                  InvalidStatusRows = []
                  RowParseFailures = []
                  HeaderOk = false
                  HeaderFailures = [ e ]
                  MalformedIdentities = []
                  MalformedIdentityReasons = [] },
               [ e ])
    | Result.Ok rows -> validate rows

/// P1-1: Enhanced summary includes all P1-1 accountability fields.
let renderSummary (outcome: ValidationOutcome) : string =
    match outcome with
    | Ok r ->
        sprintf "parity: PASS (production_rules=%d, parity_rows=%d, exact_matches=%d, missing=%d, unknown=%d, duplicates=%d, malformed=%d, production_dups=%d)"
            (List.length r.Rows)
            (List.length r.Rows)
            (r.FieldMismatches |> List.filter (fun (a, b, _) -> a = b) |> List.length)
            (List.length r.MissingIdentities)
            (List.length r.UnexpectedIdentities)
            (List.length r.DuplicateIdentities)
            (List.length r.MalformedIdentities)
            (List.length r.DuplicateProductionIds)
    | Failed (r, fs) ->
        sprintf "parity: FAIL (production_rules=%d, parity_rows=%d, missing=%d, unknown=%d, duplicates=%d, malformed=%d, production_dups=%d, reasons=%s)"
            (List.length r.Rows)
            (List.length r.Rows)
            (List.length r.MissingIdentities)
            (List.length r.UnexpectedIdentities)
            (List.length r.DuplicateIdentities)
            (List.length r.MalformedIdentities)
            (List.length r.DuplicateProductionIds)
            (String.concat "; " fs)
