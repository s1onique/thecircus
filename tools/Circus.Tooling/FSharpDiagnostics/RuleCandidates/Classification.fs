module Circus.Tooling.FSharpDiagnostics.RuleCandidates.Classification

// Parser-family classification for ParserCascadeRepair candidates.

open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Domain

// Classification failure reasons
type ClassificationFailure =
    | InsufficientTransitions of count: int
    | MissingRequiredCode of requiredCode: string
    | NonParserCodeFound of code: string
    | PathNotInChangeSet of path: string
    | PathDeletedOnly of path: string
    | UnsupportedTransitionAssessment of assessment: string

// Classification result
type ClassificationResult =
    | ClassifiedAsParserCascade of groupFacts: TransitionGroupFacts
    | NotClassified of reason: ClassificationFailure

// Check if transition qualifies as repair-supporting
let isRepairSupportingTransition
    (episode: RepairEpisode)
    (changeSet: GitChangeSet)
    (transition: DiagnosticTransition)
    : bool =
    if transition.EpisodeId <> episode.EpisodeId then
        false
    elif Option.isNone transition.SourcePath then
        false
    elif
        not (List.exists (fun (e: GitChangeEntry) -> e.CanonicalPath = transition.SourcePath.Value) changeSet.Entries)
    then
        false
    else
        let entry =
            List.find (fun (e: GitChangeEntry) -> e.CanonicalPath = transition.SourcePath.Value) changeSet.Entries

        if entry.ChangeKind = GitChangeKind.Deleted then
            false
        elif transition.BeforeOccurrenceCount <= 0 then
            false
        elif transition.AfterOccurrenceCount > 0 then
            false
        elif transition.TransitionKind = ExactTransitionKind.IntroducedAfter then
            false
        elif transition.Assessment = TransitionAssessment.Unassessable then
            false
        elif transition.Assessment = TransitionAssessment.Ambiguous then
            false
        else
            true

// Check if path qualifies as repair-supporting
let isRepairSupportingPath (changeSet: GitChangeSet) (transition: DiagnosticTransition) : bool =
    match transition.SourcePath with
    | None -> false
    | Some path ->
        match List.tryFind (fun (e: GitChangeEntry) -> e.CanonicalPath = path) changeSet.Entries with
        | None -> false
        | Some entry -> entry.ChangeKind <> GitChangeKind.Deleted

// Classify a transition group for ParserCascadeRepair eligibility
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
        // Requirement 2: All transitions must share the same path
        let paths =
            transitions
            |> List.choose (fun (t: DiagnosticTransition) -> t.SourcePath)
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
            // Requirement 4: Path must exist in change set
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
                        // Derive group facts
                        let distinctCodes = codes |> List.distinct |> List.sort

                        let earliestLine =
                            transitions
                            |> List.choose (fun (t: DiagnosticTransition) -> t.Span.StartLine)
                            |> function
                                | [] -> None
                                | lines -> Some(List.min lines)

                        let transitionIds =
                            transitions |> List.map (fun (t: DiagnosticTransition) -> t.ExactFingerprint)

                        ClassifiedAsParserCascade
                            { Path = path
                              TransitionCount = transitions.Length
                              DiagnosticCodes = distinctCodes
                              EarliestLine = earliestLine
                              TransitionIds = transitionIds }
