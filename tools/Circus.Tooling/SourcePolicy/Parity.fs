module Circus.Tooling.SourcePolicy.Parity

/// Strict parser and validator for ``factory/container-policy-parity.csv``.
/// CORRECTION01 P1-1: Eliminates prefix aliasing in rule identity comparison.
/// Uses ContainerPolicy.CheckMetadata as the single authoritative source.

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

/// P1-1: Concrete check identity grammar pattern.
/// Valid format: CP-XX_suffix (e.g., CP-01_required_files, CP-10_trusted_runner)
/// Uses \A (absolute start) and \z (absolute end) for strict anchoring.
let private ConcreteIdPattern = Regex(@"\ACP-[0-9]{2}_[a-z0-9]+(?:_[a-z0-9]+)*\z")

/// P1-1: Validates concrete check identity grammar.
let parseConcreteId (id: string) : string option =
    if ConcreteIdPattern.IsMatch(id) then Some id else None

/// P1-1: Reason for malformed identity.
let malformedReason (id: string) : string =
    if id.Length = 0 then "empty identity"
    elif String.IsNullOrWhiteSpace(id) then "whitespace-only identity"
    elif id <> id.Trim() then sprintf "identity has leading/trailing whitespace: '%s'" id
    elif id <> id.ToUpperInvariant() && id.StartsWith("CP-", System.StringComparison.OrdinalIgnoreCase) then
        sprintf "identity has incorrect case: '%s' (expected uppercase)" id
    else sprintf "identity '%s' is not a valid CP-NN_suffix format" id

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
    ProductionRuleCount: int
    ParityRowCount: int
    ExactMatches: int
    MissingIdentities: string list
    UnknownIdentities: string list
    DuplicateIdentities: string list
    DuplicateProductionIds: string list
    FieldMismatches: (string * string * string) list
    IdentityPathMismatches: (string * string * string) list
    IdentityPathFunctionMismatches: (string * string) list
    InvalidStatusRows: (int * string) list
    RowParseFailures: RowError list
    HeaderOk: bool
    HeaderFailures: string list
    MalformedIdentities: string list
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
let private extractFunctionName (implLoc: string) : string option =
    let m = Regex(@"\(([^)]+)\)").Match(implLoc)
    if m.Success then Some m.Groups.[1].Value else None

/// P1-1: Authoritative metadata map from ContainerPolicy.CheckMetadata.
/// Key: exact concrete check ID, Value: CheckMetadata record.
let private metadataByExactId : Map<string, CheckMetadata> =
    CheckMetadata
    |> List.map (fun m -> m.Id, m)
    |> Map.ofList

/// P1-1: Exact identity validation using CheckMetadata as single authority.
let validate (rows: ParityRow list) : ValidationOutcome =
    // P1-1: Production metadata from authoritative source
    let productionMetadata = CheckMetadata
    let productionRuleCount = List.length productionMetadata
    let parityRowCount = List.length rows

    // P1-1: Build metadata ID set for exact membership checks
    let knownIds = productionMetadata |> List.map (fun m -> m.Id) |> Set.ofList

    // P1-1: Extract CSV identities
    let csvLegacyIds = rows |> List.map (fun r -> r.LegacyCheckId)
    let csvFsharpIds = rows |> List.map (fun r -> r.FsharpCheckId)

    // P1-1: Partition into valid (grammar OK) and invalid (grammar fail)
    let parsedLegacy =
        csvLegacyIds
        |> List.map (fun id -> id, parseConcreteId id)
    let validLegacy, invalidLegacy = parsedLegacy |> List.partition (fun (_, p) -> p.IsSome)

    let parsedFsharp =
        csvFsharpIds
        |> List.map (fun id -> id, parseConcreteId id)
    let validFsharp, invalidFsharp = parsedFsharp |> List.partition (fun (_, p) -> p.IsSome)

    // P1-1: Malformed identities from invalid partitions
    let malformedLegacyIds = invalidLegacy |> List.map fst |> List.sort |> List.distinct
    let malformedLegacyReasons = invalidLegacy |> List.map (fun (id, _) -> id, malformedReason id) |> List.sortBy fst
    let malformedFsharpIds = invalidFsharp |> List.map fst |> List.sort |> List.distinct
    let malformedFsharpReasons = invalidFsharp |> List.map (fun (id, _) -> id, malformedReason id) |> List.sortBy fst
    let allMalformed = (malformedLegacyIds @ malformedFsharpIds) |> List.sort |> List.distinct
    let allMalformedReasons = (malformedLegacyReasons @ malformedFsharpReasons) |> List.sortBy fst |> List.distinctBy fst

    // P1-1: Duplicate parity IDs from original list (before Set.ofList)
    let dupCsvIds =
        csvLegacyIds
        |> List.groupBy id
        |> List.choose (fun (k, g) -> if List.length g > 1 then Some k else None)

    // P1-1: Duplicate production IDs from original metadata list (before Set.ofList)
    let productionIds = productionMetadata |> List.map (fun m -> m.Id)
    let dupProductionIds =
        productionIds
        |> List.groupBy id
        |> List.choose (fun (k, g) -> if List.length g > 1 then Some k else None)

    // P1-1: Known IDs from valid legacy partition
    let validLegacyIds = validLegacy |> List.map fst
    let validFsharpIds = validFsharp |> List.map fst

    // P1-1: Unknown identities (valid grammar but absent from metadata) from BOTH columns
    let unknownLegacy =
        validLegacyIds
        |> List.filter (fun id -> not (Set.contains id knownIds))
        |> List.sort |> List.distinct

    // P1-1: Unknown FsharpCheckId identities (valid grammar but absent from metadata)
    let unknownFsharp =
        validFsharpIds
        |> List.filter (fun id -> not (Set.contains id knownIds))
        |> List.sort |> List.distinct

    // P1-1: Combined unknown identities from both columns
    let unknownIdentities =
        (unknownLegacy @ unknownFsharp)
        |> List.sort |> List.distinct

    // P1-1: Missing identities (in production but not in parity)
    let missing =
        knownIds
        |> Set.toList
        |> List.filter (fun id -> not (List.contains id csvLegacyIds))
        |> List.sort |> List.distinct

    // P1-1: Field mismatches
    let fieldMismatches =
        rows
        |> List.filter (fun r -> r.LegacyCheckId <> r.FsharpCheckId)
        |> List.map (fun r -> r.LegacyCheckId, r.FsharpCheckId, "legacy_check_id != fsharp_check_id")

    // P1-1: Path mismatches
    let identityPathMismatches =
        rows
        |> List.filter (fun r -> not (r.ImplementationLocation.Contains "ContainerPolicy.fs"))
        |> List.map (fun r -> r.LegacyCheckId, r.ImplementationLocation, "<must reference ContainerPolicy.fs>")

    // P1-1: Function mismatches using exact metadata lookup
    let identityPathFunctionMismatches =
        rows
        |> List.choose (fun r ->
            // Look up exact identity in metadata
            match Map.tryFind r.LegacyCheckId metadataByExactId with
            | Some metadata ->
                match extractFunctionName r.ImplementationLocation with
                | Some actual when actual = metadata.ImplementationFunction -> None
                | actual -> Some (r.LegacyCheckId, sprintf "expected %s; got %s" metadata.ImplementationFunction (defaultArg actual "<missing>"))
            | None -> None)

    // P1-1: Invalid status rows
    let invalidStatusRows =
        rows
        |> List.mapi (fun i r -> (i + 2, r))
        |> List.filter (fun (_, r) -> not (List.contains r.Status ValidStatuses))
        |> List.map (fun (line, r) -> (line, r.Status))

    // P1-1: Exact matches count
    let exactMatches =
        rows
        |> List.filter (fun r ->
            Set.contains r.LegacyCheckId knownIds &&
            Set.contains r.FsharpCheckId knownIds &&
            r.LegacyCheckId = r.FsharpCheckId)
        |> List.length

    let report = {
        Rows = rows
        ProductionRuleCount = productionRuleCount
        ParityRowCount = parityRowCount
        ExactMatches = exactMatches
        MissingIdentities = missing
        UnknownIdentities = unknownIdentities  // P1-1: Combined from both legacy and fsharp columns
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

    // P1-1: Build failure list (use combined unknownIdentities)
    let failures =
        []
        |> List.append (if productionRuleCount <> parityRowCount then
                           [sprintf "production_rule_count (%d) != parity_row_count (%d)" productionRuleCount parityRowCount]
                        else [])
        |> List.append (if parityRowCount <> exactMatches then
                           [sprintf "parity_row_count (%d) != exact_matches (%d)" parityRowCount exactMatches]
                        else [])
        |> List.append (List.map (sprintf "malformed identity: %s") allMalformed)
        |> List.append (List.map (fun (id, reason) -> sprintf "malformed '%s': %s" id reason) allMalformedReasons)
        |> List.append (List.map (sprintf "unknown identity: %s") unknownIdentities)  // P1-1: Combined from both columns
        |> List.append (List.map (sprintf "missing identity: %s") missing)
        |> List.append (List.map (sprintf "duplicate parity identity: %s") dupCsvIds)
        |> List.append (List.map (sprintf "duplicate production identity: %s") dupProductionIds)
        |> List.append (List.map (fun (csv, fs, reason) -> sprintf "field mismatch: csv=%s fsharp=%s (%s)" csv fs reason) fieldMismatches)
        |> List.append (List.map (fun (id, csv, reg) -> sprintf "path mismatch: %s csv=%s registry=%s" id csv reg) identityPathMismatches)
        |> List.append (List.map (fun (id, msg) -> sprintf "function mismatch: %s (%s)" id msg) identityPathFunctionMismatches)
        |> List.append (List.map (fun (line, status) -> sprintf "line %d: invalid status '%s'" line status) invalidStatusRows)

    if List.isEmpty failures then Ok report
    else Failed (report, failures)

let validateFile (path: string) : ValidationOutcome =
    match parse path with
    | Result.Error e ->
        Failed ({ Rows = []
                  ProductionRuleCount = 0
                  ParityRowCount = 0
                  ExactMatches = 0
                  MissingIdentities = []
                  UnknownIdentities = []
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

/// P1-1: Enhanced summary with stored mechanical accounting.
let renderSummary (outcome: ValidationOutcome) : string =
    match outcome with
    | Ok r ->
        sprintf "parity: PASS (production_rules=%d, parity_rows=%d, exact_matches=%d, missing=%d, unknown=%d, duplicates=%d, malformed=%d)"
            r.ProductionRuleCount
            r.ParityRowCount
            r.ExactMatches
            (List.length r.MissingIdentities)
            (List.length r.UnknownIdentities)
            (List.length r.DuplicateIdentities)
            (List.length r.MalformedIdentities)
    | Failed (r, fs) ->
        sprintf "parity: FAIL (production_rules=%d, parity_rows=%d, exact_matches=%d, missing=%d, unknown=%d, duplicates=%d, malformed=%d, reasons=%s)"
            r.ProductionRuleCount
            r.ParityRowCount
            r.ExactMatches
            (List.length r.MissingIdentities)
            (List.length r.UnknownIdentities)
            (List.length r.DuplicateIdentities)
            (List.length r.MalformedIdentities)
            (String.concat "; " fs)
