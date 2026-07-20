module Circus.Tooling.SourcePolicy.GateSummary

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
///   checks_failed:  integer (count of *failed checks*, not violations)
///   violations_total: integer (count of individual violations across all checks)
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
open System.Text
open System.Text.Json
open System.Text.Json.Serialization
open System.Runtime.InteropServices
open System.Threading

open Circus.Tooling.SourcePolicy.Inventory
open Circus.Tooling.SourcePolicy.ProcessRunner

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

/// Status for a check given a runner outcome.
let private statusForOutcome (outcome: ProcessOutcome) : string * int =
    match outcome with
    | Exited 0 -> "pass", 0
    | Exited n -> "fail", n
    | NonzeroExit n -> "fail", n
    | SpawnFailure _ -> "unavailable", -1
    | CleanupFailure _ -> "unavailable", -1
    | OutputFailure _ -> "unavailable", -1
    | Cancelled _ -> "unavailable", -1

/// Outcome of a Git invocation.  ``Ok`` carries the trimmed
/// stdout.  ``Error`` carries a non-zero exit code; an exception
/// is treated as exit code ``-1``.  We deliberately never return
/// empty data — the caller must decide what to do when git fails,
/// and that decision must be fail-closed.
let private runGit (args: string list) (workingDir: string) : Result<string, int> =
    let argv = "git" :: args
    let result = runProcessText argv (Some workingDir) CancellationToken.None
    match result.Outcome with
    | Exited 0 -> Result.Ok (result.Output.Trim())
    | Exited code -> Result.Error code
    | NonzeroExit code -> Result.Error code
    | SpawnFailure _ -> Result.Error -1
    | CleanupFailure _ -> Result.Error -1
    | OutputFailure _ -> Result.Error -1
    | Cancelled _ -> Result.Error -1

/// Same shape as ``runGit`` but preserves stdout verbatim (no
/// trimming) so NUL delimiters are not lost.  Reads through the
/// byte-mode runner and decodes the captured bytes as UTF-8 with
/// replacement-fallback so the verifier never throws on non-UTF8
/// content; the verifier consumer (``Inventory.fs``) handles the
/// strict NUL framing separately.
let private runGitNul (args: string list) (workingDir: string) : Result<string, int> =
    let argv = "git" :: args
    let result = runProcessBytes argv (Some workingDir) CancellationToken.None
    match result.Outcome with
    | Exited 0 -> Result.Ok (Encoding.UTF8.GetString(result.Output))
    | Exited code -> Result.Error code
    | NonzeroExit code -> Result.Error code
    | SpawnFailure _ -> Result.Error -1
    | CleanupFailure _ -> Result.Error -1
    | OutputFailure _ -> Result.Error -1
    | Cancelled _ -> Result.Error -1

let private testedTreeOid (workingDir: string) : string =
    match runGit [ "rev-parse"; "HEAD^{tree}" ] workingDir with
    | Result.Ok v -> v
    | Result.Error _ -> ""

/// Split a NUL-delimited Git inventory listing into non-empty
/// relative paths.  ``git ls-files -z`` outputs filenames verbatim
/// and terminates each with NUL.  Used by ``Inventory.fs`` and any
/// other consumer that needs to enumerate tracked files robustly in
/// the presence of unusual path characters.
let splitNulInventory (raw: string) : string list =
    if raw.Length = 0 then []
    else
        raw.Split('\u0000')
        |> Array.filter (fun s -> s <> "")
        |> Array.toList

/// Tracked-file inventory used by container-policy (CP-29).  Uses
/// NUL-delimited ``git ls-files -z`` so file paths with newlines
/// are handled correctly.  Git failure surfaces as ``Error`` —
/// callers must treat that as an operational failure (exit 2) so
/// the secret scan cannot fail open.
let gitTrackedFilesResult (workingDir: string) : Result<string list, int> =
    match runGitNul [ "ls-files"; "-z" ] workingDir with
    | Result.Ok raw -> Result.Ok (splitNulInventory raw)
    | Result.Error code -> Result.Error code

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

/// Extract the deterministic violations count from the
/// container-policy textual output.  Returns 0 when the policy
/// runner reported a pass or could not run.
let private extractViolations (stdout: string) : int =
    let mutable n = 0
    let mutable acc = ""
    let mutable inParen = false
    let mutable seen = false
    for ch in stdout do
        if inParen && ch = ')' then
            inParen <- false
        elif inParen && ch = ',' then
            if seen && acc.Contains "violations=" then
                let tail = acc.Trim().Substring("violations=".Length)
                let digits = tail |> Seq.filter System.Char.IsDigit |> Seq.toArray
                if digits.Length > 0 then
                    n <- int (System.String digits)
            acc <- ""
        elif inParen then
            acc <- acc + string ch
        elif ch = '(' && acc.Contains "FAIL" then
            inParen <- true
            seen <- true
        else
            acc <- acc + string ch
    n

/// Compute the authoritative ``violations_total`` by re-invoking
/// the container-policy verifier and parsing its deterministic
/// textual output.  This guarantees the count is grounded in the
/// actual emission of the canonical checker and never a constant.
let private authoritativeViolations (root: string) (toolingDll: string) : int =
    let argv = [ "dotnet"; toolingDll; "container-policy"; "verify" ]
    let result = runProcessText argv (Some root) CancellationToken.None
    match result.Outcome with
    | Exited 0 -> 0
    | Exited _ ->
        if result.Output.Contains "container-policy verify: FAIL" then
            extractViolations result.Output
        else 0
    | NonzeroExit _ ->
        if result.Output.Contains "container-policy verify: FAIL" then
            extractViolations result.Output
        else 0
    | _ -> 0

/// Execute one canonical check and record its outcome through the
/// governed ``ProcessRunner``.  The runner never throws: a failure
/// to launch the process becomes ``unavailable`` so the producer can
/// distinguish "ran and failed" from "did not run at all".
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

/// Regenerate the artefact against the canonical local gates.
/// ``toolingDll`` is the absolute path of the running ``circus-tooling.dll``
/// so the container-policy check re-invokes the same canonical binary.
///
/// **Violations total** is **derived** from the authoritative
/// container-policy producer: we re-invoke the binary, parse its
/// deterministic textual output, and surface the reported count.
/// The other two checks do not surface a structured violation count,
/// so they contribute ``0`` each — making ``violations_total``
/// grounded in the authoritative emission and never a constant.
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

    let violationsTotal = authoritativeViolations root toolingDll

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

/// Exit-code contract for ``runRegenerate``:
///
///   0 - regenerated successfully AND overall_status=pass
///   1 - regenerated successfully AND overall_status=fail (failed checks)
///   2 - operational error (could not run git, could not write the
///       artefact, etc.).  Failed-check verdicts are never reported
///       as exit 2: they are still operational successes that
///       merely observed policy violations.
let runRegenerate (root: string) : int =
    try
        match runGit [ "rev-parse"; "HEAD^{tree}" ] root with
        | Result.Error code ->
            stderr.WriteLine(sprintf "gate-summary regenerate: FAIL (git rev-parse HEAD^{tree} exit=%d)" code)
            2
        | Result.Ok _ ->
            let toolingDll = resolveToolingDll root
            let doc = regenerate root toolingDll
            stdout.WriteLine(sprintf "gate summary written to .factory/gate-summary.json: %s (%d/%d pass, violations=%d) tree=%s"
                doc.OverallStatus doc.ChecksPassed doc.ChecksTotal doc.ViolationsTotal
                (if doc.TestedTreeOid.Length >= 12 then doc.TestedTreeOid.Substring(0, 12) else doc.TestedTreeOid))
            if doc.OverallStatus = "pass" then 0
            else if doc.OverallStatus = "fail" then 1
            else 2
    with ex ->
        stderr.WriteLine(sprintf "gate-summary regenerate: FAIL (operational: %s: %s)" (ex.GetType().FullName) ex.Message)
        2