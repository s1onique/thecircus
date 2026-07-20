module Circus.Tooling.SourcePolicy.GateSummary

#nowarn "3261"

/// F# port of the deleted `.factory/regenerate_gate_summary.py`. Writes
/// `.factory/gate-summary.json` using the canonical Leamas v1 status
/// vocabulary and the canonical check-status vocabulary.

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json

type CheckStatus = {
    Name: string
    Status: string
    ExitCode: int
    Command: string list
}

type GateSummaryDoc = {
    SchemaVersion: int
    GeneratedAt: string
    Tool: string
    OverallStatus: string
    ChecksTotal: int
    ChecksPassed: int
    ChecksFailed: int
    ChecksUnavailable: int
    Checks: CheckStatus list
    TestedTreeOid: string
}

let private statusFor (exitCode: int) : string =
    if exitCode = 0 then "pass"
    else "fail"

let private runCheck (name: string) (cmd: string list) : CheckStatus =
    try
        let psi = ProcessStartInfo()
        psi.FileName <- cmd.[0]
        psi.Arguments <- String.concat " " cmd.[1..]
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        let proc = Process.Start psi
        proc.WaitForExit()
        { Name = name
          Status = statusFor proc.ExitCode
          ExitCode = proc.ExitCode
          Command = cmd }
    with
    | ex ->
        { Name = name
          Status = "unavailable"
          ExitCode = -1
          Command = cmd }

let private runGit (args: string list) : string =
    try
        let psi = ProcessStartInfo()
        psi.FileName <- "git"
        psi.Arguments <- String.concat " " args
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        let proc = Process.Start psi
        proc.WaitForExit()
        if proc.ExitCode = 0 then
            (proc.StandardOutput.ReadToEnd()).Trim()
        else
            ""
    with _ -> ""

let private testedTreeOid () : string =
    runGit ["rev-parse"; "HEAD^{tree}"]

let private defaultChecks () : (string * string list) list =
    [
        ("container-publication-policy", [ "tools/Circus.Tooling/bin/Release/net10.0/circus-tooling"; "container-policy"; "verify" ])
        ("executable-shell-tests", [ "bash"; "tests/ci/test_build_publish_shell.sh" ])
        ("action-pin-mutation-test", [ "bash"; "tests/ci/test_action_pin_mutation.sh" ])
    ]

let private nowIso () : string =
    DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")

let private serialize (doc: GateSummaryDoc) : string =
    let opts = JsonSerializerOptions()
    opts.WriteIndented <- true
    JsonSerializer.Serialize(doc, opts)

let regenerate (root: string) : GateSummaryDoc =
    let checks = defaultChecks () |> List.map (fun (n, c) -> runCheck n c)
    let passed = checks |> List.filter (fun c -> c.Status = "pass") |> List.length
    let failed = checks |> List.filter (fun c -> c.Status = "fail") |> List.length
    let unavailable = checks |> List.filter (fun c -> c.Status = "unavailable") |> List.length
    let overall =
        if failed > 0 then "fail"
        else if passed = List.length checks then "pass"
        else "unavailable"
    let doc = {
        SchemaVersion = 1
        GeneratedAt = nowIso ()
        Tool = "circus-regenerate-gate-summary"
        OverallStatus = overall
        ChecksTotal = List.length checks
        ChecksPassed = passed
        ChecksFailed = failed
        ChecksUnavailable = unavailable
        Checks = checks
        TestedTreeOid = testedTreeOid ()
    }
    let target = Path.Combine(root, ".factory", "gate-summary.json")
    let dir = Path.GetDirectoryName target
    if not (Directory.Exists dir) then Directory.CreateDirectory dir |> ignore
    File.WriteAllText(target, serialize doc)
    doc

let runRegenerate (root: string) : int =
    try
        let doc = regenerate root
        if doc.OverallStatus = "pass" then 0 else 1
    with _ -> 2
