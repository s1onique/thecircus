module Circus.Tooling.SourcePolicy.GateSummaryVerify

/// Structural validator for ``.factory/gate-summary.json``.
///
/// The producer (``GateSummary.regenerate``) writes a JSON document
/// that the Leamas v1 targeted-digest consumer parses as
/// ``source_status=present``, ``schema_version=1`` and every check
/// reported as ``pass`` / ``fail`` / ``skip`` / ``unavailable``.  The
/// validator here is the consumer-side guard: it parses the document
/// with the **same field names** the real consumer uses, and rejects
/// the artefact if any required field is missing, mistyped, or
/// carrying a non-canonical status.
///
/// Exit codes follow the Leamas contract:
///
///   0 - structurally valid AND every required check passes
///   1 - structurally valid AND at least one required check failed
///   2 - malformed JSON, missing required field, or schema mismatch

open System
open System.IO
open System.Text.Json
open System.Text.RegularExpressions

open Circus.Tooling.SourcePolicy.GateSummary

let private requiredTopLevelFields =
    [
        "schema_version"
        "generated_at"
        "tool"
        "overall_status"
        "checks_total"
        "checks_passed"
        "checks_failed"
        "checks_skipped"
        "checks_unavailable"
        "checks"
        "tested_tree_oid"
    ]

let private requiredCheckFields =
    [ "name"; "status"; "exit_code"; "command" ]

let private validOverallStatuses =
    set [ "pass"; "fail"; "unavailable" ]

let private validCheckStatuses =
    set [ "pass"; "fail"; "skip"; "unavailable" ]

let private treeOidPattern =
    Regex("^[0-9a-f]{40}$", RegexOptions.Compiled)

/// Outcome of validating a single document.  Carries both the
/// machine-readable verdict (for the gate runner) and the human-readable
/// summary (for the operator).
type VerifyResult = {
    Path: string
    SchemaVersion: int
    OverallStatus: string
    ChecksTotal: int
    ChecksPassed: int
    ChecksFailed: int
    ChecksSkipped: int
    ChecksUnavailable: int
    Checks: CheckStatus list
    TestedTreeOid: string
    FailureReasons: string list
}

let private parseJson (raw: string) : JsonDocument option =
    let mutable opts = JsonDocumentOptions()
    opts.AllowTrailingCommas <- false
    opts.CommentHandling <- JsonCommentHandling.Disallow
    opts.MaxDepth <- 64
    try JsonDocument.Parse(raw, opts) |> Some
    with _ -> None

let private readInt (root: JsonElement) (name: string) (failures: ResizeArray<string>) : int option =
    if root.ValueKind <> JsonValueKind.Object then
        None
    else
        let mutable found = Unchecked.defaultof<JsonElement>
        if root.TryGetProperty(name, &found) then
            if found.ValueKind = JsonValueKind.Number then
                try
                    Some(found.GetInt32())
                with _ ->
                    failures.Add(sprintf "field %s must be a finite integer" name)
                    None
            else
                failures.Add(sprintf "field %s must be an integer, got %s" name (found.ValueKind.ToString()))
                None
        else
            failures.Add(sprintf "missing required field: %s" name)
            None

let private readString (root: JsonElement) (name: string) (failures: ResizeArray<string>) : string option =
    if root.ValueKind <> JsonValueKind.Object then
        None
    else
        let mutable found = Unchecked.defaultof<JsonElement>
        if root.TryGetProperty(name, &found) then
            if found.ValueKind = JsonValueKind.String then Some(found.GetString())
            else
                failures.Add(sprintf "field %s must be a string, got %s" name (found.ValueKind.ToString()))
                None
        else
            failures.Add(sprintf "missing required field: %s" name)
            None

let private readArray (root: JsonElement) (name: string) (failures: ResizeArray<string>) : JsonElement option =
    if root.ValueKind <> JsonValueKind.Object then None
    else
        let mutable found = Unchecked.defaultof<JsonElement>
        if root.TryGetProperty(name, &found) then
            if found.ValueKind = JsonValueKind.Array then Some found
            else
                failures.Add(sprintf "field %s must be an array, got %s" name (found.ValueKind.ToString()))
                None
        else
            failures.Add(sprintf "missing required field: %s" name)
            None

let private hasProperty (obj: JsonElement) (name: string) : bool =
    if obj.ValueKind <> JsonValueKind.Object then false
    else
        let mutable found = Unchecked.defaultof<JsonElement>
        obj.TryGetProperty(name, &found)

let private parseCheck (el: JsonElement) : Result<CheckStatus, string> =
    if el.ValueKind <> JsonValueKind.Object then
        Error "checks[i] must be a JSON object"
    else
        let failures = ResizeArray<string>()
        for f in requiredCheckFields do
            if not (hasProperty el f) then
                failures.Add(sprintf "check is missing required field: %s" f)

        let name = readString el "name" failures
        let status = readString el "status" failures
        let exitCode = readInt el "exit_code" failures
        let cmd = readString el "command" failures

        if failures.Count > 0 then
            Error(String.concat "; " failures)
        else
            match name, status, exitCode, cmd with
            | Some n, Some s, Some x, Some c ->
                if not (Set.contains s validCheckStatuses) then
                    Error(sprintf "checks[i].status must be one of [pass, fail, skip, unavailable], got %s" s)
                else if x <> 0 && s = "pass" then
                    Error(sprintf "checks[i] has exit_code=%d but status=pass (must be fail or unavailable)" x)
                else if x = 0 && s = "fail" then
                    Error(sprintf "checks[i] has exit_code=%d but status=fail (must be pass or skip)" x)
                else if String.IsNullOrWhiteSpace n then
                    Error "checks[i].name must be non-empty"
                else
                    Ok { Name = n; Status = s; ExitCode = x; Command = c }
            | _ -> Error "internal: failed to collect check fields"

/// Parse and validate a gate-summary document.
let validate (path: string) : Result<VerifyResult, string> =
    if not (File.Exists path) then
        Error(sprintf "gate-summary not found at %s" path)
    else
        let raw = File.ReadAllText path
        match parseJson raw with
        | None -> Error "gate-summary.json is not valid JSON"
        | Some d ->
            let root = d.RootElement
            let failures = ResizeArray<string>()
            if root.ValueKind <> JsonValueKind.Object then
                failures.Add("root must be a JSON object")
            else
                // Reject the PascalCase twin names outright.
                for forbidden in [ "SchemaVersion"; "OverallStatus"; "ChecksTotal"; "ChecksPassed"; "ChecksFailed"; "ChecksSkipped"; "ChecksUnavailable"; "TestedTreeOid"; "GeneratedAt"; "Checks" ] do
                    if hasProperty root forbidden then
                        failures.Add(sprintf "root must not carry PascalCase field: %s (use snake_case)" forbidden)

                for required in requiredTopLevelFields do
                    if not (hasProperty root required) then
                        failures.Add(sprintf "missing required field: %s" required)

            let schemaV = readInt root "schema_version" failures
            let overall = readString root "overall_status" failures
            let total = readInt root "checks_total" failures
            let passed = readInt root "checks_passed" failures
            let failed = readInt root "checks_failed" failures
            let skipped = readInt root "checks_skipped" failures
            let unavail = readInt root "checks_unavailable" failures
            let oid = readString root "tested_tree_oid" failures
            let checksArr = readArray root "checks" failures

            // Field-level validations
            (match schemaV with
             | Some v when v <> 1 -> failures.Add(sprintf "schema_version must be 1, got %d" v)
             | _ -> ())

            (match overall with
             | Some v when not (Set.contains v validOverallStatuses) ->
                 failures.Add(sprintf "overall_status must be one of [pass, fail, unavailable], got %s" v)
             | _ -> ())

            (match oid with
             | Some v when not (treeOidPattern.IsMatch v) ->
                 failures.Add(sprintf "tested_tree_oid must be a 40-character lowercase SHA-1, got %s" v)
             | _ -> ())

            // Check-array parsing
            let parsedChecks = ResizeArray<CheckStatus>()
            (match checksArr with
             | Some v ->
                 if v.GetArrayLength() = 0 then
                     failures.Add "checks must be a non-empty array"
                 else
                     for item in v.EnumerateArray() do
                         match parseCheck item with
                         | Ok c -> parsedChecks.Add c
                         | Error e -> failures.Add(sprintf "checks[i]: %s" e)
             | _ -> ())

            // Count consistency
            (match total, passed, failed, skipped, unavail with
             | Some total, Some p, Some f, Some s, Some u
                 when (p + f + s + u) <> total ->
                 failures.Add(sprintf "count inconsistency: total=%d, sum(passed+failed+skipped+unavailable)=%d"
                    total (p + f + s + u))
             | _ -> ())

            (match total, checksArr with
             | Some total, Some v when v.GetArrayLength() <> total ->
                 failures.Add(sprintf "checks_total=%d does not match checks array length=%d"
                    total (v.GetArrayLength()))
             | _ -> ())

            (match overall, failed, unavail with
             | Some "pass", Some f, _ when f > 0 ->
                 failures.Add "overall_status=pass contradicts failed > 0"
             | Some "pass", _, Some u when u > 0 ->
                 failures.Add "overall_status=pass contradicts unavailable > 0"
             | _ -> ())

            let r = {
                Path = path
                SchemaVersion = (match schemaV with Some v -> v | _ -> -1)
                OverallStatus = (match overall with Some v -> v | _ -> "")
                ChecksTotal = (match total with Some v -> v | _ -> -1)
                ChecksPassed = (match passed with Some v -> v | _ -> -1)
                ChecksFailed = (match failed with Some v -> v | _ -> -1)
                ChecksSkipped = (match skipped with Some v -> v | _ -> -1)
                ChecksUnavailable = (match unavail with Some v -> v | _ -> -1)
                Checks = parsedChecks |> Seq.toList
                TestedTreeOid = (match oid with Some v -> v | _ -> "")
                FailureReasons = failures |> Seq.toList
            }

            if failures.Count > 0 then
                Error(String.concat "; " failures)
            else
                Ok r

/// Run ``git rev-parse HEAD^{tree}`` in the given repository root and
/// return the trimmed OID.  ``None`` is returned when the command
/// fails or git is missing.
let readExpectedTreeOid (repoRoot: string) : string option =
    try
        let psi = System.Diagnostics.ProcessStartInfo()
        psi.FileName <- "git"
        psi.Arguments <- "rev-parse HEAD^{tree}"
        psi.RedirectStandardOutput <- true
        psi.RedirectStandardError <- true
        psi.UseShellExecute <- false
        psi.CreateNoWindow <- true
        psi.WorkingDirectory <- repoRoot
        let mutable proc : System.Diagnostics.Process = Unchecked.defaultof<System.Diagnostics.Process>
        proc <- System.Diagnostics.Process.Start psi
        proc.WaitForExit()
        let result = if proc.ExitCode = 0 then Some((proc.StandardOutput.ReadToEnd()).Trim()) else None
        proc.Dispose()
        result
    with _ -> None

/// Compare the ``tested_tree_oid`` field against the actual
/// ``HEAD^{tree}`` of the repository.  Used to prove the artefact
/// was regenerated against the committed tree.
let validateTreeBinding (result: VerifyResult) (repoRoot: string) : Result<unit, string> =
    match readExpectedTreeOid repoRoot with
    | None -> Error "git rev-parse HEAD^{tree} failed"
    | Some expected ->
        if expected <> result.TestedTreeOid then
            Error(sprintf "tested_tree_oid %s does not match HEAD^{tree} %s" result.TestedTreeOid expected)
        else
            Ok ()

/// Exit code for the verifier based on the structural verdict.
/// 0 - structurally valid AND all required checks pass
/// 1 - structurally valid AND at least one required check failed
/// 2 - malformed / contract-incompatible summary
let runVerify (path: string) (expectedOid: string option) : int =
    match validate path with
    | Error reasons ->
        stderr.WriteLine "gate-summary verify: FAIL (contract)"
        stderr.WriteLine(sprintf "  reasons: %s" reasons)
        2
    | Ok r ->
        let bindingMessage =
            match expectedOid with
            | Some expected when expected <> r.TestedTreeOid ->
                Some(sprintf "tested_tree_oid %s does not match expected %s" r.TestedTreeOid expected)
            | Some _ -> None
            | None -> None

        match bindingMessage with
        | Some msg ->
            stderr.WriteLine "gate-summary verify: FAIL (binding)"
            stderr.WriteLine(sprintf "  %s" msg)
            2
        | None ->
            let failedChecks = r.Checks |> List.filter (fun c -> c.Status = "fail" || c.Status = "unavailable")
            if failedChecks.Length > 0 then
                stdout.WriteLine(sprintf "gate-summary verify: FAIL (checks=%d, failed=%d)"
                    r.ChecksTotal failedChecks.Length)
                for c in failedChecks do
                    stdout.WriteLine(sprintf "  - %s: %s (exit=%d)" c.Name c.Status c.ExitCode)
                1
            else
                let prefix = if r.TestedTreeOid.Length >= 12 then r.TestedTreeOid.Substring(0, 12) else r.TestedTreeOid
                stdout.WriteLine(sprintf "gate-summary verify: PASS (checks=%d, schema_version=%d, tree=%s)"
                    r.ChecksTotal r.SchemaVersion prefix)
                0
