module Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Transitions

open Circus.Tooling.FSharpDiagnostics.Domain
open Circus.Tooling.FSharpDiagnostics.OccurrenceIdentity
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Git

type FingerprintCount = {
    Fingerprint: string
    Severity: DiagnosticSeverity
    Subcategory: string option
    Code: string option
    SourcePath: string option
    ProjectPath: string option
    StartLine: int option
    StartColumn: int option
    EndLine: int option
    EndColumn: int option
    MessageNormalized: string
    Count: int
}

let private countByFingerprint
    (occurrences: DiagnosticOccurrence list)
    : FingerprintCount list =
    let dict = System.Collections.Generic.Dictionary<string, FingerprintCount>()
    for occ in occurrences do
        let fp = fingerprintFor occ
        match dict.TryGetValue fp with
        | true, existing ->
            dict.[fp] <- { existing with Count = existing.Count + 1 }
        | false, _ ->
            dict.[fp] <-
                { Fingerprint = fp
                  Severity = occ.Severity
                  Subcategory = occ.Subcategory
                  Code = occ.Code
                  SourcePath = occ.SourcePath
                  ProjectPath = occ.ProjectPath
                  StartLine = occ.Span.StartLine
                  StartColumn = occ.Span.StartColumn
                  EndLine = occ.Span.EndLine
                  EndColumn = occ.Span.EndColumn
                  MessageNormalized = occ.MessageNormalized
                  Count = 1 }
    dict.Values |> Seq.toList |> List.sortBy (fun f -> f.Fingerprint)

let classifyExactTransition (beforeCount: int) (afterCount: int) : ExactTransitionKind =
    match beforeCount, afterCount with
    | b, a when b > 0 && a = b -> PersistedSameCount
    | b, a when b > a && a > 0 -> PersistedCountDecreased
    | b, a when a > b && b > 0 -> PersistedCountIncreased
    | b, a when b > 0 && a = 0 -> EliminatedAfter
    | b, a when b = 0 && a > 0 -> IntroducedAfter
    | _ -> raise (System.ArgumentException "transitions: invalid counts")

let linkSourceChange
    (changeEntries: GitChangeEntry list)
    (declaredRelevant: string list)
    (sourcePath: string option)
    (projectPath: string option)
    : SourceLink =
    let buildSourceLink (kind: SourceLinkKind) (paths: string list) (reasons: string list) : SourceLink =
        { Kind = kind; Paths = paths; Reasons = reasons }
    let lookupKind (path: string) : GitChangeKind option =
        changeEntries
        |> List.tryPick (fun e -> if e.CanonicalPath = path then Some e.ChangeKind else None)
    let resolveSource () : SourceLinkKind option =
        match sourcePath with
        | None -> None
        | Some p ->
            match lookupKind p with
            | Some Modified -> Some(SourceFileModified p)
            | Some Added -> Some(SourceFileAdded p)
            | Some Deleted -> Some(SourceFileDeleted p)
            | Some TypeChanged -> Some(AmbiguousPathEvidence [ sprintf "source path %s has type change" p ])
            | None ->
                let declaredTouched =
                    declaredRelevant
                    |> List.filter (fun d -> hasAnyChange changeEntries d)
                if List.contains p declaredTouched then
                    Some(DeclaredRelevantPathChanged declaredTouched)
                else None
    let resolveProject () : SourceLinkKind option =
        match projectPath with
        | None -> None
        | Some p ->
            match lookupKind p with
            | Some Modified -> Some(ProjectFileModified p)
            | Some Added -> Some(ProjectFileModified p)
            | Some Deleted -> Some(ProjectFileModified p)
            | Some TypeChanged -> Some(AmbiguousPathEvidence [ sprintf "project path %s has type change" p ])
            | None ->
                let declaredTouched =
                    declaredRelevant
                    |> List.filter (fun d -> hasAnyChange changeEntries d)
                if List.contains p declaredTouched then
                    Some(DeclaredRelevantPathChanged declaredTouched)
                else None
    let sourceKind = resolveSource ()
    let projectKind = resolveProject ()
    match sourceKind, projectKind with
    | Some (SourceFileModified sp), Some (ProjectFileModified pp) ->
        buildSourceLink (SourceAndProjectModified (sp, pp)) [ sp; pp ] []
    | Some k, _ ->
        let ps = match sourcePath with Some p -> [ p ] | None -> []
        buildSourceLink k ps []
    | None, Some (ProjectFileModified pp) ->
        buildSourceLink (ProjectFileModified pp) [ pp ] []
    | None, Some (DeclaredRelevantPathChanged paths) ->
        buildSourceLink (DeclaredRelevantPathChanged paths) paths []
    | None, Some (AmbiguousPathEvidence rs) ->
        let ps = match projectPath with Some p -> [ p ] | None -> []
        buildSourceLink (AmbiguousPathEvidence rs) ps []
    | None, Some _ ->
        let touched = declaredRelevantTouched changeEntries declaredRelevant
        if not (List.isEmpty touched) then
            buildSourceLink (DeclaredRelevantPathChanged touched) touched []
        else
            buildSourceLink NoDirectPathChange [] []
    | None, None ->
        let touched = declaredRelevantTouched changeEntries declaredRelevant
        if not (List.isEmpty touched) then
            buildSourceLink (DeclaredRelevantPathChanged touched) touched []
        else
            buildSourceLink NoDirectPathChange [] []

let private pathDeleted (changes: GitChangeEntry list) (p: string option) : bool =
    match p with
    | Some v -> hasChangeOfKind changes Deleted v
    | None -> false

let classifyAssessment
    (transition: ExactTransitionKind)
    (compat: Compatibility)
    (sourceLink: SourceLink)
    (changeEntries: GitChangeEntry list)
    (projectPath: string option)
    : TransitionAssessment =
    let scopeOk = compat.Status = Compatible
    let afterScopeOk = not (pathDeleted changeEntries projectPath)
    let ambiguity = TransitionAssessment.Ambiguous
    let unassessable = TransitionAssessment.Unassessable
    let result =
        match transition with
        | EliminatedAfter ->
            if not scopeOk then unassessable
            else
                match sourceLink.Kind with
                | SourceFileDeleted _ -> TransitionAssessment.EliminatedBySourceRemoval
                | SourceFileModified _ when afterScopeOk -> TransitionAssessment.ObservedResolutionCandidate
                | _ -> unassessable
        | IntroducedAfter ->
            if not scopeOk then unassessable
            else
                match sourceLink.Kind with
                | SourceFileAdded _ -> TransitionAssessment.IntroducedWithSourceAddition
                | SourceFileModified _ when afterScopeOk -> TransitionAssessment.ObservedRegressionCandidate
                | SourceAndProjectModified _ when afterScopeOk -> TransitionAssessment.ObservedRegressionCandidate
                | ProjectFileModified _ when afterScopeOk -> TransitionAssessment.ObservedRegressionCandidate
                | _ -> unassessable
        | PersistedSameCount ->
            if scopeOk then TransitionAssessment.ExactPersistence else ambiguity
        | PersistedCountDecreased ->
            if scopeOk then TransitionAssessment.MultiplicityImprovementCandidate else unassessable
        | PersistedCountIncreased ->
            if scopeOk then TransitionAssessment.MultiplicityRegressionCandidate else unassessable
    result

type TransitionBuildResult = {
    Transitions: DiagnosticTransition list
    Counts: TransitionCounts
}

let emptyTransitionCounts () : TransitionCounts =
    { PersistedSameCount = 0
      PersistedCountDecreased = 0
      PersistedCountIncreased = 0
      EliminatedAfter = 0
      IntroducedAfter = 0
      ResolutionCandidates = 0
      RegressionCandidates = 0
      Unassessable = 0 }

let bumpKind (counts: TransitionCounts) (kind: ExactTransitionKind) : TransitionCounts =
    match kind with
    | PersistedSameCount -> { counts with PersistedSameCount = counts.PersistedSameCount + 1 }
    | PersistedCountDecreased -> { counts with PersistedCountDecreased = counts.PersistedCountDecreased + 1 }
    | PersistedCountIncreased -> { counts with PersistedCountIncreased = counts.PersistedCountIncreased + 1 }
    | EliminatedAfter -> { counts with EliminatedAfter = counts.EliminatedAfter + 1 }
    | IntroducedAfter -> { counts with IntroducedAfter = counts.IntroducedAfter + 1 }

let bumpAssessment (counts: TransitionCounts) (a: TransitionAssessment) : TransitionCounts =
    match a with
    | ObservedResolutionCandidate -> { counts with ResolutionCandidates = counts.ResolutionCandidates + 1 }
    | ObservedRegressionCandidate -> { counts with RegressionCandidates = counts.RegressionCandidates + 1 }
    | Unassessable -> { counts with Unassessable = counts.Unassessable + 1 }
    | _ -> counts

let buildTransitions
    (episodeId: string)
    (compat: Compatibility)
    (changeEntries: GitChangeEntry list)
    (declaredRelevant: string list)
    (beforeOccs: DiagnosticOccurrence list)
    (afterOccs: DiagnosticOccurrence list)
    : TransitionBuildResult =
    let beforeFps =
        countByFingerprint beforeOccs
        |> List.map (fun f -> f.Fingerprint, f)
        |> Map.ofList
    let afterFps =
        countByFingerprint afterOccs
        |> List.map (fun f -> f.Fingerprint, f)
        |> Map.ofList
    let allFps =
        let bkeys = beforeFps |> Map.toList |> List.map fst
        let akeys = afterFps |> Map.toList |> List.map fst
        Set.union (Set.ofList bkeys) (Set.ofList akeys)
        |> Set.toList
        |> List.sort
    let mutable transitions : DiagnosticTransition list = []
    let mutable counts = emptyTransitionCounts ()
    for fp in allFps do
        let b = (Map.tryFind fp beforeFps |> Option.map (fun f -> f.Count) |> Option.defaultValue 0)
        let a = (Map.tryFind fp afterFps |> Option.map (fun f -> f.Count) |> Option.defaultValue 0)
        let kind = classifyExactTransition b a
        let meta =
            match Map.tryFind fp afterFps with
            | Some f -> f
            | None -> Option.get (Map.tryFind fp beforeFps)
        let sourceLink =
            linkSourceChange changeEntries declaredRelevant meta.SourcePath meta.ProjectPath
        let assessment = classifyAssessment kind compat sourceLink changeEntries meta.ProjectPath
        counts <- bumpKind counts kind
        counts <- bumpAssessment counts assessment
        let span : SourceSpan =
            { StartLine = meta.StartLine
              StartColumn = meta.StartColumn
              EndLine = meta.EndLine
              EndColumn = meta.EndColumn }
        let t : DiagnosticTransition =
            { SchemaVersion = DiagnosticTransitionSchemaVersion
              EpisodeId = episodeId
              ExactFingerprint = fp
              TransitionKind = kind
              BeforeOccurrenceCount = b
              AfterOccurrenceCount = a
              Severity = meta.Severity
              Code = meta.Code
              MessageNormalized = meta.MessageNormalized
              SourcePath = meta.SourcePath
              ProjectPath = meta.ProjectPath
              Span = span
              Compatibility = compat
              SourceLink = sourceLink
              Assessment = assessment }
        transitions <- t :: transitions
    { Transitions = transitions |> List.sortBy (fun t -> t.EpisodeId, t.ExactFingerprint)
      Counts = counts }
