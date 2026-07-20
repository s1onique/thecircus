module Circus.Tooling.SourcePolicy.Parity

/// Strict parser and validator for ``factory/container-policy-parity.csv``.

open System
open System.IO
open System.Text

open Circus.Tooling.SourcePolicy.ContainerPolicy

let RequiredHeader : string list =
    [ "legacy_check_id"
      "legacy_behavior"
      "fsharp_check_id"
      "implementation_location"
      "positive_test"
      "negative_mutation_test"
      "status" ]

let ValidStatuses : string list = [ "complete"; "partial — positive only"; "deprecated" ]

type ParityRow = {
    LegacyCheckId: string
    LegacyBehavior: string
    FsharpCheckId: string
    ImplementationLocation: string
    PositiveTest: string
    NegativeMutationTest: string
    Status: string
}

type ValidationReport = {
    Rows: ParityRow list
    MissingIdentities: string list
    UnexpectedIdentities: string list
    DuplicateIdentities: (string * int list) list
    FieldMismatches: (string * string * string) list
    IdentityPathMismatches: (string * string * string) list
    InvalidStatusRows: (int * string) list
    RowParseFailures: (int * string) list
    HeaderOk: bool
    HeaderFailures: string list
}

type ValidationOutcome =
    | Ok of ValidationReport
    | Failed of ValidationReport * failures: string list

let private parseRow (line: string) : string list option =
    if String.IsNullOrEmpty line then Some []
    else
        let cells = ResizeArray<string>()
        let sb = StringBuilder()
        let mutable inQuotes = false
        let mutable i = 0
        let len = line.Length
        while i < len do
            let c = line.[i]
            if inQuotes then
                if c = '"' && i + 1 < len && line.[i + 1] = '"' then
                    sb.Append('"') |> ignore
                    i <- i + 2
                elif c = '"' then
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
                else
                    sb.Append c |> ignore
                    i <- i + 1
        if inQuotes then None
        else
            cells.Add(sb.ToString())
            Some (List.ofSeq cells)

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
                | None -> Result.Error "parity CSV header has unbalanced quoting"
                | Some headers ->
                    if List.sort headers <> List.sort RequiredHeader then
                        let extras = headers |> List.filter (fun h -> not (List.contains h RequiredHeader)) |> List.sort
                        let missing = RequiredHeader |> List.filter (fun h -> not (List.contains h headers)) |> List.sort
                        let msgs =
                            (if not (List.isEmpty extras) then [ sprintf "extra header columns: %s" (String.concat "," extras) ] else [])
                            @ (if not (List.isEmpty missing) then [ sprintf "missing header columns: %s" (String.concat "," missing) ] else [])
                        Result.Error(String.concat "; " msgs)
                    else
                        let mutable rows : ParityRow list = []
                        let mutable errors : string list = []
                        let mutable lineNo = 1
                        for line in dataRows do
                            lineNo <- lineNo + 1
                            match parseRow line with
                            | None -> errors <- sprintf "line %d: malformed quoting" lineNo :: errors
                            | Some cells ->
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
                                    if not (List.contains status ValidStatuses) then
                                        errors <- sprintf "line %d: invalid status '%s'" lineNo status :: errors
                                    if String.IsNullOrWhiteSpace pos then
                                        errors <- sprintf "line %d: blank positive_test" lineNo :: errors
                                    if String.IsNullOrWhiteSpace neg then
                                        errors <- sprintf "line %d: blank negative_mutation_test" lineNo :: errors
                                    if String.IsNullOrWhiteSpace fsId then
                                        errors <- sprintf "line %d: blank fsharp_check_id" lineNo :: errors
                                    if String.IsNullOrWhiteSpace implLoc then
                                        errors <- sprintf "line %d: blank implementation_location" lineNo :: errors
                                    if String.IsNullOrWhiteSpace legacyBehavior then
                                        errors <- sprintf "line %d: blank legacy_behavior" lineNo :: errors
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

/// Normalise a rule identity to its short prefix (e.g. ``CP-01``)
/// so the parity CSV (``CP-01``) and the rule registry
/// (``CP-01_required_files``) compare under a shared canonical form.
let private shortId (id: string) : string =
    let idx = id.IndexOf '_'
    if idx < 0 then id else id.Substring(0, idx)

let validate (rows: ParityRow list) : ValidationOutcome =
    let csvIds = rows |> List.map (fun r -> shortId r.LegacyCheckId)
    let registryIds = ContainerPolicy.CheckIds |> List.map shortId

    let dupIds =
        csvIds
        |> List.groupBy id
        |> List.choose (fun (k, g) -> if List.length g > 1 then Some (k, [ for r in g -> 1 ]) else None)

    let unexpected = csvIds |> List.filter (fun id -> not (List.contains id registryIds)) |> List.sort |> List.distinct
    let missing = registryIds |> List.filter (fun id -> not (List.contains id csvIds)) |> List.sort |> List.distinct

    let fieldMismatches =
        rows
        |> List.filter (fun r -> shortId r.LegacyCheckId <> shortId r.FsharpCheckId)
        |> List.map (fun r -> r.LegacyCheckId, r.FsharpCheckId, "<short prefix mismatch>")

    let identityPathMismatches =
        rows
        |> List.filter (fun r -> not (r.ImplementationLocation.Contains "ContainerPolicy.fs"))
        |> List.map (fun r -> r.LegacyCheckId, r.ImplementationLocation, "<must reference ContainerPolicy.fs>")

    let report =
        { Rows = rows
          MissingIdentities = missing
          UnexpectedIdentities = unexpected
          DuplicateIdentities = dupIds
          FieldMismatches = fieldMismatches
          IdentityPathMismatches = identityPathMismatches
          InvalidStatusRows = []
          RowParseFailures = []
          HeaderOk = true
          HeaderFailures = [] }

    let failures : string list =
        []
        |> List.append (List.map (sprintf "unexpected identity in CSV: %s") unexpected)
        |> List.append (List.map (sprintf "registered identity missing from CSV: %s") missing)
        |> List.append (List.map (fun (i, _) -> sprintf "duplicate identity in CSV: %s" i) dupIds)
        |> List.append (List.map (fun (id, csv, reg) -> sprintf "identity %s field disagreement: csv=%s registry=%s" id csv reg) fieldMismatches)
        |> List.append (List.map (fun (id, csv, reg) -> sprintf "identity %s implementation_location disagreement: csv=%s registry=%s" id csv reg) identityPathMismatches)

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