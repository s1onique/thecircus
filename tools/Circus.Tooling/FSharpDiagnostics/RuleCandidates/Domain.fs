module Circus.Tooling.FSharpDiagnostics.RuleCandidates.Domain

// =============================================================================
// Rule candidate domain model
// =============================================================================

open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain

// -----------------------------------------------------------------------------
// Schema version
// -----------------------------------------------------------------------------

let RuleCandidateSchemaVersion = "rule-candidate-v1"
let RuleCandidateSummarySchemaVersion = "rule-candidate-summary-v1"

// -----------------------------------------------------------------------------
// Candidate kind
// -----------------------------------------------------------------------------

[<RequireQualifiedAccess>]
type RuleCandidateKind = ParserCascadeRepair

let ruleCandidateKindToken (k: RuleCandidateKind) : string =
    match k with
    | RuleCandidateKind.ParserCascadeRepair -> "parser_cascade_repair"

let tryParseRuleCandidateKind (token: string) : RuleCandidateKind option =
    match token with
    | "parser_cascade_repair" -> Some RuleCandidateKind.ParserCascadeRepair
    | _ -> None

// -----------------------------------------------------------------------------
// Evidence strength
// -----------------------------------------------------------------------------

[<RequireQualifiedAccess>]
type EvidenceStrength = SingleEpisodeObservedRepair

let evidenceStrengthToken (s: EvidenceStrength) : string =
    match s with
    | EvidenceStrength.SingleEpisodeObservedRepair -> "single_episode_observed_repair"

let tryParseEvidenceStrength (token: string) : EvidenceStrength option =
    match token with
    | "single_episode_observed_repair" -> Some EvidenceStrength.SingleEpisodeObservedRepair
    | _ -> None

// -----------------------------------------------------------------------------
// Candidate status
// -----------------------------------------------------------------------------

[<RequireQualifiedAccess>]
type RuleCandidateStatus = Proposed

let ruleCandidateStatusToken (s: RuleCandidateStatus) : string =
    match s with
    | RuleCandidateStatus.Proposed -> "proposed"

let tryParseRuleCandidateStatus (token: string) : RuleCandidateStatus option =
    match token with
    | "proposed" -> Some RuleCandidateStatus.Proposed
    | _ -> None

// -----------------------------------------------------------------------------
// Evidence reference
// -----------------------------------------------------------------------------

type RuleCandidateEvidence =
    { EpisodeId: string
      EpisodeKey: string
      ChangeSetId: string
      VerificationEvidenceIds: string list
      TransitionIds: string list
      BeforeCommitOid: string
      BeforeTreeOid: string
      AfterCommitOid: string
      AfterTreeOid: string }

// -----------------------------------------------------------------------------
// Rule candidate
// -----------------------------------------------------------------------------

type RuleCandidate =
    { SchemaVersion: string
      CandidateId: string
      Status: RuleCandidateStatus
      Kind: RuleCandidateKind
      EvidenceStrength: EvidenceStrength
      Title: string
      Symptom: string
      Applicability: string
      Observation: string
      ProposedRepair: string
      Limitations: string list
      PrimaryPath: string
      DiagnosticCodes: string list
      DiagnosticCount: int
      EarliestLine: int option
      ChangedPaths: string list
      Evidence: RuleCandidateEvidence }

// -----------------------------------------------------------------------------
// Summary
// -----------------------------------------------------------------------------

type RuleCandidateSummary =
    { SchemaVersion: string
      EligibleEpisodes: int
      EpisodesWithCandidates: int
      CandidatesTotal: int
      ParserCascadeCandidates: int
      SingleEpisodeCandidates: int
      CandidateIds: string list }

// -----------------------------------------------------------------------------
// Transition group facts
// -----------------------------------------------------------------------------

type TransitionGroupFacts =
    { Path: string
      TransitionCount: int
      DiagnosticCodes: string list
      EarliestLine: int option
      TransitionIds: string list }

let compareTransitionGroupFacts (a: TransitionGroupFacts) (b: TransitionGroupFacts) : int =
    // 1. Transition count descending
    match compare b.TransitionCount a.TransitionCount with
    | 0 ->
        // 2. Distinct code count descending
        match compare b.DiagnosticCodes.Length a.DiagnosticCodes.Length with
        | 0 ->
            // 3. Earliest line ascending (None = infinity)
            let cmpLine x y =
                match x, y with
                | None, None -> 0
                | None, Some _ -> 1
                | Some _, None -> -1
                | Some xl, Some yl -> compare xl yl
            match cmpLine a.EarliestLine b.EarliestLine with
            | 0 ->
                // 4. Path ordinal ascending
                compare a.Path b.Path
            | x -> x
        | x -> x
    | x -> x

// -----------------------------------------------------------------------------
// Candidate selection
// -----------------------------------------------------------------------------

type CandidateSelector = RepairEpisode -> GitChangeSet -> DiagnosticTransition list -> TransitionGroupFacts option

// -----------------------------------------------------------------------------
// Prose derivation
// -----------------------------------------------------------------------------

let deriveParserCascadeProse (episodeKey: string) (afterCommitOid: string) (gf: TransitionGroupFacts) : string =
    sprintf
        "In episode %s (commit %s), %d parser diagnostic(s) including %s were resolved after a verified repair on path %s (earliest at line %A)."
        episodeKey
        afterCommitOid
        gf.TransitionCount
        (String.concat "," gf.DiagnosticCodes)
        gf.Path
        gf.EarliestLine

// -----------------------------------------------------------------------------
// Parser family classification
// -----------------------------------------------------------------------------

/// Closed set of parser-family diagnostic codes per ACT specification.
let parserDiagnosticCodes =
    Set.ofList [
        "FS0010"
        "FS0603"
        "FS1156"
        "FS3118"
    ]

let isParserDiagnostic (code: string) : bool = Set.contains code parserDiagnosticCodes

let isNonParserDiagnostic (code: string) : bool =
    code.StartsWith("FS") && not (isParserDiagnostic code)
