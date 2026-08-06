module Circus.Tooling.FSharpDiagnostics.RuleCandidates.Domain

// =============================================================================
// Rule candidate domain model
// =============================================================================
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-EXTRACTION01-CORRECTION01
//
// A `rule-candidate-v2` record describes an observed candidate only.  It must
// not instruct an agent to modify code.  Repair advice, when produced by a
// later curation act, is published separately as `repair-advice-v1` and
// `llm-tip-v1` artifacts and is never embedded inside a candidate.
//
// The schema was bumped from `rule-candidate-v1` to `rule-candidate-v2`
// because `v1` had already been published as a compatibility surface and
// contained `proposed_repair`.  Silent mutation of an already-published
// schema version is forbidden.

open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain

// -----------------------------------------------------------------------------
// Schema version
// -----------------------------------------------------------------------------

let RuleCandidateSchemaVersion = "rule-candidate-v2"
let RuleCandidateSummarySchemaVersion = "rule-candidate-summary-v2"

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
      BeforeCommitOid: string
      BeforeTreeOid: string
      AfterCommitOid: string
      AfterTreeOid: string }

/// Structural record of evidence transitions.
///   * `supporting_transition_ids` count positively towards the candidate.
///   * `context_transition_ids` are attached for context only and contribute
///     no positive weight.
///   * `counterevidence_transition_ids` weaken the candidate.
type RuleCandidateTransitionPartition =
    { SupportingTransitionIds: string list
      ContextTransitionIds: string list
      CounterevidenceTransitionIds: string list }

let emptyTransitionPartition : RuleCandidateTransitionPartition =
    { SupportingTransitionIds = []
      ContextTransitionIds = []
      CounterevidenceTransitionIds = [] }

// -----------------------------------------------------------------------------
// Curation flags
// -----------------------------------------------------------------------------

/// Candidate status flags.  A `rule-candidate-v2` record is always emitted
/// with all three flags set to false.  Later acts flip them when the
/// corresponding downstream artifact exists and is bound.
type CandidateStatusFlags =
    { CausalFamilyCurated: bool
      RepairAdviceAvailable: bool
      LlmTipAvailable: bool }

/// Default status flags - all false for proposed candidates.
let defaultCandidateStatusFlags =
    { CausalFamilyCurated = false
      RepairAdviceAvailable = false
      LlmTipAvailable = false }

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
      ApplicabilityConditions: string
      Observation: string
      CandidateHypothesis: string
      Limitations: string list
      PrimaryPath: string
      DiagnosticCodes: string list
      DiagnosticCount: int
      EarliestLine: int option
      ChangedPaths: string list
      StatusFlags: CandidateStatusFlags
      TransitionPartition: RuleCandidateTransitionPartition
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
let parserDiagnosticCodes = Set.ofList [ "FS0010"; "FS0603"; "FS1156"; "FS3118" ]

let isParserDiagnostic (code: string) : bool = Set.contains code parserDiagnosticCodes

let isNonParserDiagnostic (code: string) : bool =
    code.StartsWith("FS") && not (isParserDiagnostic code)

// -----------------------------------------------------------------------------
// Transition assessment authority
// -----------------------------------------------------------------------------

/// The set of `TransitionAssessment` values that count positively towards a
/// rule candidate.  Any transition whose assessment is not in this set is
/// not positive support, no matter how "parser-like" the diagnostic looks.
///
/// The spec uses the names `ResolutionObservation` and
/// `MultiplicityImprovementObservation`; we map them to the existing
/// `TransitionAssessment` discriminants.
let isPositiveTransitionAssessment (a: TransitionAssessment) : bool =
    match a with
    | TransitionAssessment.ObservedResolutionCandidate -> true
    | TransitionAssessment.MultiplicityImprovementCandidate -> true
    | _ -> false

/// The set of `TransitionAssessment` values that weaken or contradict the
/// candidate.  They are never positive support.
let isCounterevidenceTransitionAssessment (a: TransitionAssessment) : bool =
    match a with
    | TransitionAssessment.ObservedRegressionCandidate -> true
    | TransitionAssessment.MultiplicityRegressionCandidate -> true
    | TransitionAssessment.IntroducedWithSourceAddition -> true
    | _ -> false

/// Context-only assessments: parser family diagnostics that cannot
/// independently demonstrate causation.
let isContextTransitionAssessment (a: TransitionAssessment) : bool =
    match a with
    | TransitionAssessment.Unassessable -> true
    | TransitionAssessment.Ambiguous -> true
    | TransitionAssessment.ExactPersistence -> true
    | TransitionAssessment.EliminatedBySourceRemoval -> true
    | _ -> false
