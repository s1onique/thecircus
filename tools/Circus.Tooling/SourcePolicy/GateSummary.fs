module Circus.Tooling.SourcePolicy.GateSummary

/// F# implementation of the gate-summary regenerator.
///
/// Document construction is separated from artifact writing so
/// tests can exercise the producer with an injected
/// ``ExternalCheckRunner`` and write only to a unique temporary
/// path.  The production ``regenerate`` command composes them
/// against the real checkout.
///
/// The container-publication-policy check is sourced from
/// ``ContainerPolicy.verify`` invoked **in-process**.  No subprocess
/// is launched for the container-policy check; status, exit code,
/// violation count, and operational-failure state all come from
/// one structured invocation.  The other canonical checks are
/// delegated to an injected runner so tests can prove the exact
/// command contract without spawning real subprocesses.

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

/// Externally-supplied identity used to bind the produced
/// document to a specific implementation.  ``regenerate`` reads
/// this from the repository; tests inject deterministic values.
type TestedIdentity = {
    CommitOid: string
    TreeOid: string
}

/// Contract for an external check runner.  Production injects the
/// ``ProcessRunner``-backed implementation; tests inject a
/// deterministic or failing implementation that records every
/// invocation.
type ExternalCheckRunner =
    string         // check name
        -> string list // argv
        -> string     // working directory
        -> CheckStatus

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

let private testedIdentityFromGit (workingDir: string) : TestedIdentity =
    match runGit [ "rev-parse"; "HEAD" ] workingDir with
    | Result.Ok v -> { CommitOid = v; TreeOid = "" }
    | Result.Error _ -> { CommitOid = ""; TreeOid = "" }
    |> fun partial ->
        match runGit [ "rev-parse"; "HEAD^{tree}" ] workingDir with
        | Result.Ok t -> { partial with TreeOid = t }
        | Result.Error _ -> partial

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

/// Canonical list of external checks.  Each entry records the
/// exact check name and argv that MUST be passed to the injected
/// runner.  The source-policy-tests entry is the canonical
/// invocation of ``make test-source-policy``; the wiring test
/// proves this exact command is issued.
let CanonicalChecks : (string * string list) list =
    [
        "executable-shell-tests",   [ "bash"; "tests/ci/test_build_publish_shell.sh" ]
        "action-pin-mutation-test", [ "bash"; "tests/ci/test_action_pin_mutation.sh" ]
        "source-policy-tests",      [ "make"; "test-source-policy" ]
    ]


let private externalChecks (root: string) (runCheck: ExternalCheckRunner) : CheckStatus list =
    CanonicalChecks
    |> List.map (fun (name, argv) -> runCheck name argv root)

/// Build the canonical gate document.  Pure: no filesystem side
/// effects, no subprocess launch other than those triggered by the
/// injected runner.  Tests call this with a deterministic runner
/// to assert the exact command contract.
let buildDocument
    (root: string)
    (identity: TestedIdentity)
    (runCheck: ExternalCheckRunner)
    : GateSummaryDoc =
    let report = ContainerPolicy.verify root
    let cpCheck = containerPolicyCheck report
    let extChecks = externalChecks root runCheck
    let checks = cpCheck :: extChecks

    let passed       = checks |> List.filter (fun c -> c.Status = "pass") |> List.length
    let skipped      = checks |> List.filter (fun c -> c.Status = "skip") |> List.length
    let unavailable  = checks |> List.filter (fun c -> c.Status = "unavailable") |> List.length
    let failedChecks = checks |> List.filter (fun c -> c.Status = "fail") |> List.length

    let overall =
        if failedChecks > 0 || unavailable > 0 then "fail"
        else if passed = List.length checks then "pass"
        else "unavailable"

    let violationsTotal = report.ViolationsTotal
    let violationsOperational = List.length report.OperationalFailures

    {
        SchemaVersion = 1
        GeneratedAt = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ")
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
        TestedCommitOid = identity.CommitOid
        TestedTreeOid = identity.TreeOid
    }

/// Strict diagnostic returned by ``writeDocument`` when the
/// artefact cannot be written.
type GateWriteFailure =
    | DirectoryCreationFailed of message: string
    | FileWriteFailed of message: string

/// Serialize and write a gate document to ``outputPath``.  The
/// directory is created if absent.  Returns ``Ok`` on success and
/// ``Error`` on failure so callers (including tests) can compose
/// without ``try``/``raise``.
let writeDocument
    (outputPath: string)
    (doc: GateSummaryDoc)
    : Result<unit, GateWriteFailure> =
    try
        let dir = Path.GetDirectoryName outputPath
        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            try Directory.CreateDirectory dir |> ignore
            with ex ->
                Error (DirectoryCreationFailed (sprintf "%s: %s" (ex.GetType().Name) ex.Message))
                |> ignore
        let serialized =
            let opts = JsonSerializerOptions()
            opts.WriteIndented <- true
            opts.DefaultIgnoreCondition <- JsonIgnoreCondition.Never
            JsonSerializer.Serialize(doc, opts)
        File.WriteAllText(outputPath, serialized + "\n")
        Ok ()
    with ex ->
        Error (FileWriteFailed (sprintf "%s: %s" (ex.GetType().Name) ex.Message))

/// Serialize a gate document to a string.  Pure.
let serialize (doc: GateSummaryDoc) : string =
    let opts = JsonSerializerOptions()
    opts.WriteIndented <- true
    opts.DefaultIgnoreCondition <- JsonIgnoreCondition.Never
    JsonSerializer.Serialize(doc, opts)

/// Production runner that launches a real subprocess via the
/// shared ``ProcessRunner``.  Used by ``regenerate`` and by the
/// canonical gate.  Status is derived from the process outcome.
let productionRunner : ExternalCheckRunner =
    fun (name: string) (argv: string list) (workingDir: string) ->
        try
            let result = runProcessText argv (Some workingDir) CancellationToken.None
            let status, exitCode = statusForOutcome result.Outcome
            { Name = name
              Status = status
              ExitCode = exitCode
              Command = String.concat " " argv }
        with _ ->
            { Name = name; Status = "unavailable"; ExitCode = -1; Command = String.concat " " argv }

/// Production entry point.  Reads the implementation identity from
/// git, composes the canonical artefact against the production
/// runner, and writes ``.factory/gate-summary.json``.
let regenerate (root: string) : GateSummaryDoc =
    let identity = testedIdentityFromGit root
    let doc = buildDocument root identity productionRunner
    let target = Path.Combine(root, ".factory", "gate-summary.json")
    match writeDocument target doc with
    | Ok () -> ()
    | Error (DirectoryCreationFailed m) ->
        stderr.WriteLine(sprintf "gate-summary regenerate: FAIL (mkdir %s)" m)
    | Error (FileWriteFailed m) ->
        stderr.WriteLine(sprintf "gate-summary regenerate: FAIL (write %s)" m)
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
