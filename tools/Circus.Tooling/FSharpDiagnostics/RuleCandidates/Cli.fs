module Circus.Tooling.FSharpDiagnostics.RuleCandidates.Cli

// =============================================================================
// Rule candidates CLI
// =============================================================================
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-EXTRACTION01-CORRECTION01
//
// The `verify` command performs no writes.  It recomputes the candidate ID
// from the parsed semantic fields and reconciles the summary counts.
// On success it exits zero with the canonical bytes byte-identical to
// before the call.

open System
open System.IO
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Domain
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Engine
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Paths
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Serialization

module ExitCode =
    let pass = 0
    let policyFailure = 1
    let operationalError = 2

type Command =
    | InventoryCmd
    | RegenerateCmd
    | VerifyCmd
    | ShowCmd of candidateId: string
    | HelpCmd

let helpText () : string =
    "fsharp-diagnostics rule-candidates — deterministic rule-candidate extraction\n"
    + "\n"
    + "Usage:\n"
    + "  circus-tooling fsharp-diagnostics rule-candidates inventory\n"
    + "  circus-tooling fsharp-diagnostics rule-candidates regenerate\n"
    + "  circus-tooling fsharp-diagnostics rule-candidates verify\n"
    + "  circus-tooling fsharp-diagnostics rule-candidates show <candidate-id>\n"
    + "  circus-tooling fsharp-diagnostics rule-candidates help\n"

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

    sb.AppendLine(sprintf "  supporting_transition_ids: %d" c.TransitionPartition.SupportingTransitionIds.Length)
    |> ignore

    sb.AppendLine(sprintf "  context_transition_ids: %d" c.TransitionPartition.ContextTransitionIds.Length)
    |> ignore

    sb.AppendLine(sprintf "  counterevidence_transition_ids: %d" c.TransitionPartition.CounterevidenceTransitionIds.Length)
    |> ignore

    sb.AppendLine(sprintf "  episode_id: %s" c.Evidence.EpisodeId) |> ignore
    sb.AppendLine(sprintf "  episode_key: %s" c.Evidence.EpisodeKey) |> ignore

    sb.AppendLine(sprintf "  before_commit_oid: %s" c.Evidence.BeforeCommitOid)
    |> ignore

    sb.AppendLine(sprintf "  after_commit_oid: %s" c.Evidence.AfterCommitOid)
    |> ignore

    sb.AppendLine(sprintf "  verification_evidence_ids: %s" (String.concat ", " c.Evidence.VerificationEvidenceIds))
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
    | EngineError.DuplicateInputIdentities(kind, ids) ->
        sprintf "Duplicate %A identities found: %s" kind (String.concat ", " ids)
    | EngineError.InvalidInputIdentity(kind, idx, field, reason) ->
        sprintf "Invalid %A identity at index %d: field '%s' is %s" kind idx field reason
    | EngineError.Internal msg -> "Internal error: " + msg
    | EngineError.UnsupportedRepairEpisodeSchemaVersion ver ->
        sprintf "Unsupported repair-episode schema version: %s" ver
    | EngineError.UnsupportedChangeSetSchemaVersion ver ->
        sprintf "Unsupported change-set schema version: %s" ver
    | EngineError.UnsupportedVerificationEvidenceSchemaVersion ver ->
        sprintf "Unsupported verification-evidence schema version: %s" ver
    | EngineError.MalformedRepairEpisodeJson(line, msg) ->
        sprintf "Malformed repair-episode JSON at line %d: %s" line msg
    | EngineError.MalformedChangeSetJson(line, msg) ->
        sprintf "Malformed change-set JSON at line %d: %s" line msg
    | EngineError.MalformedTransitionJson(line, msg) ->
        sprintf "Malformed transition JSON at line %d: %s" line msg
    | EngineError.MalformedVerificationEvidenceJson(line, msg) ->
        sprintf "Malformed verification-evidence JSON at line %d: %s" line msg

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
    let verdict, byteIdentical = runReadOnlyVerify repoRoot

    match verdict with
    | Verified ->
        if byteIdentical then
            stdout.WriteLine "fsharp-diagnostics rule-candidates verify: VERIFIED (canonical bytes unchanged)"
            ExitCode.pass
        else
            eprintfn "error: canonical bytes changed during verification (verifier must be read-only)"
            ExitCode.policyFailure
    | IdentityMismatch(_, _, reason) ->
        eprintfn "error: identity mismatch: %s" reason
        ExitCode.policyFailure
    | SummaryMismatch reason ->
        eprintfn "error: summary mismatch: %s" reason
        ExitCode.policyFailure
    | ParseFailure reason ->
        eprintfn "error: parse failure: %s" reason
        ExitCode.policyFailure
    | OutputMissing path ->
        eprintfn "error: canonical output missing: %s" path
        ExitCode.policyFailure
    | MultipleCandidatesWhenExactlyOneRequired ->
        eprintfn "error: exactly one candidate is required but multiple were found"
        ExitCode.policyFailure

let runShow (repoRoot: string) (candidateId: string) : int =
    let result = extractCandidates repoRoot

    match result.Candidates |> List.tryFind (fun c -> c.CandidateId = candidateId) with
    | Some candidate ->
        stdout.WriteLine(renderCandidate candidate)
        ExitCode.pass
    | None ->
        eprintfn "error: candidate %s not found" candidateId
        ExitCode.operationalError

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
