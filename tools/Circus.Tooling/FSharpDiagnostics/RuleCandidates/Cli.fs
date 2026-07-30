module Circus.Tooling.FSharpDiagnostics.RuleCandidates.Cli

// =============================================================================
// Rule candidates CLI
// =============================================================================

open System
open System.IO
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Domain
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Engine
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Paths
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Serialization

// -----------------------------------------------------------------------------
// Exit codes
// -----------------------------------------------------------------------------

module ExitCode =
    let pass = 0
    let policyFailure = 1
    let operationalError = 2

// -----------------------------------------------------------------------------
// Command types
// -----------------------------------------------------------------------------

type Command =
    | InventoryCmd
    | RegenerateCmd
    | VerifyCmd
    | ShowCmd of candidateId: string
    | HelpCmd

// -----------------------------------------------------------------------------
// Help text
// -----------------------------------------------------------------------------

let helpText () : string =
    "fsharp-diagnostics rule-candidates — deterministic rule-candidate extraction\n"
    + "\n"
    + "Usage:\n"
    + "  circus-tooling fsharp-diagnostics rule-candidates inventory\n"
    + "  circus-tooling fsharp-diagnostics rule-candidates regenerate\n"
    + "  circus-tooling fsharp-diagnostics rule-candidates verify\n"
    + "  circus-tooling fsharp-diagnostics rule-candidates show <candidate-id>\n"
    + "  circus-tooling fsharp-diagnostics rule-candidates help\n"

// -----------------------------------------------------------------------------
// Command parsing
// -----------------------------------------------------------------------------

let parse (argv: string list) : Command =
    match argv with
    | []
    | [ "help" ]
    | [ "-h" ]
    | [ "--help" ] -> HelpCmd
    | [ "inventory" ] -> InventoryCmd
    | [ "regenerate" ] -> RegenerateCmd
    | [ "verify" ] -> VerifyCmd
    | [ "show"; id ] -> ShowCmd id
    | [ "show" ] ->
        eprintfn "error: show requires a candidate-id argument"
        HelpCmd
    | _ ->
        eprintfn "error: unknown command"
        HelpCmd

// -----------------------------------------------------------------------------
// Renderers
// -----------------------------------------------------------------------------

let renderInventory (result: ExtractionResult) : string =
    let sb = Text.StringBuilder()
    sb.AppendLine "fsharp-diagnostics rule-candidates inventory" |> ignore

    sb.AppendLine(sprintf "  eligible_episodes: %d" result.EligibleEpisodes)
    |> ignore

    sb.AppendLine(sprintf "  episodes_with_candidates: %d" result.EpisodesWithCandidates)
    |> ignore

    sb.AppendLine(sprintf "  candidates_total: %d" result.Candidates.Length)
    |> ignore

    sb.AppendLine(sprintf "  parser_cascade_candidates: %d" result.Candidates.Length)
    |> ignore

    sb.AppendLine(sprintf "  single_episode_candidates: %d" result.Candidates.Length)
    |> ignore

    if not (List.isEmpty result.Errors) then
        sb.AppendLine "  errors:" |> ignore

        for err in result.Errors do
            sb.AppendLine(sprintf "    %A" err) |> ignore

    sb.ToString()

let renderCandidate (c: RuleCandidate) : string =
    let sb = Text.StringBuilder()
    sb.AppendLine "fsharp-diagnostics rule-candidates show" |> ignore
    sb.AppendLine(sprintf "  candidate_id: %s" c.CandidateId) |> ignore

    sb.AppendLine(sprintf "  status: %s" (ruleCandidateStatusToken c.Status))
    |> ignore

    sb.AppendLine(sprintf "  kind: %s" (ruleCandidateKindToken c.Kind)) |> ignore

    sb.AppendLine(sprintf "  evidence_strength: %s" (evidenceStrengthToken c.EvidenceStrength))
    |> ignore

    sb.AppendLine(sprintf "  title: %s" c.Title) |> ignore
    sb.AppendLine(sprintf "  primary_path: %s" c.PrimaryPath) |> ignore

    sb.AppendLine(sprintf "  diagnostic_codes: %s" (String.concat ", " c.DiagnosticCodes))
    |> ignore

    sb.AppendLine(sprintf "  diagnostic_count: %d" c.DiagnosticCount) |> ignore
    sb.AppendLine(sprintf "  episode_id: %s" c.Evidence.EpisodeId) |> ignore
    sb.AppendLine(sprintf "  episode_key: %s" c.Evidence.EpisodeKey) |> ignore

    sb.AppendLine(sprintf "  before_commit_oid: %s" c.Evidence.BeforeCommitOid)
    |> ignore

    sb.AppendLine(sprintf "  after_commit_oid: %s" c.Evidence.AfterCommitOid)
    |> ignore

    sb.AppendLine(sprintf "  verification_evidence_ids: %s" (String.concat ", " c.Evidence.VerificationEvidenceIds))
    |> ignore

    sb.AppendLine(sprintf "  transition_ids: %d" c.Evidence.TransitionIds.Length)
    |> ignore

    sb.AppendLine "  limitations:" |> ignore

    for lim in c.Limitations do
        sb.AppendLine(sprintf "    - %s" lim) |> ignore

    sb.ToString()

let renderError (err: EngineError) : string =
    match err with
    | EngineError.EpisodeLoadFailed errors -> "Episode load failed:\n" + (errors |> String.concat "\n")
    | EngineError.TransitionLoadFailed errors -> "Transition load failed:\n" + (errors |> String.concat "\n")
    | EngineError.ChangeSetLoadFailed errors -> "Change set load failed:\n" + (errors |> String.concat "\n")
    | EngineError.VerificationEvidenceLoadFailed errors ->
        "Verification evidence load failed:\n" + (errors |> String.concat "\n")
    | EngineError.NoEligibleEpisodes -> "No eligible episodes for candidate extraction"
    | EngineError.CandidateGenerationFailed details -> "Candidate generation failed: " + details
    | EngineError.PublicationFailed details -> "Publication failed: " + details

// -----------------------------------------------------------------------------
// Run commands
// -----------------------------------------------------------------------------

let runInventory (repoRoot: string) : int =
    let result = extractCandidates repoRoot
    stdout.WriteLine(renderInventory result)

    if not (List.isEmpty result.Errors) then
        ExitCode.policyFailure
    else
        ExitCode.pass

let runRegenerate (repoRoot: string) : int =
    let result = runExtraction repoRoot

    if not (List.isEmpty result.Errors) then
        for err in result.Errors do
            eprintfn "error: %s" (renderError err)

        ExitCode.policyFailure
    else
        stdout.WriteLine(
            sprintf "fsharp-diagnostics rule-candidates regenerate: candidates=%d" result.Candidates.Length
        )

        ExitCode.pass

let runVerify (repoRoot: string) : int =
    // Regenerate and compare
    let result = runExtraction repoRoot

    if not (List.isEmpty result.Errors) then
        for err in result.Errors do
            eprintfn "error: %s" (renderError err)

        ExitCode.policyFailure
    else
        // Parse and verify candidates
        let candidatesPath = toAbsolutePath repoRoot ruleCandidatesJsonlRelativePath
        let summaryPath = toAbsolutePath repoRoot ruleCandidatesSummaryRelativePath

        if not (File.Exists candidatesPath) then
            eprintfn "error: candidates file not found: %s" candidatesPath
            ExitCode.policyFailure
        elif not (File.Exists summaryPath) then
            eprintfn "error: summary file not found: %s" summaryPath
            ExitCode.policyFailure
        else
            try
                let candidateLines = File.ReadAllLines candidatesPath
                let mutable parseErrors = 0

                for line in candidateLines do
                    match parseRuleCandidateStrict line with
                    | Result.Ok _ -> ()
                    | Result.Error e ->
                        parseErrors <- parseErrors + 1
                        eprintfn "error: candidate parse error: %A" e

                match parseRuleCandidateSummaryStrict (File.ReadAllText summaryPath) with
                | Result.Ok summary ->
                    // Verify summary consistency
                    if summary.CandidatesTotal <> result.Candidates.Length then
                        eprintfn "error: summary candidate count mismatch"
                        ExitCode.policyFailure
                    elif summary.CandidateIds.Length <> result.Candidates.Length then
                        eprintfn "error: summary candidate IDs count mismatch"
                        ExitCode.policyFailure
                    elif parseErrors > 0 then
                        eprintfn "error: %d candidate parse errors" parseErrors
                        ExitCode.policyFailure
                    else
                        stdout.WriteLine(
                            sprintf
                                "fsharp-diagnostics rule-candidates verify: candidates=%d verified"
                                result.Candidates.Length
                        )

                        ExitCode.pass
                | Result.Error e ->
                    eprintfn "error: summary parse error: %A" e
                    ExitCode.policyFailure
            with ex ->
                eprintfn "error: verification exception: %s" ex.Message
                ExitCode.operationalError

let runShow (repoRoot: string) (candidateId: string) : int =
    // Run extraction to get candidates
    let result = extractCandidates repoRoot

    match result.Candidates |> List.tryFind (fun c -> c.CandidateId = candidateId) with
    | Some candidate ->
        stdout.WriteLine(renderCandidate candidate)
        ExitCode.pass
    | None ->
        eprintfn "error: candidate %s not found" candidateId
        ExitCode.operationalError

// -----------------------------------------------------------------------------
// Main entry point
// -----------------------------------------------------------------------------

let run (argv: string list) : int =
    match parse argv with
    | HelpCmd ->
        stdout.WriteLine(helpText ())
        ExitCode.pass
    | InventoryCmd ->
        match Circus.Tooling.SourcePolicy.Cli.resolveRepoRoot () with
        | Ok root -> runInventory root
        | Result.Error msg ->
            eprintfn "error: %s" msg
            ExitCode.operationalError
    | RegenerateCmd ->
        match Circus.Tooling.SourcePolicy.Cli.resolveRepoRoot () with
        | Ok root -> runRegenerate root
        | Result.Error msg ->
            eprintfn "error: %s" msg
            ExitCode.operationalError
    | VerifyCmd ->
        match Circus.Tooling.SourcePolicy.Cli.resolveRepoRoot () with
        | Ok root -> runVerify root
        | Result.Error msg ->
            eprintfn "error: %s" msg
            ExitCode.operationalError
    | ShowCmd candidateId ->
        match Circus.Tooling.SourcePolicy.Cli.resolveRepoRoot () with
        | Ok root -> runShow root candidateId
        | Result.Error msg ->
            eprintfn "error: %s" msg
            ExitCode.operationalError
