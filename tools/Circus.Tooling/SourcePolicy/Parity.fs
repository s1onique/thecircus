module Circus.Tooling.SourcePolicy.Parity

/// Strict parser and validator for ``factory/container-policy-parity.csv``.

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
    FieldMismatches: (string * string * string) list
    IdentityPathMismatches: (string * string * string) list
    IdentityPathFunctionMismatches: (string * string) list
    InvalidStatusRows: (int * string) list
    RowParseFailures: RowError list
    HeaderOk: bool
    HeaderFailures: string list
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

let private canonicalId (id: string) : string =
    let m = Regex("^(CP-\d+)").Match(id)
    if m.Success then m.Groups.[1].Value else id

let private extractFunctionName (implLoc: string) : string option =
    let m = Regex(@"\(([^)]+)\)").Match(implLoc)
    if m.Success then Some m.Groups.[1].Value else None

/// Map keyed by canonical ``CP-NN`` short prefix -> expected function
/// name.  Using the short prefix keeps the lookup consistent with
/// ``canonicalId`` so a missing trailing ``_name`` does not silently
/// bypass the function-name assertion.
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

let validate (rows: ParityRow list) : ValidationOutcome =
    let csvIds = rows |> List.map (fun r -> canonicalId r.LegacyCheckId)
    let registryIds = ContainerPolicy.CheckIds |> List.map canonicalId

    let dupIds =
        csvIds
        |> List.groupBy id
        |> List.choose (fun (k, g) -> if List.length g > 1 then Some k else None)

    let unexpected = csvIds |> List.filter (fun id -> not (List.contains id registryIds)) |> List.sort |> List.distinct
    let missing = registryIds |> List.filter (fun id -> not (List.contains id csvIds)) |> List.sort |> List.distinct

    let fieldMismatches =
        rows
        |> List.filter (fun r -> canonicalId r.LegacyCheckId <> canonicalId r.FsharpCheckId)
        |> List.map (fun r -> r.LegacyCheckId, r.FsharpCheckId, "<short prefix mismatch>")

    let identityPathMismatches =
        rows
        |> List.filter (fun r -> not (r.ImplementationLocation.Contains "ContainerPolicy.fs"))
        |> List.map (fun r -> r.LegacyCheckId, r.ImplementationLocation, "<must reference ContainerPolicy.fs>")

    let identityPathFunctionMismatches =
        rows
        |> List.choose (fun r ->
            let key = canonicalId r.LegacyCheckId
            match Map.tryFind key ruleFunctionName with
            | Some expected ->
                match extractFunctionName r.ImplementationLocation with
                | Some actual when actual = expected -> None
                | _ -> Some (r.LegacyCheckId, sprintf "expected %s; got %s" expected (defaultArg (extractFunctionName r.ImplementationLocation) "<missing>"))
            | None -> None)

    let invalidStatusRows =
        rows
        |> List.mapi (fun i r -> (i + 2, r))
        |> List.filter (fun (_, r) -> not (List.contains r.Status ValidStatuses))
        |> List.map (fun (line, r) -> (line, r.Status))

    let report =
        { Rows = rows
          MissingIdentities = missing
          UnexpectedIdentities = unexpected
          DuplicateIdentities = dupIds
          FieldMismatches = fieldMismatches
          IdentityPathMismatches = identityPathMismatches
          IdentityPathFunctionMismatches = identityPathFunctionMismatches
          InvalidStatusRows = invalidStatusRows
          RowParseFailures = []
          HeaderOk = true
          HeaderFailures = [] }

    let failures : string list =
        []
        |> List.append (List.map (sprintf "unexpected identity in CSV: %s") unexpected)
        |> List.append (List.map (sprintf "registered identity missing from CSV: %s") missing)
        |> List.append (List.map (sprintf "duplicate identity in CSV: %s") dupIds)
        |> List.append (List.map (fun (id, csv, reg) -> sprintf "identity %s field disagreement: csv=%s registry=%s" id csv reg) fieldMismatches)
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
                  FieldMismatches = []
                  IdentityPathMismatches = []
                  IdentityPathFunctionMismatches = []
                  InvalidStatusRows = []
                  RowParseFailures = []
                  HeaderOk = false
                  HeaderFailures = [ e ] },
               [ e ])
    | Result.Ok rows -> validate rows

let renderSummary (outcome: ValidationOutcome) : string =
    match outcome with
    | Ok r ->
        sprintf "parity: PASS (identities=%d, missing=%d, unexpected=%d, duplicates=%d, field_mismatches=%d)"
            (List.length r.Rows)
            (List.length r.MissingIdentities)
            (List.length r.UnexpectedIdentities)
            (List.length r.DuplicateIdentities)
            (List.length r.FieldMismatches)
    | Failed (r, fs) ->
        sprintf "parity: FAIL (identities=%d, missing=%d, unexpected=%d, duplicates=%d, field_mismatches=%d, reasons=%s)"
            (List.length r.Rows)
            (List.length r.MissingIdentities)
            (List.length r.UnexpectedIdentities)
            (List.length r.DuplicateIdentities)
            (List.length r.FieldMismatches)
            (String.concat "; " fs)
