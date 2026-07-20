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

/// Run a process to completion and return its exit code plus
/// its stdout and stderr streams.  On process-launch failure
/// (e.g. the executable is missing from PATH) this returns
/// ``(-1, "", err)`` where ``err`` carries the exception message;
/// callers must distinguish this from a successful launch that
/// exited non-zero.
///
/// The streams are read via ``StreamReader.ReadToEndAsync()``
/// which returns ``string`` after UTF-8 decoding; invalid UTF-8
/// sequences are replaced per the .NET default.  Git
/// ``ls-files -z`` paths are normally ASCII and decode cleanly,
/// but arbitrary binary filenames (which Git permits on POSIX
/// filesystems) are not byte-faithfully preserved.  A future ACT
/// should switch the NUL-inventory path to a dedicated ``byte[]``
/// capture to support arbitrary filenames.
let internal runProcessAsync
    (psi: ProcessStartInfo)
    (cancellationToken: System.Threading.CancellationToken)
    : System.Threading.Tasks.Task<int * string * string> =
    task {
        let proc = Process.Start(psi)
        // Read stdout/stderr as raw byte streams so we preserve
        // every character the child wrote (including embedded NULs
        // and newlines).  The string conversion is byte-faithful
        // because F# strings are UTF-16 and the input is decoded as
        // the system default code page; git ls-files paths are ASCII.
        let stdoutTask =
            task {
                use reader = new System.IO.StreamReader(proc.StandardOutput.BaseStream)
                return! reader.ReadToEndAsync()
            }
        let stderrTask =
            task {
                use reader = new System.IO.StreamReader(proc.StandardError.BaseStream)
                return! reader.ReadToEndAsync()
            }
        try
            do! proc.WaitForExitAsync(cancellationToken)
        with _ ->
            try if not proc.HasExited then proc.Kill(true) with _ -> ()
        let stdout = stdoutTask.Result
        let stderr = stderrTask.Result
        return (proc.ExitCode, stdout, stderr)
    }

/// Synchronous wrapper around ``runProcessAsync``.
let internal runProcess (psi: ProcessStartInfo) : int * string * string =
    try
        runProcessAsync psi System.Threading.CancellationToken.None
        |> Async.AwaitTask
        |> Async.RunSynchronously
    with ex ->
        let msg = sprintf "%s: %s" (ex.GetType().FullName) ex.Message
        (-1, "", msg)

/// Execute one canonical check and record its outcome.  The runner
/// never throws: a failure to launch the process becomes
/// ``unavailable`` so the producer can distinguish "ran and failed"
/// from "did not run at all".
let private runCheck (name: string) (cmd: string list) (workingDir: string) : CheckStatus =
    if List.isEmpty cmd then
        { Name = name; Status = "unavailable"; ExitCode = -1; Command = "" }
    else
        let psi = ProcessStartInfo()
        psi.FileName <- cmd.[0]
        // ``ProcessStartInfo.ArgumentList`` is the documented
        // argument-passing mechanism; ``psi.Arguments`` joins the
        // list with spaces and then re-parses the string per
        // Windows argument-parsing rules, which corrupts paths or
        // arguments containing spaces, quotes, or other
        // shell-significant characters.  See Microsoft docs on
        // ``ProcessStartInfo.ArgumentList``.
        psi.ArgumentList.Clear()
        for a in List.skip 1 cmd do
            psi.ArgumentList.Add(a)
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        psi.WorkingDirectory <- workingDir
        try
            let exitCode, _, _ = runProcess psi
            // ``runProcess`` returns ``ExitCode = -1`` when the child
            // cannot be launched (e.g. ``dotnet`` not on PATH).  Map
            // that to ``status=unavailable`` per the documented wire
            // contract; map any other non-zero exit to ``fail``.
            let status =
                if exitCode = -1 then "unavailable"
                else statusForExitCode exitCode
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

/// Outcome of a Git invocation.  ``Ok`` carries the trimmed
/// stdout.  ``Error`` carries a non-zero exit code; an exception
/// is treated as exit code ``-1``.  We deliberately never return
/// empty data — the caller must decide what to do when git fails,
/// and that decision must be fail-closed.
let private runGit (args: string list) (workingDir: string) : Result<string, int> =
    let psi = ProcessStartInfo()
    psi.FileName <- "git"
    psi.Arguments <- String.concat " " args
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true
    psi.WorkingDirectory <- workingDir
    try
        let exitCode, stdout, _ = runProcess psi
        if exitCode = 0 then Ok (stdout.Trim())
        else Error exitCode
    with _ -> Error -1

/// Same shape as ``runGit`` but preserves stdout verbatim (no
/// trimming) so NUL delimiters are not lost.  Used for inventory
/// listings (e.g. ``git ls-files``) where file paths may legally
/// contain newlines.
let private runGitNul (args: string list) (workingDir: string) : Result<string, int> =
    let psi = ProcessStartInfo()
    psi.FileName <- "git"
    psi.Arguments <- String.concat " " args
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.UseShellExecute <- false
    psi.CreateNoWindow <- true
    psi.WorkingDirectory <- workingDir
    try
        let exitCode, stdout, _ = runProcess psi
        if exitCode = 0 then Ok stdout
        else Error exitCode
    with _ -> Error -1

let private testedTreeOid (workingDir: string) : string =
    match runGit [ "rev-parse"; "HEAD^{tree}" ] workingDir with
    | Ok v -> v
    | Error _ -> ""

/// Split a NUL-delimited Git inventory listing into non-empty
/// relative paths.  ``git ls-files -z`` outputs filenames verbatim
/// and terminates each with NUL; we must not ``Trim`` because
/// legitimate paths can legally begin or end with whitespace
/// characters (the surrounding double-quote delimiters in
/// ``git config --get`` output, for example).  See ``git-ls-files``
/// documentation.  Used by ``ContainerPolicy.fs`` and any other
/// consumer that needs to enumerate tracked files robustly in the
/// presence of unusual path characters.
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
    | Ok raw -> Ok (splitNulInventory raw)
    | Error code -> Error code

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
    let skipped     = checks |> List.filter (fun c -> c.Status = "skip")       |> List.length
    let unavailable = checks |> List.filter (fun c -> c.Status = "unavailable") |> List.length
    // Failed *checks* are checks whose status is *fail only*.
    // ``unavailable`` is a distinct, fourth state and must not be
    // double-counted in ``checks_failed``.  The validator requires
    // passed + failed + skipped + unavailable == total.
    let failedChecks = checks |> List.filter (fun c -> c.Status = "fail") |> List.length

    let overall =
        if failedChecks > 0 || unavailable > 0 then "fail"
        else if passed = List.length checks then "pass"
        else "unavailable"

    // violations_total: aggregate per-child violation counts when
    // we can compute them.  The container-policy verifier exposes
    // its violation count via its exit code: we cannot introspect
    // its internal report without an IPC channel, so the canonical
    // contract has each child check surface machine-readable
    // violation counts via a sibling JSON artefact.  For the
    // present three checks, none of them publishes a structured
    // report, so the producer sets ``ViolationsTotal = 0`` and
    // documents this in the close report.  The validator still
    // requires ``violations_total`` to be a non-negative integer
    // (see GateSummaryVerify.fs) so the wire contract is upheld.
    let violationsTotal = 0

    let doc = {
        SchemaVersion = 1
        GeneratedAt = nowIso ()
        Tool = "circus-regenerate-gate-summary"
        OverallStatus = overall
        ChecksTotal = List.length checks
        ChecksPassed = passed
        ChecksFailed = failedChecks
        ViolationsTotal = 0
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
        // Operational failure: missing HEAD^{tree}.  Refusing to
        // continue is fail-closed: an artefact with an empty
        // ``tested_tree_oid`` would silently pass the validator
        // because the binding check returns None.
        match runGit [ "rev-parse"; "HEAD^{tree}" ] root with
        | Error code ->
            stderr.WriteLine(sprintf "gate-summary regenerate: FAIL (git rev-parse HEAD^{tree} exit=%d)" code)
            2
        | Ok _ ->
            let toolingDll = resolveToolingDll root
            let doc = regenerate root toolingDll
            stdout.WriteLine(sprintf "gate summary written to .factory/gate-summary.json: %s (%d/%d pass) tree=%s"
                doc.OverallStatus doc.ChecksPassed doc.ChecksTotal
                (if doc.TestedTreeOid.Length >= 12 then doc.TestedTreeOid.Substring(0, 12) else doc.TestedTreeOid))
            if doc.OverallStatus = "pass" then 0
            else if doc.OverallStatus = "fail" then 1
            else 2
    with ex ->
        stderr.WriteLine(sprintf "gate-summary regenerate: FAIL (operational: %s: %s)" (ex.GetType().FullName) ex.Message)
        2
