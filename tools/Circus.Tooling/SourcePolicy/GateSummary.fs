module Circus.Tooling.SourcePolicy.GateSummary

/// F# implementation of the gate-summary regenerator.
///
/// ``violations_total`` is **derived** from the authoritative
/// container-policy producer by invoking ``ContainerPolicy.verify``
/// in-process and reading the resulting ``ContainerPolicyReport``.
/// No human-text parsing is performed.

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Runtime.InteropServices
open System.Threading

open Circus.Tooling.SourcePolicy.Inventory
open Circus.Tooling.SourcePolicy.ProcessRunner
open Circus.Tooling.SourcePolicy.ContainerPolicy

type CheckStatus = {
    [<JsonPropertyName("name")>]
    Name: string
    [<JsonPropertyName("status")>]
    Status: string
    [<JsonPropertyName("exit_code")>]
    ExitCode: int
    [<JsonPropertyName("command")>]
    Command: string
}

type GateSummaryDoc = {
    [<JsonPropertyName("schema_version")>]
    SchemaVersion: int
    [<JsonPropertyName("generated_at")>]
    GeneratedAt: string
    [<JsonPropertyName("tool")>]
    Tool: string
    [<JsonPropertyName("overall_status")>]
    OverallStatus: string
    [<JsonPropertyName("checks_total")>]
    ChecksTotal: int
    [<JsonPropertyName("checks_passed")>]
    ChecksPassed: int
    [<JsonPropertyName("checks_failed")>]
    ChecksFailed: int
    [<JsonPropertyName("violations_total")>]
    ViolationsTotal: int
    [<JsonPropertyName("checks_skipped")>]
    ChecksSkipped: int
    [<JsonPropertyName("checks_unavailable")>]
    ChecksUnavailable: int
    [<JsonPropertyName("checks")>]
    Checks: CheckStatus list
    [<JsonPropertyName("tested_commit_oid")>]
    TestedCommitOid: string
    [<JsonPropertyName("tested_tree_oid")>]
    TestedTreeOid: string
}

let internal ValidOverallStatuses = set [ "pass"; "fail"; "unavailable" ]
let internal ValidCheckStatuses   = set [ "pass"; "fail"; "skip"; "unavailable" ]

let statusForExitCode (exitCode: int) : string =
    if exitCode = 0 then "pass" else "fail"

let private statusForOutcome (outcome: ProcessOutcome) : string * int =
    match outcome with
    | Exited (0, _) -> "pass", 0
    | Exited (n, _) -> "fail", n
    | NonzeroExit (n, _) -> "fail", n
    | SpawnFailure _ -> "unavailable", -1
    | CleanupFailure _ -> "unavailable", -1
    | OutputFailure _ -> "unavailable", -1
    | Cancelled _ -> "unavailable", -1

let private runGit (args: string list) (workingDir: string) : Result<string, int> =
    let argv = "git" :: args
    let result = runProcessText argv (Some workingDir) CancellationToken.None
    match result.Outcome with
    | Exited (0, _) -> Result.Ok (result.Output.Trim())
    | Exited _ -> Result.Error 1
    | NonzeroExit (code, _) -> Result.Error code
    | SpawnFailure _ -> Result.Error -1
    | CleanupFailure _ -> Result.Error -1
    | OutputFailure _ -> Result.Error -1
    | Cancelled _ -> Result.Error -1

let private runGitNul (args: string list) (workingDir: string) : Result<byte[], int> =
    let argv = "git" :: args
    let result = runProcessBytes argv (Some workingDir) CancellationToken.None
    match result.Outcome with
    | Exited (0, _) -> Result.Ok result.Output
    | Exited _ -> Result.Error 1
    | NonzeroExit (code, _) -> Result.Error code
    | SpawnFailure _ -> Result.Error -1
    | CleanupFailure _ -> Result.Error -1
    | OutputFailure _ -> Result.Error -1
    | Cancelled _ -> Result.Error -1

let private testedCommitOid (workingDir: string) : string =
    match runGit [ "rev-parse"; "HEAD" ] workingDir with
    | Result.Ok v -> v
    | Result.Error _ -> ""

let private testedTreeOid (workingDir: string) : string =
    match runGit [ "rev-parse"; "HEAD^{tree}" ] workingDir with
    | Result.Ok v -> v
    | Result.Error _ -> ""

/// Split a NUL-delimited byte buffer into non-empty UTF-8 strings.
/// Uses a manual scan so the byte boundary is exact and the result
/// is deterministic on unusual but valid filenames.
let splitNulInventory (raw: byte[]) : string list =
    if raw.Length = 0 then []
    else
        let mutable acc : System.Collections.Generic.List<string> = System.Collections.Generic.List()
        let mutable start = 0
        let mutable i = 0
        let mutable sawNul = false
        while i < raw.Length do
            if raw.[i] = byte 0 then
                let len = i - start
                if len > 0 then
                    acc.Add(Encoding.UTF8.GetString(raw, start, len))
                start <- i + 1
                sawNul <- true
            i <- i + 1
        if start < raw.Length then
            // Trailing non-NUL bytes: not a valid NUL inventory but be
            // defensive — return what we got so far plus the tail.
            acc.Add(Encoding.UTF8.GetString(raw, start, raw.Length - start))
        elif not sawNul then
            // No NUL at all: the entire buffer is one record.
            acc.Clear()
            acc.Add(Encoding.UTF8.GetString(raw, 0, raw.Length))
        acc |> Seq.toList

let gitTrackedFilesResult (workingDir: string) : Result<string list, int> =
    match runGitNul [ "ls-files"; "-z" ] workingDir with
    | Result.Ok raw -> Result.Ok (splitNulInventory raw)
    | Result.Error code -> Result.Error code

let private defaultChecks (toolingDll: string) : (string * string list) list =
    [
        ("container-publication-policy",
         [ "dotnet"; toolingDll; "container-policy"; "verify" ])
        ("executable-shell-tests",
         [ "bash"; "tests/ci/test_build_publish_shell.sh" ])
        ("action-pin-mutation-test",
         [ "bash"; "tests/ci/test_action_pin_mutation.sh" ])
    ]

let private nowIso () : string =
    DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")

let serialize (doc: GateSummaryDoc) : string =
    let opts = JsonSerializerOptions()
    opts.WriteIndented <- true
    opts.DefaultIgnoreCondition <- JsonIgnoreCondition.Never
    JsonSerializer.Serialize(doc, opts)

/// Compute the authoritative ``violations_total`` from the
/// container-policy producer.  In-process invocation of
/// ``ContainerPolicy.verify`` returns a structured report; no
/// human-text parsing is performed.
let private authoritativeViolations (root: string) : int =
    try
        let report = ContainerPolicy.verify root
        if not (List.isEmpty report.OperationalFailures) then
            // Operational failures are not zero violations.
            -(List.length report.OperationalFailures)
        else
            report.ViolationsTotal
    with _ ->
        -1

let private runCheck (name: string) (cmd: string list) (workingDir: string) : CheckStatus =
    if List.isEmpty cmd then
        { Name = name; Status = "unavailable"; ExitCode = -1; Command = "" }
    else
        try
            let result = runProcessText cmd (Some workingDir) CancellationToken.None
            let status, exitCode = statusForOutcome result.Outcome
            { Name = name
              Status = status
              ExitCode = exitCode
              Command = String.concat " " cmd }
        with
        | _ ->
            { Name = name
              Status = "unavailable"
              ExitCode = -1
              Command = String.concat " " cmd }

let regenerate (root: string) (toolingDll: string) : GateSummaryDoc =
    let checks =
        defaultChecks toolingDll
        |> List.map (fun (n, c) -> runCheck n c root)

    let passed      = checks |> List.filter (fun c -> c.Status = "pass")       |> List.length
    let skipped     = checks |> List.filter (fun c -> c.Status = "skip")       |> List.length
    let unavailable = checks |> List.filter (fun c -> c.Status = "unavailable") |> List.length
    let failedChecks = checks |> List.filter (fun c -> c.Status = "fail") |> List.length

    let overall =
        if failedChecks > 0 || unavailable > 0 then "fail"
        else if passed = List.length checks then "pass"
        else "unavailable"

    let violationsTotal = authoritativeViolations root

    let doc = {
        SchemaVersion = 1
        GeneratedAt = nowIso ()
        Tool = "circus-regenerate-gate-summary"
        OverallStatus = overall
        ChecksTotal = List.length checks
        ChecksPassed = passed
        ChecksFailed = failedChecks
        ViolationsTotal = violationsTotal
        ChecksSkipped = skipped
        ChecksUnavailable = unavailable
        Checks = checks
        TestedCommitOid = testedCommitOid root
        TestedTreeOid = testedTreeOid root
    }

    let target = Path.Combine(root, ".factory", "gate-summary.json")
    let dir = Path.GetDirectoryName target
    if not (Directory.Exists dir) then Directory.CreateDirectory dir |> ignore
    let serialized = serialize doc
    File.WriteAllText(target, serialized + "\n")
    doc

let internal resolveToolingDll (root: string) : string =
    let ridSubdir =
        if RuntimeInformation.IsOSPlatform(OSPlatform.Linux) then "linux-x64"
        elif RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then "win-x64"
        else "osx-arm64"

    let canonical = Path.Combine(root, "tools", "Circus.Tooling", "bin", "Release", "net10.0", "circus-tooling.dll")
    if File.Exists canonical then canonical
    else Path.Combine(root, "tools", "Circus.Tooling", "bin", "Release", "net10.0", ridSubdir, "circus-tooling.dll")

let runRegenerate (root: string) : int =
    try
        match runGit [ "rev-parse"; "HEAD^{tree}" ] root with
        | Result.Error code ->
            stderr.WriteLine(sprintf "gate-summary regenerate: FAIL (git rev-parse HEAD^{tree} exit=%d)" code)
            2
        | Result.Ok _ ->
            let toolingDll = resolveToolingDll root
            let doc = regenerate root toolingDll
            stdout.WriteLine(sprintf "gate summary written to .factory/gate-summary.json: %s (%d/%d pass, violations=%d) commit=%s tree=%s"
                doc.OverallStatus doc.ChecksPassed doc.ChecksTotal doc.ViolationsTotal
                (if doc.TestedCommitOid.Length >= 12 then doc.TestedCommitOid.Substring(0, 12) else doc.TestedCommitOid)
                (if doc.TestedTreeOid.Length >= 12 then doc.TestedTreeOid.Substring(0, 12) else doc.TestedTreeOid))
            if doc.OverallStatus = "pass" then 0
            else if doc.OverallStatus = "fail" then 1
            else 2
    with ex ->
        stderr.WriteLine(sprintf "gate-summary regenerate: FAIL (operational: %s: %s)" (ex.GetType().FullName) ex.Message)
        2
