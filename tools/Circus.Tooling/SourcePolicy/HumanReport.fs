module Circus.Tooling.SourcePolicy.HumanReport

open System.Text

open Circus.Tooling.SourcePolicy.Language
open Circus.Tooling.SourcePolicy.Domain

let private renderFinding (f: Finding) : string =
    let location =
        match f.Line with
        | Some n -> sprintf "%s:%d" f.Path n
        | None -> f.Path
    sprintf "  %s [%s] %s" location f.Code.AsTag f.Detail

let renderVerify (outcome: VerificationOutcome) : string =
    if List.isEmpty outcome.Findings then
        sprintf "source-policy verify: PASS (files=%d, baseline=%d)"
            outcome.FilesExamined outcome.BaselineEntries
    else
        let sb = StringBuilder()
        sb.AppendLine "source-policy verify: FAIL" |> ignore
        sb.AppendLine(sprintf "  files examined: %d" outcome.FilesExamined) |> ignore
        sb.AppendLine(sprintf "  baseline entries: %d" outcome.BaselineEntries) |> ignore
        sb.AppendLine "  violations:" |> ignore
        for f in outcome.Findings do
            sb.AppendLine(renderFinding f) |> ignore
        sb.ToString().TrimEnd()

let renderOperationalError (detail: string) : string =
    sprintf "source-policy operational error: %s" detail
