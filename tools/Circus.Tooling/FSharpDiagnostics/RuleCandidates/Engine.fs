module Circus.Tooling.FSharpDiagnostics.RuleCandidates.Engine

// =============================================================================
// Rule candidate extraction engine
// =============================================================================

open System
open System.IO
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
// Stub loaders - actual implementation requires repair-episodes engine integration
// -----------------------------------------------------------------------------

let loadRepairEpisodes (repoRoot: string) : Result<RepairEpisode list, EngineError> = Result.Ok []
let loadTransitions (repoRoot: string) : Result<DiagnosticTransition list, EngineError> = Result.Ok []
let loadChangeSets (repoRoot: string) : Result<Map<string, GitChangeSet>, EngineError> = Result.Ok Map.empty

let loadVerificationEvidence (repoRoot: string) : Result<Map<string, VerificationEvidence>, EngineError> =
    Result.Ok Map.empty

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
// Extraction
// -----------------------------------------------------------------------------

let extractCandidates (repoRoot: string) : ExtractionResult =
    let cands = ResizeArray<RuleCandidate>()
    let mutable elEp = 0
    let mutable epWC = 0
    let errs = ResizeArray<EngineError>()

    match loadRepairEpisodes repoRoot with
    | Result.Error e -> errs.Add e
    | Result.Ok eps ->
        match loadTransitions repoRoot with
        | Result.Error e -> errs.Add e
        | Result.Ok trans ->
            match loadChangeSets repoRoot with
            | Result.Error e -> errs.Add e
            | Result.Ok css ->
                match loadVerificationEvidence repoRoot with
                | Result.Error e -> errs.Add e
                | Result.Ok _ ->
                    for ep in eps do
                        if isEpisodeEligible ep then
                            elEp <- elEp + 1
                            let et = trans |> List.filter (fun t -> t.EpisodeId = ep.EpisodeId)

                            match selectCandidateGroup ep (Map.find ep.ChangeSetId css) et with
                            | Some gf ->
                                cands.Add(buildCandidate ep (Map.find ep.ChangeSetId css) gf)
                                epWC <- epWC + 1
                            | None -> ()

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
