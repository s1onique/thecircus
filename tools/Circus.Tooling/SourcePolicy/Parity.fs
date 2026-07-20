module Circus.Tooling.SourcePolicy.Parity

/// Strict parser and validator for ``factory/container-policy-parity.csv``.
///
/// The parity CSV is the canonical ledger mapping every legacy Python
/// container-policy check to its F# implementation, its positive
/// test surface, and (now) its **executable negative mutation test**.
/// This module mechanically verifies the CSV against the
/// authoritative rule registry so the document can no longer be
/// trusted as static prose: any drift between the parity ledger
/// and the rule implementation surfaces as a deterministic
/// failure.

open System
open System.Collections.Generic
open System.IO
open System.Text

open Circus.Tooling.SourcePolicy.ContainerPolicy

// -----------------------------------------------------------------------------
// Wire shape
// -----------------------------------------------------------------------------

/// Header columns the ledger is required to carry, in canonical order.
/// The schema is intentionally narrow: any extra column is rejected so
/// downstream readers and the producer cannot drift.
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

type ParseFailure =
    | HeaderMissing
    | HeaderHasDuplicates of column: string
    | HeaderHasExtraColumns of columns: string list
    | RowFieldCountMismatch of line: int * expected: int * actual: int
    | MalformedQuoting of line: int
    | MissingIdentity of line: int
    | DuplicateIdentity of identity: string * line: int
    | InvalidStatus of line: int * actual: string
    | BlankRequiredField of line: int * field: string
    | IdentityPathDisagreement of identity: string * csv: string * registry: string
    | UnexpectedIdentity of identity: string
    | MissingIdentityInCsv of identity: string
    | DuplicateIdentityInCsv of identity: string * lines: int list

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

let private renderFailures (failures: ParseFailure list) : string list =
    failures
    |> List.map (function
        | HeaderMissing -> "missing required header row"
        | HeaderHasDuplicates c -> sprintf "duplicate header column: %s" c
        | HeaderHasExtraColumns cs ->
            sprintf "unexpected header columns: %s" (String.concat "," cs)
        | RowFieldCountMismatch (l, e, a) ->
            sprintf "row field count mismatch at line %d (expected %d, got %d)" l e a
        | MalformedQuoting l -> sprintf "malformed quoting at line %d" l
        | MissingIdentity l -> sprintf "missing identity at line %d" l
        | DuplicateIdentity (i, l) -> sprintf "duplicate identity at line %d: %s" l i
        | InvalidStatus (l, a) -> sprintf "invalid status at line %d: %s" l a
        | BlankRequiredField (l, f) -> sprintf "blank required field at line %d: %s" l f
        | IdentityPathDisagreement (i, c, r) ->
            sprintf "identity %s disagreement: csv=%s registry=%s" i c r
        | UnexpectedIdentity i -> sprintf "unexpected identity in CSV: %s" i
        | MissingIdentityInCsv i -> sprintf "registered identity missing from CSV: %s" i
        | DuplicateIdentityInCsv (i, ls) ->
            sprintf "duplicate identity in CSV: %s (lines %s)" i (String.concat "," (List.map string ls)))

// -----------------------------------------------------------------------------
// CSV parsing
// -----------------------------------------------------------------------------

/// Parse a single CSV row honouring double-quote escaping.  Returns
/// ``None`` if the row has unbalanced quoting.
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

// -----------------------------------------------------------------------------
// Public surface
// -----------------------------------------------------------------------------

/// Parse a parity CSV document into the typed ``ParityRow`` list.  All
/// syntactic failures are accumulated and returned as a single
/// ``Error`` so the caller can report every defect at once.
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
                        let dup = headers |> List.groupBy id |> List.filter (fun (_, g) -> List.length g > 1) |> List.map fst
                        let msgs =
                            (if not (List.isEmpty extras) then [ sprintf "extra header columns: %s" (String.concat "," extras) ] else [])
                            @ (if not (List.isEmpty missing) then [ sprintf "missing header columns: %s" (String.concat "," missing) ] else [])
                            @ (List.map (fun d -> sprintf "duplicate header column: %s" d) dup)
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
                                    let lookup key = fields |> List.pick (fun (k, v) -> if k = key then Some v else None) []
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

/// Validate the parsed ledger against the authoritative rule
/// registry.  Identity equality is enforced as a set comparison; row
/// ordering is normalised for comparison but duplicates still fail.
let validate (rows: ParityRow list) : ValidationOutcome =
    let csvIds = rows |> List.map (fun r -> r.LegacyCheckId)
    let registryIds = ContainerPolicy.CheckIds

    let dupIds =
        csvIds
        |> List.groupBy id
        |> List.choose (fun (k, g) -> if List.length g > 1 then Some (k, [ for r in g -> 1 ]) else None)

    let unexpected = csvIds |> List.filter (fun id -> not (List.contains id registryIds)) |> List.sort
    let missing = registryIds |> List.filter (fun id -> not (List.contains id csvIds)) |> List.sort

    let fieldMismatches =
        rows
        |> List.filter (fun r -> r.FsharpCheckId <> r.LegacyCheckId)
        |> List.map (fun r -> r.LegacyCheckId, r.FsharpCheckId, r.LegacyCheckId)

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

    let failures =
        []
        |> List.append (List.map (fun i -> UnexpectedIdentity i) unexpected)
        |> List.append (List.map (fun i -> MissingIdentityInCsv i) missing)
        |> List.append (List.map (fun (i, _) -> DuplicateIdentityInCsv (i, [1])) dupIds)
        |> List.append (List.map (fun (id, csv, reg) -> IdentityPathDisagreement (id, csv, reg)) fieldMismatches)
        |> List.append (List.map (fun (id, csv, reg) -> IdentityPathDisagreement (id, csv, reg)) identityPathMismatches)

    if List.isEmpty failures then Ok report
    else Failed (report, renderFailures failures)

/// Combined read+validate against the registry.
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

/// Render a human-readable summary of a validation outcome.  The
/// line is intentionally compact and machine-readable so it can
/// appear in the gate-summary closure artefact.
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