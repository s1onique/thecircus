module Circus.Tooling.SourcePolicy.GateSummary

#nowarn "3261"

/// F# port of the deleted `.factory/regenerate_gate_summary.py`.
///
/// Produces ``.factory/gate-summary.json`` using the **exact** Leamas v1
/// wire contract so the targeted-digest consumer
/// (``leamas factory digest``) can parse every check and recognise it
/// as ``pass`` rather than ``unavailable``.
///
/// Wire contract (snake_case, exact field names):
///
///   schema_version: integer (must be 1)
///   generated_at:   ISO-8601 UTC timestamp
///   tool:           "circus-regenerate-gate-summary"
///   overall_status: "pass" | "fail" | "unavailable"
///   checks_total:   integer
///   checks_passed:  integer
///   checks_failed:  integer
///   checks_skipped: integer
///   checks_unavailable: integer
///   checks:         array of CheckStatus
///   tested_tree_oid: 40-char SHA-1 of HEAD^{tree}
///
/// Per-check shape:
///
///   name:      string
///   status:    "pass" | "fail" | "skip" | "unavailable"
///   exit_code: integer
///   command:   string (single space-separated command line)

open System
open System.Diagnostics
open System.IO
open System.Text.Json
open System.Text.Json.Serialization
open System.Runtime.InteropServices

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
    [<JsonPropertyName("checks_skipped")>]
    ChecksSkipped: int
    [<JsonPropertyName("checks_unavailable")>]
    ChecksUnavailable: int
    [<JsonPropertyName("checks")>]
    Checks: CheckStatus list
    [<JsonPropertyName("tested_tree_oid")>]
    TestedTreeOid: string
}

let internal ValidOverallStatuses = set [ "pass"; "fail"; "unavailable" ]
let internal ValidCheckStatuses   = set [ "pass"; "fail"; "skip"; "unavailable" ]

/// Returns the Leamas v1 status for a given exit code.  A zero exit
/// becomes ``pass``; anything else becomes ``fail``.  The
/// ``skip``/``unavailable`` values are reserved for cases where the
/// check could not be executed at all (e.g. the command did not
/// exist); those are produced by the runner below.
let statusForExitCode (exitCode: int) : string =
    if exitCode = 0 then "pass" else "fail"

/// Execute one canonical check and record its outcome.  The runner
/// never throws: a failure to launch the process becomes
/// ``unavailable`` so the producer can distinguish "ran and failed"
/// from "did not run at all".
let private runCheck (name: string) (cmd: string list) (workingDir: string) : CheckStatus =
    try
        if List.isEmpty cmd then
            { Name = name; Status = "unavailable"; ExitCode = -1; Command = "" }
        else
            let psi = ProcessStartInfo()
            psi.FileName <- cmd.[0]
            psi.Arguments <- String.concat " " (List.skip 1 cmd)
            psi.RedirectStandardOutput <- true
            psi.RedirectStandardError <- true
            psi.UseShellExecute <- false
            psi.CreateNoWindow <- true
            psi.WorkingDirectory <- workingDir
            use proc = Process.Start psi
            proc.WaitForExit()
            { Name = name
              Status = statusForExitCode proc.ExitCode
              ExitCode = proc.ExitCode
              Command = String.concat " " cmd }
    with
    | _ ->
        { Name = name
          Status = "unavailable"
          ExitCode = -1
          Command = String.concat " " cmd }

let private runGit (args: string list) (workingDir: string) : string =
    try
        let psi = ProcessStartInfo()
        psi.FileName <- "git"
        psi.Arguments <- String.concat " " args
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        psi.WorkingDirectory <- workingDir
        use proc = Process.Start psi
        proc.WaitForExit()
        if proc.ExitCode = 0 then (proc.StandardOutput.ReadToEnd()).Trim()
        else ""
    with _ -> ""

let private testedTreeOid (workingDir: string) : string =
    runGit [ "rev-parse"; "HEAD^{tree}" ] workingDir

/// The three canonical local gates captured in the summary.  The
/// runner itself invokes them so the producer owns the *single*
/// invocation of each check; downstream consumers (Make, CI, Leamas)
/// only consume the JSON artefact.
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

/// Serialize a GateSummaryDoc to JSON with the canonical Leamas v1
/// snake_case wire names.  We rely on explicit ``JsonPropertyName``
/// attributes (instead of a global naming policy) so the wire format
/// is always exact, regardless of the runtime serializer defaults.
let serialize (doc: GateSummaryDoc) : string =
    let opts = JsonSerializerOptions()
    opts.WriteIndented <- true
    opts.DefaultIgnoreCondition <- JsonIgnoreCondition.Never
    JsonSerializer.Serialize(doc, opts)

/// Regenerate the artefact against the canonical local gates.
/// ``toolingDll`` is the absolute path of the running ``circus-tooling.dll``
/// so the container-policy check re-invokes the same canonical binary.
let regenerate (root: string) (toolingDll: string) : GateSummaryDoc =
    let checks =
        defaultChecks toolingDll
        |> List.map (fun (n, c) -> runCheck n c root)

    let passed      = checks |> List.filter (fun c -> c.Status = "pass")       |> List.length
    let failed      = checks |> List.filter (fun c -> c.Status = "fail")       |> List.length
    let skipped     = checks |> List.filter (fun c -> c.Status = "skip")       |> List.length
    let unavailable = checks |> List.filter (fun c -> c.Status = "unavailable") |> List.length

    let overall =
        if failed > 0 || unavailable > 0 then "fail"
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
        ChecksSkipped = skipped
        ChecksUnavailable = unavailable
        Checks = checks
        TestedTreeOid = testedTreeOid root
    }

    let target = Path.Combine(root, ".factory", "gate-summary.json")
    let dir = Path.GetDirectoryName target
    if not (Directory.Exists dir) then Directory.CreateDirectory dir |> ignore
    let serialized = serialize doc
    File.WriteAllText(target, serialized + "\n")
    doc

/// Resolve the absolute path of the canonical tooling DLL.  Prefers
/// the RID-neutral build at ``net10.0/circus-tooling.dll``; falls
/// back to the RID-specific layout when only a published self-contained
/// binary is present (the latter is reserved for explicit publication
/// targets, not for ordinary development and verification).
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
        let toolingDll = resolveToolingDll root
        let doc = regenerate root toolingDll
        stdout.WriteLine(sprintf "gate summary written to .factory/gate-summary.json: %s (%d/%d pass) tree=%s"
            doc.OverallStatus doc.ChecksPassed doc.ChecksTotal
            (if doc.TestedTreeOid.Length >= 12 then doc.TestedTreeOid.Substring(0, 12) else doc.TestedTreeOid))
        if doc.OverallStatus = "pass" then 0 else 1
    with _ -> 2
