module Circus.Tooling.SourcePolicy.GateSummary

/// F# implementation of the gate-summary regenerator.
///
/// The container-publication-policy check is sourced from
/// ``ContainerPolicy.verify`` invoked **in-process**.  No subprocess
/// is launched for the container-policy check; status, exit code,
/// violation count, and operational-failure state all come from
/// one structured invocation.
///
/// The other canonical checks (executable-shell-tests,
/// action-pin-mutation-test) are executed via the process runner.
///
/// All checks participate in a single verdict pass: the count
/// ``violations_total`` is grounded in the structured
/// ``ContainerPolicyReport`` produced by the in-process check.

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
    [<JsonPropertyName("violations_operational")>]
    ViolationsOperational: int
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
    | BodyFailure _ -> "unavailable", -1
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
    | BodyFailure _ -> Result.Error -1
    | Cancelled _ -> Result.Error -1

let private testedCommitOid (workingDir: string) : string =
    match runGit [ "rev-parse"; "HEAD" ] workingDir with
    | Result.Ok v -> v
    | Result.Error _ -> ""

let private testedTreeOid (workingDir: string) : string =
    match runGit [ "rev-parse"; "HEAD^{tree}" ] workingDir with
    | Result.Ok v -> v
    | Result.Error _ -> ""

/// Build the container-publication-policy ``CheckStatus`` from a
/// pre-produced ``ContainerPolicyReport``.  The caller is
/// responsible for invoking ``ContainerPolicy.verify`` exactly once
/// and threading the resulting report through both the status and
/// the count derivations.
let private containerPolicyCheck (report: ContainerPolicy.ContainerPolicyReport) : CheckStatus =
    let exitCode, status =
        if not (List.isEmpty report.OperationalFailures) then
            // Operational unavailability surfaces as ``unavailable``,
            // not as a policy failure.  The exit code carries the
            // count of operational failures for downstream tooling.
            -(List.length report.OperationalFailures), "unavailable"
        else if List.isEmpty report.Violations then
            0, "pass"
        else
            1, "fail"
    {
        Name = "container-publication-policy"
        Status = status
        ExitCode = exitCode
        Command = "<in-process ContainerPolicy.verify>"
    }

/// Run one of the external canonical checks (executable-shell-tests,
/// action-pin-mutation-test).  These checks intentionally launch a
/// subprocess because their semantics depend on shell behaviour.
let private runExternalCheck (name: string) (cmd: string list) (workingDir: string) : CheckStatus =
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
        with _ ->
            { Name = name; Status = "unavailable"; ExitCode = -1; Command = String.concat " " cmd }

let private externalChecks (root: string) : CheckStatus list =
    [
        runExternalCheck "executable-shell-tests"
            [ "bash"; "tests/ci/test_build_publish_shell.sh" ] root
        runExternalCheck "action-pin-mutation-test"
            [ "bash"; "tests/ci/test_action_pin_mutation.sh" ] root
        // Canonical source-policy test invocation.  The gate summary
        // records ``source-policy-tests`` as ``pass`` only when
        // ``make test-source-policy`` exits 0.  The Makefile target
        // builds the tooling and the tooling tests, then runs the
        // full Expecto suite through ``Circus.Tooling.Tests``.  This
        // is the same command wired into ``dev-gate-linux`` so the
        // artefact is fail-closed at every consumer.
        //
        // To break the otherwise-circular dependency between
        // ``make test-source-policy`` and the gate producer (which
        // would otherwise re-invoke make via ``gate run``), the
        // caller can set ``CIRCUS_GATE_SKIP_SOURCE_POLICY=1`` so the
        // check is recorded as ``unavailable`` without launching the
        // make invocation.  The wiring test in ``GateSummaryWiringTests``
        // uses this escape hatch so the producer can be exercised
        // without recursing into another ``make test-source-policy``.
        let sourcePolicySkip =
            System.Environment.GetEnvironmentVariable("CIRCUS_GATE_SKIP_SOURCE_POLICY")
        let sourcePolicyCheck =
            if sourcePolicySkip = "1" then
                { Name = "source-policy-tests"
                  Status = "unavailable"
                  ExitCode = -1
                  Command = "make test-source-policy (skipped via CIRCUS_GATE_SKIP_SOURCE_POLICY)" }
            else
                runExternalCheck "source-policy-tests"
                    [ "make"; "test-source-policy" ] root
        sourcePolicyCheck
    ]



let private nowIso () : string =
    DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")

let serialize (doc: GateSummaryDoc) : string =
    let opts = JsonSerializerOptions()
    opts.WriteIndented <- true
    opts.DefaultIgnoreCondition <- JsonIgnoreCondition.Never
    JsonSerializer.Serialize(doc, opts)

let regenerate (root: string) : GateSummaryDoc =
    // SINGLE in-process container-policy invocation drives both the
    // check status and the violation count.  No subprocess is launched
    // for the policy check; status and counts come from the same
    // structured report.
    let report = ContainerPolicy.verify root
    let cpCheck = containerPolicyCheck report
    let extChecks = externalChecks root
    let checks = cpCheck :: extChecks

    let passed      = checks |> List.filter (fun c -> c.Status = "pass") |> List.length
    let skipped     = checks |> List.filter (fun c -> c.Status = "skip") |> List.length
    let unavailable = checks |> List.filter (fun c -> c.Status = "unavailable") |> List.length
    let failedChecks = checks |> List.filter (fun c -> c.Status = "fail") |> List.length

    let overall =
        if failedChecks > 0 || unavailable > 0 then "fail"
        else if passed = List.length checks then "pass"
        else "unavailable"

    let violationsTotal = report.ViolationsTotal
    let violationsOperational = List.length report.OperationalFailures

    let doc = {
        SchemaVersion = 1
        GeneratedAt = nowIso ()
        Tool = "circus-regenerate-gate-summary"
        OverallStatus = overall
        ChecksTotal = List.length checks
        ChecksPassed = passed
        ChecksFailed = failedChecks
        ViolationsTotal = violationsTotal
        ViolationsOperational = violationsOperational
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
    let canonical = Path.Combine(root, "tools", "Circus.Tooling", "bin", "Release", "net10.0", "circus-tooling.dll")
    if File.Exists canonical then canonical
    else canonical

let runRegenerate (root: string) : int =
    try
        match runGit [ "rev-parse"; "HEAD^{tree}" ] root with
        | Result.Error code ->
            stderr.WriteLine(sprintf "gate-summary regenerate: FAIL (git rev-parse HEAD^{tree} exit=%d)" code)
            2
        | Result.Ok _ ->
            let doc = regenerate root
            stdout.WriteLine(sprintf "gate summary written to .factory/gate-summary.json: %s (%d/%d pass, violations=%d operational=%d) commit=%s tree=%s"
                doc.OverallStatus doc.ChecksPassed doc.ChecksTotal doc.ViolationsTotal doc.ViolationsOperational
                (if doc.TestedCommitOid.Length >= 12 then doc.TestedCommitOid.Substring(0, 12) else doc.TestedCommitOid)
                (if doc.TestedTreeOid.Length >= 12 then doc.TestedTreeOid.Substring(0, 12) else doc.TestedTreeOid))
            if doc.OverallStatus = "pass" then 0
            else if doc.OverallStatus = "fail" then 1
            else 2
    with ex ->
        stderr.WriteLine(sprintf "gate-summary regenerate: FAIL (operational: %s: %s)" (ex.GetType().FullName) ex.Message)
        2
