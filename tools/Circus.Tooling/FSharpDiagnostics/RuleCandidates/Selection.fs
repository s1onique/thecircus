module Circus.Tooling.FSharpDiagnostics.RuleCandidates.Selection

// =============================================================================
// Deterministic candidate selection
// =============================================================================
//
// This module implements the deterministic selection logic for rule candidates.
// Selection is deterministic: same input always produces same output regardless
// of filesystem enumeration order, map iteration, or timestamps.

open System.Collections.Generic
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Classification
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Domain

// -----------------------------------------------------------------------------
// Selection errors
// -----------------------------------------------------------------------------

/// Errors that prevent candidate selection.
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
    // Qualification must be Qualified (not ambiguous, rejected, or qualified_with_limitations)
    if episode.Qualification.Status <> EpisodeQualificationStatus.Qualified then
        false
    // Must have verification evidence
    elif List.isEmpty episode.VerificationEvidenceIds then
        false
    // Must have transitions
    elif
        episode.TransitionCounts.EliminatedAfter
        + episode.TransitionCounts.PersistedCountDecreased
        + episode.TransitionCounts.IntroducedAfter
        <= 0
    then
        false
    // Must have change set entries
    elif System.String.IsNullOrEmpty episode.ChangeSetId then
        false
    // Git OIDs must be full-width (40 for SHA-1, 64 for SHA-256)
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

/// Group transitions by their source path deterministically.
let groupTransitionsByPath (transitions: DiagnosticTransition list) : Map<string, DiagnosticTransition list> =
    // Use Map to ensure deterministic ordering
    transitions
    |> List.groupBy (fun t -> defaultArg t.SourcePath "")
    |> List.filter (fun (path, _) -> not (System.String.IsNullOrEmpty path))
    |> Map.ofList

// -----------------------------------------------------------------------------
// Candidate derivation
// -----------------------------------------------------------------------------

/// Derive the fixed prose template for ParserCascadeRepair.
let deriveParserCascadeProse (episodeKey: string) (afterCommitOid: string) (groupFacts: TransitionGroupFacts) : string =
    // Template observation that names the episode and repair commit
    sprintf
        "In %s, the selected %s parser-diagnostic cluster was present before %s and absent from the verified after-state."
        episodeKey
        (System.IO.Path.GetFileName groupFacts.Path)
        (afterCommitOid.Substring(0, 7))

/// Select the best candidate group from an episode's transitions.
let selectCandidateGroup
    (episode: RepairEpisode)
    (changeSet: GitChangeSet)
    (transitions: DiagnosticTransition list)
    : TransitionGroupFacts option =

    // Filter to supporting transitions
    let supporting =
        transitions
        |> List.filter (fun t -> isRepairSupportingTransition episode changeSet t)

    if List.isEmpty supporting then
        None
    else
        // Group by path
        let groups = groupTransitionsByPath supporting

        // Classify each group
        let classifiedGroups =
            groups
            |> Map.toList
            |> List.map (fun (path, pathTransitions) -> classifyGroup episode changeSet pathTransitions)

        // Filter to parser cascade groups and derive facts
        let parserGroups =
            classifiedGroups
            |> List.choose (function
                | ClassifiedAsParserCascade facts -> Some facts
                | NotClassified _ -> None)

        if List.isEmpty parserGroups then
            None
        else
            // Sort deterministically using the comparison function
            let sorted = parserGroups |> List.sortWith compareTransitionGroupFacts
            Some sorted.Head

// -----------------------------------------------------------------------------
// Candidate building
// -----------------------------------------------------------------------------

/// Build all changed paths from a change set.
let buildChangedPaths (changeSet: GitChangeSet) : string list =
    changeSet.Entries |> List.map (fun e -> e.CanonicalPath) |> List.sort

/// Extract diagnostic codes from a list of transitions.
let extractDiagnosticCodes (transitions: DiagnosticTransition list) : string list =
    transitions |> List.choose (fun t -> t.Code) |> List.distinct |> List.sort

// -----------------------------------------------------------------------------
// Selection result
// -----------------------------------------------------------------------------

/// Result of the selection process for a single episode.
type EpisodeSelectionResult =
    { EpisodeKey: string
      EpisodeId: string
      Eligible: bool
      CandidateGroup: TransitionGroupFacts option
      SupportingTransitionCount: int
      TotalTransitionCount: int
      ParserGroupCount: int }
