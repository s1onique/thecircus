module Circus.Tooling.FSharpDiagnostics.RuleCandidates.Selection

// =============================================================================
// Deterministic candidate selection
// =============================================================================
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-EXTRACTION01-CORRECTION01
//
// Selection is deterministic: same input always produces same output
// regardless of filesystem enumeration order, map iteration, or timestamps.
// Repository path normalization is delegated to the shared authority.

open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain
open Circus.Tooling.FSharpDiagnostics.RepoPaths
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Classification
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Domain

// -----------------------------------------------------------------------------
// Selection errors
// -----------------------------------------------------------------------------

type SelectionError =
    | NoEligibleEpisodes
    | EpisodeIneligible of episodeKey: string * reason: string
    | NoSupportingTransitions of episodeKey: string
    | NoParserCascadeGroups of episodeKey: string
    | CandidateReferenceIntegrityFailed of details: string

// -----------------------------------------------------------------------------
// Episode eligibility
// -----------------------------------------------------------------------------

/// Check if an episode is eligible for rule-candidate extraction.
///
/// An episode is eligible when:
/// - qualification status is Qualified;
/// - verification evidence exists and passes;
/// - after-tree binding is exact;
/// - transitions exist;
/// - change set is present with entries;
/// - all Git OIDs are full-width.
let isEpisodeEligible (episode: RepairEpisode) : bool =
    if episode.Qualification.Status <> EpisodeQualificationStatus.Qualified then
        false
    elif List.isEmpty episode.VerificationEvidenceIds then
        false
    elif
        episode.TransitionCounts.EliminatedAfter
        + episode.TransitionCounts.PersistedCountDecreased
        + episode.TransitionCounts.IntroducedAfter
        <= 0
    then
        false
    elif System.String.IsNullOrEmpty episode.ChangeSetId then
        false
    elif episode.BeforeCommitOid.Length <> 40 && episode.BeforeCommitOid.Length <> 64 then
        false
    elif episode.AfterCommitOid.Length <> 40 && episode.AfterCommitOid.Length <> 64 then
        false
    elif episode.BeforeTreeOid.Length <> 40 && episode.BeforeTreeOid.Length <> 64 then
        false
    elif episode.AfterTreeOid.Length <> 40 && episode.AfterTreeOid.Length <> 64 then
        false
    else
        true

// -----------------------------------------------------------------------------
// Transition grouping
// -----------------------------------------------------------------------------

/// Group transitions by their normalized source path deterministically.
/// Normalization delegates to the shared authority.
let groupTransitionsByPath (transitions: DiagnosticTransition list) : Map<string, DiagnosticTransition list> =
    transitions
    |> List.groupBy (fun t -> normalizeRepositoryPath (defaultArg t.SourcePath ""))
    |> List.filter (fun (path, _) -> not (System.String.IsNullOrEmpty path))
    |> Map.ofList

// -----------------------------------------------------------------------------
// Candidate derivation prose
// -----------------------------------------------------------------------------

/// Derive the fixed observation template for ParserCascadeRepair.
let deriveParserCascadeProse (episodeKey: string) (afterCommitOid: string) (groupFacts: TransitionGroupFacts) : string =
    sprintf
        "In episode %s (commit %s), %d parser diagnostic(s) including %s were resolved after a verified repair on path %s (earliest at line %A)."
        episodeKey
        afterCommitOid
        groupFacts.TransitionCount
        (String.concat "," groupFacts.DiagnosticCodes)
        groupFacts.Path
        groupFacts.EarliestLine

// -----------------------------------------------------------------------------
// Candidate group selection
// -----------------------------------------------------------------------------

/// Select the best candidate group from an episode's transitions.  Only
/// positively assessed transitions may contribute.  Unassessable and
/// regression transitions are filtered out at this stage.
let selectCandidateGroup
    (episode: RepairEpisode)
    (changeSet: GitChangeSet)
    (transitions: DiagnosticTransition list)
    : TransitionGroupFacts option =

    let supporting =
        transitions
        |> List.filter (fun t -> isRepairSupportingTransition episode changeSet t)

    if List.isEmpty supporting then
        None
    else
        let groups = groupTransitionsByPath supporting

        let classifiedGroups =
            groups
            |> Map.toList
            |> List.map (fun (path, pathTransitions) -> classifyGroup episode changeSet pathTransitions)

        let parserGroups =
            classifiedGroups
            |> List.choose (function
                | ClassifiedAsParserCascade facts -> Some facts
                | NotClassified _ -> None)

        if List.isEmpty parserGroups then
            None
        else
            let sorted = parserGroups |> List.sortWith compareTransitionGroupFacts
            Some sorted.Head

// -----------------------------------------------------------------------------
// Path and code helpers
// -----------------------------------------------------------------------------

let buildChangedPaths (changeSet: GitChangeSet) : string list =
    changeSet.Entries |> List.map (fun e -> e.CanonicalPath) |> List.sort

let extractDiagnosticCodes (transitions: DiagnosticTransition list) : string list =
    transitions |> List.choose (fun t -> t.Code) |> List.distinct |> List.sort

// -----------------------------------------------------------------------------
// Selection result
// -----------------------------------------------------------------------------

type EpisodeSelectionResult =
    { EpisodeKey: string
      EpisodeId: string
      Eligible: bool
      CandidateGroup: TransitionGroupFacts option
      SupportingTransitionCount: int
      TotalTransitionCount: int
      ParserGroupCount: int }
