module Circus.Tooling.FSharpDiagnostics.RuleCandidates.Engine

// =============================================================================
// Rule candidate extraction engine
// =============================================================================

open System
open System.IO
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Engine
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Classification
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Domain
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Paths
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Selection
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Serialization

// -----------------------------------------------------------------------------
// Engine errors
// -----------------------------------------------------------------------------

type EngineError =
    | EpisodeLoadFailed of errors: string list
    | VerificationEvidenceLoadFailed of errors: string list
    | TransitionLoadFailed of errors: string list
    | ChangeSetLoadFailed of errors: string list
    | NoEligibleEpisodes
    | CandidateGenerationFailed of details: string
    | PublicationFailed of details: string
    | Internal of message: string

// -----------------------------------------------------------------------------
// Fixed prose templates
// -----------------------------------------------------------------------------

let parserCascadeTitle =
    "Repair the earliest malformed binding before chasing downstream F# parser errors"

let parserCascadeSymptom =
    "Multiple parser diagnostics occur in one changed F# source path, including FS0010 or FS3118, and later diagnostics appear in the same local region."

let parserCascadeApplicability =
    "Use this candidate only when the diagnostics form a same-path parser cluster, the path changed in the verified repair episode, and the after-state no longer contains the same exact failures."

let parserCascadeProposedRepair =
    "Inspect the earliest parser diagnostic in the cluster and restore the complete binding, expression, indentation, or delimiter structure before attempting to fix later parser diagnostics individually. Rebuild after repairing the earliest syntax break."

let parserCascadeLimitations =
    [ "This is supported by one observed repair episode."
      "Path-level change support does not prove line-level causation."
      "The candidate has not yet been reproduced with a minimal compiler fixture."
      "The candidate is not a universal interpretation of FS0010 or FS3118." ]

// -----------------------------------------------------------------------------
// Extraction result
// -----------------------------------------------------------------------------

type ExtractionResult =
    { Candidates: RuleCandidate list
      EligibleEpisodes: int
      EpisodesWithCandidates: int
      Errors: EngineError list }

// -----------------------------------------------------------------------------
// Publication
// -----------------------------------------------------------------------------

let publishCandidates (repoRoot: string) (result: ExtractionResult) : bool =
    try
        let cpath = toAbsolutePath repoRoot ruleCandidatesJsonlRelativePath
        let spath = toAbsolutePath repoRoot ruleCandidatesSummaryRelativePath
        let clines = result.Candidates |> List.map renderRuleCandidate

        let sum =
            { SchemaVersion = RuleCandidateSummarySchemaVersion
              EligibleEpisodes = result.EligibleEpisodes
              EpisodesWithCandidates = result.EpisodesWithCandidates
              CandidatesTotal = result.Candidates.Length
              ParserCascadeCandidates = result.Candidates.Length
              SingleEpisodeCandidates = result.Candidates.Length
              CandidateIds = result.Candidates |> List.map (fun c -> c.CandidateId) |> List.sort }

        let json = renderRuleCandidateSummary sum
        writeAllLines (cpath + ".tmp") clines
        writeLineOriented (spath + ".tmp") json

        if File.Exists cpath then
            File.Delete cpath

        if File.Exists spath then
            File.Delete spath

        File.Move(cpath + ".tmp", cpath)
        File.Move(spath + ".tmp", spath)
        true
    with _ ->
        false

// -----------------------------------------------------------------------------
// Single canonical input snapshot from episode engine
// -----------------------------------------------------------------------------

/// Single authoritative snapshot of all inputs from one episode engine execution.
/// Prevents multiple expensive engine runs and ensures data consistency.
type RuleCandidateInputs =
    { Episodes: RepairEpisode list
      Transitions: DiagnosticTransition list
      ChangeSets: Map<string, GitChangeSet>
      VerificationEvidence: Map<string, VerificationEvidence> }

let private mapEpisodeEngineFailure (failure: EpisodeEngineFailure) : EngineError =
    match failure with
    | EpisodeEngineFailure.VerificationEvidenceLoadFailed errors ->
        EngineError.VerificationEvidenceLoadFailed (errors |> List.map string)
    | EpisodeEngineFailure.DeclarationLoadFailed issues ->
        EngineError.Internal (sprintf "Declaration load failed: %A" issues)
    | EpisodeEngineFailure.PublicationFailed (_, msg) ->
        EngineError.PublicationFailed msg
    | EpisodeEngineFailure.InternalFailure (op, msg) ->
        EngineError.Internal (sprintf "Internal failure in %s: %s" op msg)

let private loadFromEpisodeEngine (repoRoot: string) : Result<RuleCandidateInputs, EngineError> =
    match runEpisodeEngine repoRoot defaultEngineOptions with
    | EpisodeEngineExecution.Failed failure -> Error(mapEpisodeEngineFailure failure)
    | EpisodeEngineExecution.Completed result ->
        Ok
            { Episodes = result.RepairEpisodes
              Transitions = result.Transitions
              ChangeSets =
                  result.ChangeSets
                  |> List.map (fun cs -> cs.ChangeSetId, cs)
                  |> Map.ofList
              VerificationEvidence =
                  result.Verification
                  |> List.map (fun lv -> lv.Evidence.EvidenceId, lv.Evidence)
                  |> Map.ofList }

// Backward-compatible individual loaders that share one engine execution
let loadAllInputs (repoRoot: string) : Result<RuleCandidateInputs, EngineError> =
    loadFromEpisodeEngine repoRoot

let loadRepairEpisodes (repoRoot: string) : Result<RepairEpisode list, EngineError> =
    loadFromEpisodeEngine repoRoot |> Result.map (fun r -> r.Episodes)

let loadTransitions (repoRoot: string) : Result<DiagnosticTransition list, EngineError> =
    loadFromEpisodeEngine repoRoot |> Result.map (fun r -> r.Transitions)

let loadChangeSets (repoRoot: string) : Result<Map<string, GitChangeSet>, EngineError> =
    loadFromEpisodeEngine repoRoot |> Result.map (fun r -> r.ChangeSets)

let loadVerificationEvidence (repoRoot: string) : Result<Map<string, VerificationEvidence>, EngineError> =
    loadFromEpisodeEngine repoRoot |> Result.map (fun r -> r.VerificationEvidence)

// -----------------------------------------------------------------------------
// Candidate building
// -----------------------------------------------------------------------------

let buildCandidate (ep: RepairEpisode) (cs: GitChangeSet) (gf: TransitionGroupFacts) : RuleCandidate =
    let obs = deriveParserCascadeProse ep.EpisodeKey ep.AfterCommitOid gf

    let evid =
        { EpisodeId = ep.EpisodeId
          EpisodeKey = ep.EpisodeKey
          ChangeSetId = ep.ChangeSetId
          VerificationEvidenceIds = ep.VerificationEvidenceIds
          TransitionIds = gf.TransitionIds
          BeforeCommitOid = ep.BeforeCommitOid
          BeforeTreeOid = ep.BeforeTreeOid
          AfterCommitOid = ep.AfterCommitOid
          AfterTreeOid = ep.AfterTreeOid }

    let cpaths = buildChangedPaths cs

    let cid =
        computeCandidateId
            RuleCandidateSchemaVersion
            RuleCandidateKind.ParserCascadeRepair
            EvidenceStrength.SingleEpisodeObservedRepair
            parserCascadeTitle
            parserCascadeSymptom
            parserCascadeApplicability
            obs
            parserCascadeProposedRepair
            parserCascadeLimitations
            gf.Path
            gf.DiagnosticCodes
            gf.TransitionCount
            gf.EarliestLine
            cpaths
            ep.EpisodeId
            ep.EpisodeKey
            ep.ChangeSetId
            ep.VerificationEvidenceIds
            gf.TransitionIds
            ep.BeforeCommitOid
            ep.BeforeTreeOid
            ep.AfterCommitOid
            ep.AfterTreeOid

    { SchemaVersion = RuleCandidateSchemaVersion
      CandidateId = cid
      Status = RuleCandidateStatus.Proposed
      Kind = RuleCandidateKind.ParserCascadeRepair
      EvidenceStrength = EvidenceStrength.SingleEpisodeObservedRepair
      Title = parserCascadeTitle
      Symptom = parserCascadeSymptom
      Applicability = parserCascadeApplicability
      Observation = obs
      ProposedRepair = parserCascadeProposedRepair
      Limitations = parserCascadeLimitations
      PrimaryPath = gf.Path
      DiagnosticCodes = gf.DiagnosticCodes
      DiagnosticCount = gf.TransitionCount
      EarliestLine = gf.EarliestLine
      ChangedPaths = cpaths
      Evidence = evid }

// -----------------------------------------------------------------------------
// Verification evidence binding
// -----------------------------------------------------------------------------

/// Validates that all referenced verification evidence records exist and are valid.
/// Returns None if any binding fails, Some with error message otherwise.
let validateVerificationBinding (ep: RepairEpisode) (verificationMap: Map<string, VerificationEvidence>) : string option =
    let evidId = ep.VerificationEvidenceIds

    if evidId.IsEmpty then
        Some "Episode has no verification evidence references"

    else
        let mutable firstError = None

        for evid in evidId do
            match Map.tryFind evid verificationMap with
            | None ->
                firstError <- Some (sprintf "Verification evidence %s not found" evid)
            | Some record ->
                // Verify episode_id matches
                if record.EpisodeId <> ep.EpisodeId then
                    firstError <-
                        Some (sprintf "Verification evidence %s has episode_id mismatch" evid)

                // Verify status is pass
                elif record.Status <> VerificationStatus.Pass then
                    firstError <-
                        Some (sprintf "Verification evidence %s has status %A (expected Pass)" evid record.Status)

                // Verify exit code is 0
                elif record.ExitCode <> 0 then
                    firstError <-
                        Some (sprintf "Verification evidence %s has exit_code %d (expected 0)" evid record.ExitCode)

                // Verify tested commit matches after commit
                elif record.TestedCommitOid <> ep.AfterCommitOid then
                    firstError <-
                        Some (sprintf "Verification evidence %s tested_commit_oid mismatch" evid)

                // Verify tested tree matches after tree
                elif record.TestedTreeOid <> ep.AfterTreeOid then
                    firstError <-
                        Some (sprintf "Verification evidence %s tested_tree_oid mismatch" evid)

        firstError

// -----------------------------------------------------------------------------
// Extraction
// -----------------------------------------------------------------------------

let extractCandidates (repoRoot: string) : ExtractionResult =
    let cands = ResizeArray<RuleCandidate>()
    let mutable elEp = 0
    let mutable epWC = 0
    let errs = ResizeArray<EngineError>()

    match loadAllInputs repoRoot with
    | Result.Error e ->
        errs.Add e
        { Candidates = []; EligibleEpisodes = 0; EpisodesWithCandidates = 0; Errors = errs |> Seq.toList }

    | Result.Ok inputs ->
        for ep in inputs.Episodes do
            // Check episode eligibility
            if isEpisodeEligible ep then
                // Verify all evidence bindings
                match validateVerificationBinding ep inputs.VerificationEvidence with
                | Some errMsg ->
                    errs.Add(EngineError.CandidateGenerationFailed errMsg)
                | None ->
                    elEp <- elEp + 1

                    let et = inputs.Transitions |> List.filter (fun t -> t.EpisodeId = ep.EpisodeId)

                    match
                        selectCandidateGroup ep (Map.find ep.ChangeSetId inputs.ChangeSets) et
                    with
                    | Some gf ->
                        cands.Add(buildCandidate ep (Map.find ep.ChangeSetId inputs.ChangeSets) gf)
                        epWC <- epWC + 1
                    | None -> ()

        // Add terminal error if no eligible episodes
        if elEp = 0 && errs.Count = 0 then
            errs.Add EngineError.NoEligibleEpisodes

        { Candidates = cands |> Seq.toList
          EligibleEpisodes = elEp
          EpisodesWithCandidates = epWC
          Errors = errs |> Seq.toList }

let runExtraction (repoRoot: string) : ExtractionResult =
    let result = extractCandidates repoRoot

    if result.Errors.IsEmpty && not (List.isEmpty result.Candidates) then
        if not (publishCandidates repoRoot result) then
            { result with
                Errors = [ EngineError.PublicationFailed "Failed to write output files" ] }
        else
            result
    else
        result
