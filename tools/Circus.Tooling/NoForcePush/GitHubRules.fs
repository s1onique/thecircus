module Circus.Tooling.NoForcePush.GitHubRules

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Security.Cryptography
open System.Text
open System.Text.Json
open System.Text.Json.Serialization

// ============================================================================
// GitHub Ruleset API Models
// ============================================================================

/// JSON model for GitHub rulesets API response (list rulesets).
type GhRulesetResponse = {
    [<JsonPropertyName("rulesets")>]
    Rulesets: GhRuleset list
}

/// JSON model for a single ruleset.
and GhRuleset = {
    [<JsonPropertyName("id")>]
    Id: int64
    [<JsonPropertyName("name")>]
    Name: string
    [<JsonPropertyName("source")>]
    Source: string  // "repository" or "organization"
    [<JsonPropertyName(" enforcement")>]
    Enforcement: string  // "enabled", "disabled", "evaluate"
    [<JsonPropertyName("rules")>]
    Rules: GhRule list option
    [<JsonPropertyName("conditions")>]
    Conditions: GhConditions option
}

/// JSON model for a single rule within a ruleset.
and GhRule = {
    [<JsonPropertyName("type")>]
    RuleType: string
    [<JsonPropertyName("parameters")>]
    Parameters: JsonElement option
}

/// JSON model for ruleset conditions.
and GhConditions = {
    [<JsonPropertyName("ref_name")>]
    RefName: GhRefNameCondition option
}

/// JSON model for ref_name conditions.
and GhRefNameCondition = {
    [<JsonPropertyName("include")>]
    Include: string list option
    [<JsonPropertyName("exclude")>]
    Exclude: string list option
}

// ============================================================================
// Bypass actor model
// ============================================================================

/// JSON model for bypass actor.
type GhBypassActor = {
    [<JsonPropertyName("actor_type")>]
    ActorType: string
    [<JsonPropertyName("actor_id")>]
    ActorId: int64
    [<JsonPropertyName("bypass_mode")>]
    BypassMode: string  // "always", "pull_request", "exempt"
}

// ============================================================================
// Result model
// ============================================================================

/// Result of GitHub ruleset verification.
type RulesetResult =
    | RulesVerified of sourceRepo: string * branch: string * sha256: string * details: string
    | RulesMissingNonFastForwardRule of sourceRepo: string * branch: string
    | RulesMissingDeletionRule of sourceRepo: string * branch: string
    | RulesetEvaluateOnly of sourceRepo: string * branch: string
    | RulesetDisabled of sourceRepo: string * branch: string
    | RulesetNotFound of sourceRepo: string * branch: string
    | RulesHasBypassActor of sourceRepo: string * branch: string * actorType: string * bypassMode: string
    | RepositoryMismatch of expected: string * actual: string
    | BranchMismatch of expected: string * actual: string
    | MalformedJson of detail: string
    | NetworkFailure of detail: string
    | AuthFailure of detail: string
    | BypassEvidenceIncomplete of detail: string
    | UnknownError of detail: string

// ============================================================================
// Evidence binding
// ============================================================================

/// Compute SHA256 of normalized JSON response for evidence binding.
let computeEvidenceHash (json: string) : string =
    use sha = SHA256.Create()
    let bytes = Encoding.UTF8.GetBytes(json)
    let hash = sha.ComputeHash(bytes)
    BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant()

/// Normalize JSON before hashing (canonical form for evidence).
let normalizeJsonForEvidence (json: string) : string =
    try
        let doc = JsonDocument.Parse(json)
        use stream = new MemoryStream()
        use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = false))
        doc.WriteTo(writer)
        writer.Flush()
        Encoding.UTF8.GetString(stream.ToArray())
    with _ ->
        // If normalization fails, return original
        json

// ============================================================================
// GitHub API seam (injectable)
// ============================================================================

/// Seam for executing GitHub CLI commands.
type GhApiSeam =
    abstract member GetRulesets: repositoryId: string -> Result<string, string>
    abstract member GetRulesetByName: repositoryId: string -> rulesetName: string -> Result<string, string>
    abstract member GetRulesetBypassActors: repositoryId: string -> rulesetId: int64 -> Result<string, string>

/// Default implementation using gh CLI.
type DefaultGhApiSeam () =
    interface GhApiSeam with
        member this.GetRulesets repositoryId =
            try
                let psi = ProcessStartInfo()
                psi.FileName <- "gh"
                psi.Arguments <- sprintf "api repos/%s/rulesets" repositoryId
                psi.RedirectStandardOutput <- true
                psi.RedirectStandardError <- true
                psi.UseShellExecute <- false
                psi.CreateNoWindow <- true
                psi.Set_RedirectStandardInput(false) |> ignore
                psi.Timeout <- 30000 // 30 second timeout

                use proc = Process.Start(psi)
                let output = proc.StandardOutput.ReadToEnd()
                let stderr = proc.StandardError.ReadToEnd()
                proc.WaitForExit()

                if proc.ExitCode <> 0 then
                    if stderr.Contains("404") then
                        Error(sprintf "repository '%s' not found" repositoryId)
                    elif stderr.Contains("authentication") || stderr.Contains("GITHUB_TOKEN") then
                        Error(sprintf "authentication failed: %s" stderr)
                    else
                        Error(sprintf "gh api failed (exit %d): %s" proc.ExitCode stderr)
                else
                    Ok output
            with ex ->
                Error(sprintf "failed to query rulesets: %s" ex.Message)

        member this.GetRulesetByName repositoryId rulesetName =
            try
                // Query all rulesets and filter by name
                match this.GetRulesets repositoryId with
                | Ok json ->
                    let doc = JsonDocument.Parse(json)
                    let rulesets = doc.RootElement.GetProperty("rulesets")
                    let mutable found = None
                    for ruleset in rulesets.EnumerateArray() do
                        let name = ruleset.GetProperty("name").GetString()
                        if name = rulesetName then
                            found <- Some ruleset
                    match found with
                    | Some r ->
                        use stream = new MemoryStream()
                        use writer = new Utf8JsonWriter(stream)
                        r.WriteTo(writer)
                        writer.Flush()
                        Ok(Encoding.UTF8.GetString(stream.ToArray()))
                    | None ->
                        Error(sprintf "ruleset '%s' not found" rulesetName)
                | Error e -> Error e
            with ex ->
                Error(sprintf "failed to get ruleset: %s" ex.Message)

        member this.GetRulesetBypassActors repositoryId rulesetId =
            try
                let psi = ProcessStartInfo()
                psi.FileName <- "gh"
                psi.Arguments <- sprintf "api repos/%s/rulesets/%d/bypass" repositoryId rulesetId
                psi.RedirectStandardOutput <- true
                psi.RedirectStandardError <- true
                psi.UseShellExecute <- false
                psi.CreateNoWindow <- true
                psi.Set_RedirectStandardInput(false) |> ignore
                psi.Timeout <- 30000

                use proc = Process.Start(psi)
                let output = proc.StandardOutput.ReadToEnd()
                let stderr = proc.StandardError.ReadToEnd()
                proc.WaitForExit()

                if proc.ExitCode <> 0 then
                    if stderr.Contains("404") then
                        Error("bypass information not available (may require admin)")
                    elif stderr.Contains("authentication") || stderr.Contains("GITHUB_TOKEN") then
                        Error(sprintf "authentication failed: %s" stderr)
                    else
                        // Bypass info may not be available - fail closed
                        Error(sprintf "bypass evidence unavailable: %s" stderr)
                else
                    Ok output
            with ex ->
                Error(sprintf "failed to get bypass actors: %s" ex.Message)

let defaultGhApiSeam = DefaultGhApiSeam() :> GhApiSeam

// ============================================================================
// Ruleset analysis
// ============================================================================

/// Check if a ruleset applies to a given branch.
let rulesetAppliesToBranch (ruleset: GhRuleset) (branch: string) : bool =
    match ruleset.Conditions with
    | Some c ->
        match c.RefName with
        | Some rn ->
            // Check if branch matches include patterns
            match rn.Include with
            | Some patterns ->
                patterns
                |> List.exists (fun pattern ->
                    // Simple pattern matching (supports * wildcards)
                    let regex = System.Text.RegularExpressions.Regex(
                        "^" + System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*") + "$")
                    regex.IsMatch(branch))
            | None -> true // No include means all refs
        | None -> true
    | None -> true

/// Check if a ruleset has non_fast_forward rule blocked.
let rulesetBlocksNonFastForward (ruleset: GhRuleset) : bool =
    match ruleset.Rules with
    | Some rules ->
        rules
        |> Seq.exists (fun r -> r.RuleType = "non_fast_forward")
    | None -> false

/// Check if a ruleset blocks deletion.
let rulesetBlocksDeletion (ruleset: GhRuleset) : bool =
    match ruleset.Rules with
    | Some rules ->
        rules
        |> Seq.exists (fun r -> r.RuleType = "delete_ref")
    | None -> false

/// Parse bypass actors and check for critical bypasses.
let parseBypassActors (json: string) : Result<GhBypassActor list, string> =
    try
        let opts = JsonSerializerOptions()
        opts.PropertyNameCaseInsensitive <- true
        let actors = JsonSerializer.Deserialize<GhBypassActor list>(json, opts)
        match actors with
        | null -> Ok []
        | _ -> Ok (List.ofArray actors)
    with ex ->
        Error(sprintf "failed to parse bypass actors: %s" ex.Message)

/// Check for critical bypass actors.
let hasCriticalBypass (actors: GhBypassActor list) : (string * string) option =
    actors
    |> List.tryFind (fun a ->
        a.BypassMode = "always" ||
        (a.ActorType = "RepositoryRole" && a.BypassMode <> "exempt"))

// ============================================================================
// Main verification
// ============================================================================

/// Verify GitHub rulesets for a repository and branch.
let verifyRulesets
    (repositoryId: string)
    (branch: string)
    (seam: GhApiSeam option)
    : RulesetResult =

    let gh = defaultArg seam defaultGhApiSeam

    // Step 1: Query active rulesets
    match gh.GetRulesets repositoryId with
    | Error e when e.Contains("not found") ->
        RulesetNotFound(repositoryId, branch)
    | Error e when e.Contains("authentication") ->
        AuthFailure(e)
    | Error e ->
        NetworkFailure(e)
    | Ok json ->
        try
            let opts = JsonSerializerOptions()
            opts.PropertyNameCaseInsensitive <- true
            let response = JsonSerializer.Deserialize<GhRulesetResponse>(json, opts)

            // Find rulesets that apply to this branch
            let applicableRulesets =
                response.Rulesets
                |> Array.filter (fun rs ->
                    rs.Source = "repository" &&
                    rulesetAppliesToBranch rs branch)

            if Array.isEmpty applicableRulesets then
                RulesNotFound(repositoryId, branch)

            else
                // Check enforcement status
                let hasEnabled =
                    applicableRulesets
                    |> Array.exists (fun rs -> rs.Enforcement = "enabled")

                if not hasEnabled then
                    // Check if any are in evaluate mode
                    let hasEvaluate =
                        applicableRulesets
                        |> Array.exists (fun rs -> rs.Enforcement = "evaluate")

                    if hasEvaluate then
                        RulesetEvaluateOnly(repositoryId, branch)
                    else
                        RulesetDisabled(repositoryId, branch)

                else
                    // Check for non_fast_forward rule
                    let blocksNff =
                        applicableRulesets
                        |> Array.filter (fun rs -> rs.Enforcement = "enabled")
                        |> Array.exists rulesetBlocksNonFastForward

                    if not blocksNff then
                        RulesMissingNonFastForwardRule(repositoryId, branch)

                    else
                        // Check for deletion rule
                        let blocksDeletion =
                            applicableRulesets
                            |> Array.filter (fun rs -> rs.Enforcement = "enabled")
                            |> Array.exists rulesetBlocksDeletion

                        if not blocksDeletion then
                            RulesMissingDeletionRule(repositoryId, branch)

                        else
                            // Check bypass actors - fail if evidence is incomplete
                            let enabledRulesets =
                                applicableRulesets
                                |> Array.filter (fun rs -> rs.Enforcement = "enabled")

                            let bypassResults =
                                enabledRulesets
                                |> Array.map (fun rs ->
                                    gh.GetRulesetBypassActors(repositoryId, rs.Id))

                            let criticalBypasses =
                                bypassResults
                                |> Array.choose (function
                                    | Ok json ->
                                        match parseBypassActors json with
                                        | Ok actors ->
                                            hasCriticalBypass actors
                                            |> Option.map (fun (at, bm) -> (at, bm))
                                        | Error _ -> None
                                    | Error e when e.Contains("unavailable") ->
                                        // Evidence incomplete - fail closed
                                        None // Will trigger BypassEvidenceIncomplete
                                    | Error _ -> None)
                                |> Array.toList

                            if not (List.isEmpty criticalBypasses) then
                                let (at, bm) = List.head criticalBypasses
                                RulesHasBypassActor(repositoryId, branch, at, bm)

                            else if bypassResults |> Array.exists (function
                                    | Error e when e.Contains("unavailable") -> true
                                    | _ -> false) then
                                // Some bypass evidence was unavailable - fail closed
                                BypassEvidenceIncomplete(
                                    sprintf "bypass evidence incomplete for %s:%s" repositoryId branch)

                            else
                                // All checks pass - bind evidence
                                let normalizedJson = normalizeJsonForEvidence json
                                let evidenceHash = computeEvidenceHash normalizedJson
                                let details = sprintf "rulesets=%d enforcement=enabled blocks_nff=%b blocks_deletion=%b"
                                    applicableRulesets.Length blocksNff blocksDeletion
                                RulesVerified(repositoryId, branch, evidenceHash, details)

        with ex ->
            MalformedJson(sprintf "parse error: %s" ex.Message)

// ============================================================================
// CLI entry point
// ============================================================================

/// Run the GitHub ruleset verification and return exit code.
let runVerify (repositoryId: string) (branch: string) (seam: GhApiSeam option) : int =
    match verifyRulesets repositoryId branch seam with
    | RulesVerified(repo, br, hash, details) ->
        stdout.WriteLine(sprintf "GitHub rulesets verified for %s:%s" repo br)
        stdout.WriteLine(sprintf "  details: %s" details)
        stdout.WriteLine(sprintf "  evidence_sha256: %s" hash)
        0

    | RulesMissingNonFastForwardRule(repo, br) ->
        stderr.WriteLine(sprintf "FAIL: rulesets missing non_fast_forward rule for %s:%s" repo br)
        1

    | RulesMissingDeletionRule(repo, br) ->
        stderr.WriteLine(sprintf "FAIL: rulesets missing deletion rule for %s:%s" repo br)
        1

    | RulesetEvaluateOnly(repo, br) ->
        stderr.WriteLine(sprintf "FAIL: rulesets in 'evaluate' mode only for %s:%s (not enforced)" repo br)
        1

    | RulesetDisabled(repo, br) ->
        stderr.WriteLine(sprintf "FAIL: rulesets disabled for %s:%s" repo br)
        1

    | RulesetNotFound(repo, br) ->
        stderr.WriteLine(sprintf "FAIL: no rulesets found for %s:%s" repo br)
        1

    | RulesHasBypassActor(repo, br, actorType, bypassMode) ->
        stderr.WriteLine(sprintf "FAIL: rulesets have critical bypass actor (type=%s, mode=%s) for %s:%s" actorType bypassMode repo br)
        1

    | RepositoryMismatch(expected, actual) ->
        stderr.WriteLine(sprintf "FAIL: repository mismatch (expected=%s, actual=%s)" expected actual)
        2

    | BranchMismatch(expected, actual) ->
        stderr.WriteLine(sprintf "FAIL: branch mismatch (expected=%s, actual=%s)" expected actual)
        2

    | MalformedJson detail ->
        stderr.WriteLine(sprintf "FAIL: malformed GitHub response: %s" detail)
        2

    | NetworkFailure detail ->
        stderr.WriteLine(sprintf "FAIL: network failure: %s" detail)
        2

    | AuthFailure detail ->
        stderr.WriteLine(sprintf "FAIL: authentication failure: %s" detail)
        2

    | BypassEvidenceIncomplete detail ->
        stderr.WriteLine(sprintf "FAIL: bypass evidence incomplete: %s" detail)
        2

    | UnknownError detail ->
        stderr.WriteLine(sprintf "FAIL: unknown error: %s" detail)
        2
