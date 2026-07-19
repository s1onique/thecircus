module Circus.Tooling.SourcePolicy.JsonReport

open System.Globalization
open System.Text

open Circus.Tooling.SourcePolicy.Language
open Circus.Tooling.SourcePolicy.Domain

let SchemaVersion = 1

let private escape (s: string) : string =
    let sb = StringBuilder()
    sb.Append('"') |> ignore
    for c in s do
        if c = '\\' then sb.Append("\\\\") |> ignore
        elif c = '"' then sb.Append("\\\"") |> ignore
        elif c = '\n' then sb.Append("\\n") |> ignore
        elif c = '\r' then sb.Append("\\r") |> ignore
        elif c = '\t' then sb.Append("\\t") |> ignore
        elif int c < 0x20 then sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", int c) |> ignore
        else sb.Append c |> ignore
    sb.Append('"') |> ignore
    sb.ToString()

let private optStr (label: string) (value: string option) =
    match value with
    | Some s -> sprintf ",\"%s\":%s" label (escape s)
    | None -> ""

let private renderFinding (f: Finding) : string =
    sprintf
        "{\"path\":%s,\"code\":%s,\"line\":%s,\"rule\":%s,\"detail\":%s%s%s}"
        (escape f.Path)
        (escape f.Code.AsTag)
        (match f.Line with | Some n -> string n | None -> "null")
        (escape f.Rule)
        (escape f.Detail)
        (optStr "expected" f.Expected)
        (optStr "actual" f.Actual)

let renderVerify (outcome: VerificationOutcome) : string =
    let findings = outcome.Findings |> List.map renderFinding |> String.concat ","
    sprintf
        "{\"schema_version\":%d,\"repository_root\":\"%s\",\"policy_status\":\"%s\",\"files_examined\":%d,\"baseline_entries\":%d,\"violations\":[%s]}"
        SchemaVersion
        (outcome.RepositoryRoot.Replace('\\', '/'))
        (if List.isEmpty outcome.Findings then "pass" else "fail")
        outcome.FilesExamined
        outcome.BaselineEntries
        findings

let renderOperationalError (code: string) (detail: string) : string =
    sprintf
        "{\"schema_version\":%d,\"error_code\":%s,\"detail\":%s,\"verdict\":\"operational_error\"}"
        SchemaVersion
        (escape code)
        (escape detail)
