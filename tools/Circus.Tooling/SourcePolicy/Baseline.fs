module Circus.Tooling.SourcePolicy.Baseline

open System
open System.IO

open Circus.Tooling.SourcePolicy.Language
open Circus.Tooling.SourcePolicy.Paths
open Circus.Tooling.SourcePolicy.Domain

let Header = "path,violation_kind,physical_lines,sha256,owner,successor_act,reason"

let PermittedBaselineKinds : string list = [ "oversized_shell" ]

type LoadResult =
    | Loaded of BaselineEntry list
    | Missing
    | Malformed of string


type Match =
    | MatchOk
    | DigestMismatch of expected: string * actual: string
    | MeasurementMismatch of expected: int * actual: int
    | MissingForCurrentFile
let private parseCsvRow (line: string) : string list option =
    if String.IsNullOrEmpty line then None
    else
        let mutable cells = []
        let mutable current = System.Text.StringBuilder()
        let mutable inQuotes = false
        let mutable i = 0
        let len = line.Length
        while i < len do
            let c = line.[i]
            if inQuotes then
                if c = '"' && i + 1 < len && line.[i + 1] = '"' then
                    current.Append('"') |> ignore
                    i <- i + 2
                elif c = '"' then
                    inQuotes <- false
                    i <- i + 1
                else
                    current.Append c |> ignore
                    i <- i + 1
            else
                if c = ',' then
                    cells <- (current.ToString()) :: cells
                    current.Clear() |> ignore
                    i <- i + 1
                elif c = '"' && current.Length = 0 then
                    inQuotes <- true
                    i <- i + 1
                else
                    current.Append c |> ignore
                    i <- i + 1
        cells <- (current.ToString()) :: cells
        if inQuotes then None
        else Some(List.rev cells)

let private validateRow (cells: string list) (lineNumber: int) : string option =
    match cells with
    | [ path; kind; linesStr; sha; owner; successor; reason ] ->
        if String.IsNullOrWhiteSpace path then
            Some(sprintf "line %d: empty path" lineNumber)
        elif String.IsNullOrWhiteSpace kind then
            Some(sprintf "line %d: empty violation_kind" lineNumber)
        elif not (List.contains kind PermittedBaselineKinds) then
            Some(sprintf "line %d: forbidden baseline kind '%s'" lineNumber kind)
        elif not (Int32.TryParse linesStr |> fst) then
            Some(sprintf "line %d: physical_lines is not an integer" lineNumber)
        elif sha.Length <> 64 || sha <> sha.ToLowerInvariant() || not (sha |> Seq.forall System.Char.IsLetterOrDigit) then
            Some(sprintf "line %d: sha256 is not 64 lowercase hex characters" lineNumber)
        elif String.IsNullOrWhiteSpace owner then
            Some(sprintf "line %d: empty owner" lineNumber)
        elif String.IsNullOrWhiteSpace successor then
            Some(sprintf "line %d: empty successor_act" lineNumber)
        elif String.IsNullOrWhiteSpace reason then
            Some(sprintf "line %d: empty reason" lineNumber)
        else None
    | _ -> Some(sprintf "line %d: expected 7 fields, got %d" lineNumber (List.length cells))

let private entryOf (cells: string list) : BaselineEntry =
    match cells with
    | [ path; kind; linesStr; sha; owner; successor; reason ] ->
        { Path = toPosix path
          ViolationKind = kind
          PhysicalLines = Int32.Parse linesStr
          Sha256 = sha
          Owner = owner
          SuccessorAct = successor
          Reason = reason }
    | _ -> failwithf "Internal error: row shape mismatch"

let load (repoRoot: string) : LoadResult =
    let path = Path.Combine(repoRoot, "factory", "source-policy-baseline.csv")
    if not (File.Exists path) then Missing
    else
        try
            let lines = File.ReadAllLines path |> Array.filter (fun l -> not (String.IsNullOrEmpty l))
            if lines.Length = 0 then Malformed "baseline is empty"
            elif lines.[0] <> Header then Malformed "baseline header row is missing or incorrect"
            else
                let mutable entries = []
                let mutable previousPath : string option = None
                let mutable sortError : string option = None
                let mutable rowError : string option = None
                for i in 1 .. lines.Length - 1 do
                    let lineNumber = i + 1
                    let line = lines.[i]
                    match parseCsvRow line with
                    | Some cells ->
                        match validateRow cells lineNumber with
                        | Some err -> rowError <- Some err
                        | None ->
                            let entry = entryOf cells
                            let entryPath = entry.Path
                            match previousPath with
                            | Some p when entryPath.CompareTo(p) <= 0 ->
                                sortError <- Some(sprintf "line %d: path '%s' is not greater than previous path '%s'" lineNumber entryPath p)
                            | _ -> ()
                            if previousPath = Some entryPath then
                                rowError <- Some(sprintf "line %d: duplicate path '%s'" lineNumber entryPath)
                            previousPath <- Some entryPath
                            entries <- entry :: entries
                    | None -> rowError <- Some(sprintf "line %d: malformed CSV (unbalanced quote)" lineNumber)
                match sortError, rowError with
                | Some e, _ -> Malformed e
                | _, Some e -> Malformed e
                | None, None -> Loaded(entries |> List.rev)
        with
        | ex -> Malformed ex.Message

let write (repoRoot: string) (entries: BaselineEntry list) : unit =
    let path = Path.Combine(repoRoot, "factory", "source-policy-baseline.csv")
    let dir = Path.GetDirectoryName path
    if not (Directory.Exists dir) then Directory.CreateDirectory dir |> ignore
    use writer = new StreamWriter(path, false, System.Text.Encoding.UTF8)
    writer.NewLine <- "\n"
    writer.WriteLine Header
    let sorted = entries |> List.sortBy (fun e -> e.Path)
    for e in sorted do
        let fields =
            [ e.Path; e.ViolationKind; string e.PhysicalLines
              e.Sha256; e.Owner; e.SuccessorAct; e.Reason ]
        let line = String.concat "," fields
        writer.WriteLine line

let matchEntry (entries: BaselineEntry list) (relativePath: string) (sha: string) (physicalLines: int) : Match =
    let norm = toPosix relativePath
    match entries |> List.tryFind (fun e -> e.Path = norm) with
    | None -> MissingForCurrentFile
    | Some entry ->
        if entry.Sha256 <> sha then DigestMismatch(entry.Sha256, sha)
        elif entry.PhysicalLines <> physicalLines then MeasurementMismatch(entry.PhysicalLines, physicalLines)
        else MatchOk

let staleFindings (entries: BaselineEntry list) (currentPaths: Set<string>) : Finding list =
    entries
    |> List.filter (fun e -> not (currentPaths.Contains e.Path))
    |> List.map (fun e ->
        { Path = e.Path
          Code = BaselineStale
          Line = None
          Detail = sprintf "baseline row no longer corresponds to a current violation (successor=%s)" e.SuccessorAct
          Rule = "baseline/no-stale-rows"
          Expected = Some "<current violation>"
          Actual = Some e.Path })

let expansionFindings (entries: BaselineEntry list) (currentPaths: Set<string>) : Finding list =
    entries
    |> List.filter (fun e -> not (currentPaths.Contains e.Path))
    |> List.map (fun e ->
        { Path = e.Path
          Code = BaselineExpansion
          Line = None
          Detail = sprintf "baseline expansion detected for '%s'" e.Path
          Rule = "baseline/no-expansion"
          Expected = Some "no new baseline rows"
          Actual = Some e.Path })
