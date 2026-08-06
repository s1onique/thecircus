module Circus.Tooling.FSharpDiagnostics.RuleCandidates.Classification

// =============================================================================
// Parser-family classification for ParserCascadeRepair candidates.
// =============================================================================
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-EXTRACTION01-CORRECTION01
//
// Authority invariants enforced here:
//   * Only positive transition assessments may contribute to a candidate.
//     Unassessable, Ambiguous, RegressionObservation, and
//     MultiplicityWorseningObservation transitions must never be counted as
//     positive support - even when they look parser-family.
//   * Unassessable / ambiguous / regression transitions remain attached as
//     contextual or counterevidence observations.  They must be partitioned
//     from the supporting transitions.
//   * Repository path normalization is delegated to the shared authority.

open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain
open Circus.Tooling.FSharpDiagnostics.RepoPaths
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Domain

// -----------------------------------------------------------------------------
// Classification failure reasons
// -----------------------------------------------------------------------------

type ClassificationFailure =
    | InsufficientTransitions of count: int
    | MissingRequiredCode of requiredCode: string
    | NonParserCodeFound of code: string
    | PathNotInChangeSet of path: string
    | PathDeletedOnly of path: string
    | UnsupportedTransitionAssessment of assessment: string
    | NoPositiveSupportingTransition of path: string

// -----------------------------------------------------------------------------
// Classification result
// -----------------------------------------------------------------------------

type ClassificationResult =
    | ClassifiedAsParserCascade of groupFacts: TransitionGroupFacts
    | NotClassified of reason: ClassificationFailure

// -----------------------------------------------------------------------------
// Transition partition
// -----------------------------------------------------------------------------

/// What a single transition contributes to the candidate.  The partition is
/// computed independently of parser-family heuristics.  Parser-family status
/// may add the transition to the candidate group facts, but it never
/// promotes an Unassessable transition to positive support.
type TransitionRole =
    | Supporting
    | Context
    | Counterevidence
    | Excluded

/// Classify the role of a transition relative to the candidate.  This is the
/// single source of truth for transition assessment authority.
let classifyTransitionRole (t: DiagnosticTransition) : TransitionRole =
    if t.TransitionKind = ExactTransitionKind.IntroducedAfter then
        // IntroducedAfter is a structural exclusion: the diagnostic did not
        // exist before the change.
        Excluded
    elif isPositiveTransitionAssessment t.Assessment then
        Supporting
    elif isCounterevidenceTransitionAssessment t.Assessment then
        Counterevidence
    elif isContextTransitionAssessment t.Assessment then
        Context
    else
        Excluded

// -----------------------------------------------------------------------------
// Repair-supporting transition predicate
// -----------------------------------------------------------------------------

/// A transition is repair-supporting only when its role is `Supporting`.
/// Path scoping is checked but assessment authority is the deciding factor.
let isRepairSupportingTransition
    (episode: RepairEpisode)
    (changeSet: GitChangeSet)
    (transition: DiagnosticTransition)
    : bool =
    if transition.EpisodeId <> episode.EpisodeId then
        false
    elif Option.isNone transition.SourcePath then
        false
    else
        let normalizedPath = normalizeRepositoryPath transition.SourcePath.Value

        if
            not (List.exists (fun (e: GitChangeEntry) -> e.CanonicalPath = normalizedPath) changeSet.Entries)
        then
            false
        else
            let entry =
                List.find (fun (e: GitChangeEntry) -> e.CanonicalPath = normalizedPath) changeSet.Entries

            if entry.ChangeKind = GitChangeKind.Deleted then
                false
            elif transition.BeforeOccurrenceCount <= 0 then
                false
            elif transition.AfterOccurrenceCount > 0 then
                false
            elif transition.TransitionKind = ExactTransitionKind.IntroducedAfter then
                false
            elif classifyTransitionRole transition <> Supporting then
                false
            else
                true

// -----------------------------------------------------------------------------
// Path scoping predicate
// -----------------------------------------------------------------------------

/// Check if a transition's path belongs to the change set and was not
/// deleted.  This predicate is purely geometric; assessment authority lives
/// in `isRepairSupportingTransition`.
let isRepairSupportingPath (changeSet: GitChangeSet) (transition: DiagnosticTransition) : bool =
    match transition.SourcePath with
    | None -> false
    | Some path ->
        let normalizedPath = normalizeRepositoryPath path
        match List.tryFind (fun (e: GitChangeEntry) -> e.CanonicalPath = normalizedPath) changeSet.Entries with
        | None -> false
        | Some entry -> entry.ChangeKind <> GitChangeKind.Deleted

// -----------------------------------------------------------------------------
// Group classification
// -----------------------------------------------------------------------------

/// Classify a transition group for ParserCascadeRepair eligibility.
///
/// Returns `ClassifiedAsParserCascade` only when:
///   * Every transition belongs to the same episode.
///   * Every transition shares one normalized path.
///   * Every diagnostic code is parser-family.
///   * The path appears in the change set and was not deleted.
///   * At least two transitions are present.
///   * The group contains at least one `FS0010` or `FS3118` diagnostic.
///   * The group contains at least one positively assessed transition.
let classifyGroup
    (episode: RepairEpisode)
    (changeSet: GitChangeSet)
    (transitions: DiagnosticTransition list)
    : ClassificationResult =

    // Requirement 1: All transitions must belong to the same episode
    let allSameEpisode =
        List.forall (fun (t: DiagnosticTransition) -> t.EpisodeId = episode.EpisodeId) transitions

    if not allSameEpisode then
        NotClassified(UnsupportedTransitionAssessment "mixed_episode_transitions")
    else
        // Requirement 2: All transitions must share the same normalized path
        let paths =
            transitions
            |> List.choose (fun (t: DiagnosticTransition) -> t.SourcePath)
            |> List.map normalizeRepositoryPath
            |> List.distinct

        if paths.Length <> 1 then
            NotClassified(UnsupportedTransitionAssessment "multiple_paths")
        else
            let path = paths.Head

            // Requirement 3: Every diagnostic code must belong to parser family
            let nonParserCodes =
                transitions
                |> List.choose (fun (t: DiagnosticTransition) -> t.Code)
                |> List.filter (fun c -> not (isParserDiagnostic c))

            if not (List.isEmpty nonParserCodes) then
                NotClassified(NonParserCodeFound nonParserCodes.Head)
            // Requirement 4: Normalized path must exist in change set
            elif not (List.exists (fun (e: GitChangeEntry) -> e.CanonicalPath = path) changeSet.Entries) then
                NotClassified(PathNotInChangeSet path)
            else
                // Requirement 5 & 6: Path must not be deleted
                let entry =
                    List.find (fun (e: GitChangeEntry) -> e.CanonicalPath = path) changeSet.Entries

                if entry.ChangeKind = GitChangeKind.Deleted then
                    NotClassified(PathDeletedOnly path)
                // Requirement 7: At least two transitions
                elif transitions.Length < 2 then
                    NotClassified(InsufficientTransitions transitions.Length)
                else
                    // Requirement 8: At least one FS0010 or FS3118
                    let codes = transitions |> List.choose (fun (t: DiagnosticTransition) -> t.Code)

                    let hasRequiredCode =
                        List.exists ((=) "FS0010") codes || List.exists ((=) "FS3118") codes

                    if not hasRequiredCode then
                        NotClassified(MissingRequiredCode "FS0010 or FS3118")
                    else
                        // Requirement 9: at least one positive assessment
                        let hasPositive =
                            transitions
                            |> List.exists (fun t -> classifyTransitionRole t = Supporting)

                        if not hasPositive then
                            NotClassified(NoPositiveSupportingTransition path)
                        else
                            // Derive group facts
                            let distinctCodes = codes |> List.distinct |> List.sort

                            let earliestLine =
                                transitions
                                |> List.choose (fun (t: DiagnosticTransition) -> t.Span.StartLine)
                                |> function
                                    | [] -> None
                                    | lines -> Some(List.min lines)

                            let transitionIds =
                                transitions
                                |> List.map (fun (t: DiagnosticTransition) -> t.ExactFingerprint)

                            ClassifiedAsParserCascade
                                { Path = path
                                  TransitionCount = transitions.Length
                                  DiagnosticCodes = distinctCodes
                                  EarliestLine = earliestLine
                                  TransitionIds = transitionIds }

// -----------------------------------------------------------------------------
// Partition construction
// -----------------------------------------------------------------------------

/// Build the supporting / context / counterevidence partition for a
/// classified transition group.  The classifier never injects unassessable
/// or regression transitions into the supporting set.
let buildPartition
    (gf: TransitionGroupFacts)
    (allTransitions: DiagnosticTransition list)
    : RuleCandidateTransitionPartition =
    let inGroup =
        allTransitions
        |> List.filter (fun t -> List.contains t.ExactFingerprint gf.TransitionIds)

    let supporting =
        inGroup
        |> List.filter (fun t -> classifyTransitionRole t = Supporting)
        |> List.map (fun t -> t.ExactFingerprint)
        |> List.sort

    let counterevidence =
        inGroup
        |> List.filter (fun t -> classifyTransitionRole t = Counterevidence)
        |> List.map (fun t -> t.ExactFingerprint)
        |> List.sort

    let context =
        inGroup
        |> List.filter (fun t -> classifyTransitionRole t = Context)
        |> List.map (fun t -> t.ExactFingerprint)
        |> List.sort

    { SupportingTransitionIds = supporting
      ContextTransitionIds = context
      CounterevidenceTransitionIds = counterevidence }
