module Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Engine

open System.IO
open System.Text
open Circus.Tooling.FSharpDiagnostics.AtomicPublish
open Circus.Tooling.FSharpDiagnostics.Domain
open Circus.Tooling.FSharpDiagnostics.Hashing
open Circus.Tooling.FSharpDiagnostics.Inventory
open Circus.Tooling.FSharpDiagnostics.LegacyTextExtractor
open Circus.Tooling.FSharpDiagnostics.Manifest
open Circus.Tooling.FSharpDiagnostics.OccurrenceIdentity
open Circus.Tooling.FSharpDiagnostics.Paths
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Episodes
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Git
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Paths
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Serialization
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Transitions
open Circus.Tooling.FSharpDiagnostics.Verifier

type EpisodeEngineOptions = {
    GitRunOptions: GitRunOptions
}

let defaultEngineOptions : EpisodeEngineOptions =
    { GitRunOptions = defaultGitRunOptions }

let private lookupString (fields: (string * JsonValue) list) (name: string) : string option =
    fields
    |> List.tryPick (fun (k, v) ->
        if k = name then
            match v with
            | JsonString s -> Some s
            | _ -> None
        else None)

let private lookupOptString (fields: (string * JsonValue) list) (name: string) : string option =
    fields
    |> List.tryPick (fun (k, v) ->
        if k = name then
            match v with
            | JsonString s -> Some s
            | JsonNull -> None
            | _ -> None
        else None)

let private lookupStringList (fields: (string * JsonValue) list) (name: string) : string list =
    fields
    |> List.tryPick (fun (k, v) ->
        if k = name then
            match v with
            | JsonArray items ->
                Some (items |> List.choose (function JsonString s -> Some s | _ -> None))
            | _ -> None
        else None)
    |> Option.defaultValue []

/// Render a single declaration JSON file into a typed record.  Performs
/// schema-level validation and returns the list of issues found.
let parseDeclaration (json: string) (source: string option) : DeclarationValidation =
    try
        let v = parseJson json
        match v with
        | JsonObject fields ->
            let knownFields =
                [ "schema_version"; "episode_key"; "before_capture_id"; "after_capture_id"
                  "before_commit_oid"; "after_commit_oid"; "expected_before_tree_oid"
                  "expected_after_tree_oid"; "verification_evidence_ids"
                  "declared_relevant_paths"; "notes" ]
            let unknown =
                fields
                |> List.choose (fun (k, _) -> if List.contains k knownFields then None else Some k)
                |> List.map (fun k -> UnknownField k)
            let schemaVersion = lookupString fields "schema_version"
            let episodeKey = lookupString fields "episode_key"
            let beforeCap = lookupString fields "before_capture_id"
            let afterCap = lookupString fields "after_capture_id"
            let beforeOid = lookupString fields "before_commit_oid"
            let afterOid = lookupString fields "after_commit_oid"
            let expBefore = lookupOptString fields "expected_before_tree_oid"
            let expAfter = lookupOptString fields "expected_after_tree_oid"
            let verEvi = lookupStringList fields "verification_evidence_ids"
            let declared = lookupStringList fields "declared_relevant_paths"
            let notes = lookupOptString fields "notes"
            let mutable issues : DeclarationIssue list = unknown
            if schemaVersion <> Some RepairEpisodeDeclarationSchemaVersion then
                issues <- InvalidSchemaVersion :: issues
            if Option.isNone episodeKey then
                issues <- MissingField "episode_key" :: issues
            if Option.isNone beforeCap then
                issues <- MissingField "before_capture_id" :: issues
            if Option.isNone afterCap then
                issues <- MissingField "after_capture_id" :: issues
            if Option.isNone beforeOid then
                issues <- MissingField "before_commit_oid" :: issues
            if Option.isNone afterOid then
                issues <- MissingField "after_commit_oid" :: issues
            if List.isEmpty verEvi then
                issues <- MissingField "verification_evidence_ids" :: issues
            if List.isEmpty declared then
                issues <- MissingField "declared_relevant_paths" :: issues
            match episodeKey with
            | Some k when k.Length = 0 -> issues <- InvalidEpisodeKey :: issues
            | _ -> ()
            match beforeCap with
            | Some c when c.Length = 0 -> issues <- InvalidCaptureId :: issues
            | _ -> ()
            match afterCap with
            | Some c when c.Length = 0 -> issues <- InvalidCaptureId :: issues
            | _ -> ()
            match beforeOid with
            | Some o when o.Length <> 40 && o.Length <> 64 ->
                issues <- InvalidOidFormat(o, o.Length) :: issues
            | _ -> ()
            match afterOid with
            | Some o when o.Length <> 40 && o.Length <> 64 ->
                issues <- InvalidOidFormat(o, o.Length) :: issues
            | _ -> ()
            match expBefore with
            | Some o when o.Length <> 40 && o.Length <> 64 ->
                issues <- InvalidOidFormat(o, o.Length) :: issues
            | _ -> ()
            match expAfter with
            | Some o when o.Length <> 40 && o.Length <> 64 ->
                issues <- InvalidOidFormat(o, o.Length) :: issues
            | _ -> ()
            for p in declared do
                if System.IO.Path.IsPathRooted p then
                    issues <- AbsoluteDeclaredPath p :: issues
            match schemaVersion, episodeKey, beforeCap, afterCap, beforeOid, afterOid with
            | Some sv, Some ek, Some bc, Some ac, Some bo, Some ao when List.isEmpty issues ->
                let decl : RepairEpisodeDeclaration =
                    { SchemaVersion = sv
                      EpisodeKey = ek
                      BeforeCaptureId = bc
                      AfterCaptureId = ac
                      BeforeCommitOid = bo
                      AfterCommitOid = ao
                      ExpectedBeforeTreeOid = expBefore
                      ExpectedAfterTreeOid = expAfter
                      VerificationEvidenceIds = verEvi
                      DeclaredRelevantPaths = declared
                      Notes = notes }
                { Declaration = Some decl; Issues = []; Source = source }
            | _ ->
                { Declaration = None; Issues = issues |> List.rev; Source = source }
        | _ ->
            { Declaration = None; Issues = [ InvalidJson ]; Source = source }
    with
    | _ ->
        { Declaration = None; Issues = [ InvalidJson ]; Source = source }

let loadDeclarations (repoRoot: string) : (string * DeclarationValidation) list =
    enumerateDeclarationPaths repoRoot
    |> List.map (fun rel ->
        let fullPath = repoRelative repoRoot rel
        let text = readDeclaration fullPath
        rel, parseDeclaration text (Some rel))

let computeCompatibility (before: CaptureManifest) (after: CaptureManifest) : Compatibility =
    let mutable reasons : string list = []
    if before.CaptureKind <> after.CaptureKind then
        reasons <- (sprintf "capture_kind changed from %s to %s" before.CaptureKind after.CaptureKind) :: reasons
    match before.WorkingDirectory, after.WorkingDirectory with
    | Some b, Some a when canonicalise b <> canonicalise a ->
        reasons <- (sprintf "working_directory changed from %s to %s" (canonicalise b) (canonicalise a)) :: reasons
    | _, _ -> ()
    match before.DotnetSdkVersion, after.DotnetSdkVersion with
    | Some b, Some a when b <> a ->
        reasons <- (sprintf "dotnet_sdk_version changed from %s to %s" b a) :: reasons
    | _, _ -> ()
    match before.MsbuildVersion, after.MsbuildVersion with
    | Some b, Some a when b <> a ->
        reasons <- (sprintf "msbuild_version changed from %s to %s" b a) :: reasons
    | _, _ -> ()
    match before.FsharpCompilerVersion, after.FsharpCompilerVersion with
    | Some b, Some a when b <> a ->
        reasons <- (sprintf "fsharp_compiler_version changed from %s to %s" b a) :: reasons
    | _, _ -> ()
    match before.OperatingSystem, after.OperatingSystem with
    | Some b, Some a when b <> a ->
        reasons <- (sprintf "operating_system changed from %s to %s" b a) :: reasons
    | _, _ -> ()
    match before.Architecture, after.Architecture with
    | Some b, Some a when b <> a ->
        reasons <- (sprintf "architecture changed from %s to %s" b a) :: reasons
    | _, _ -> ()
    match before.Culture, after.Culture with
    | Some b, Some a when b <> a ->
        reasons <- (sprintf "culture changed from %s to %s" b a) :: reasons
    | _, _ -> ()
    let required =
        [ "command"; "working_directory"; "dotnet_sdk_version"; "msbuild_version"
          "fsharp_compiler_version"; "operating_system"; "architecture"; "culture" ]
    let isMissing (m: CaptureManifest) (field: string) : bool =
        match field with
        | "command" -> m.Command.IsNone
        | "working_directory" -> m.WorkingDirectory.IsNone
        | "dotnet_sdk_version" -> m.DotnetSdkVersion.IsNone
        | "msbuild_version" -> m.MsbuildVersion.IsNone
        | "fsharp_compiler_version" -> m.FsharpCompilerVersion.IsNone
        | "operating_system" -> m.OperatingSystem.IsNone
        | "architecture" -> m.Architecture.IsNone
        | "culture" -> m.Culture.IsNone
        | _ -> false
    let missing =
        required
        |> List.filter (fun f -> isMissing before f || isMissing after f)
    if not (List.isEmpty reasons) then
        { Status = Incompatible
          Reasons = reasons
          MissingFields = [] }
    elif not (List.isEmpty missing) then
        { Status = Unknown
          Reasons = []
          MissingFields = missing }
    else
        compatible

let private afterScopeOk (changes: GitChangeEntry list) (projectPath: string option) : bool =
    match projectPath with
    | None -> true
    | Some p -> not (hasChangeOfKind changes Deleted p)

let private qualification
    (compat: Compatibility)
    (changes: GitChangeEntry list)
    (afterScopeOk: bool)
    (verificationLevel: VerificationLevel)
    (transitions: DiagnosticTransition list)
    : EpisodeQualification =
    let mutable reasons : string list = []
    if compat.Status = Incompatible then
        reasons <- "incompatible before/after scope" :: reasons
    if not afterScopeOk then
        reasons <- "after-scope project path deleted" :: reasons
    if List.isEmpty changes && List.isEmpty transitions then
        reasons <- "no changes and no diagnostic transitions" :: reasons
    match verificationLevel with
    | TransitionObserved ->
        reasons <- "verification level is transition_observed" :: reasons
    | _ -> ()
    if List.isEmpty reasons then
        { Status = Qualified; Reasons = [] }
    elif verificationLevel = TransitionObserved || verificationLevel = SourceLinked then
        { Status = Ambiguous; Reasons = reasons }
    else
        { Status = QualifiedWithLimitations; Reasons = reasons }

let verificationLevelFromEvidence (items: VerificationEvidence list) : VerificationLevel =
    let mutable anyPass = false
    let mutable hasGate = false
    let mutable hasTest = false
    let mutable hasBuild = false
    for e in items do
        if e.Status = VerificationStatus.Pass then
            anyPass <- true
            match e.Kind with
            | FocusedGate -> hasGate <- true
            | FocusedTest -> hasTest <- true
            | Build -> hasBuild <- true
            | _ -> ()
    if hasGate then FocusedGateVerified
    elif hasTest then FocusedTestVerified
    elif hasBuild then BuildVerified
    elif anyPass then SourceLinked
    else TransitionObserved

type EpisodeEngineResult = {
    Summary: RepairEpisodeSummary
    RepairEpisodes: RepairEpisode list
    Transitions: DiagnosticTransition list
    ChangeSets: GitChangeSet list
    Verification: VerificationEvidence list
    Outcome: PublishOutcome
    Declarations: (string * DeclarationValidation) list
}

let private buildEpisodeId
    (beforeCap: string)
    (afterCap: string)
    (beforeTree: string)
    (afterTree: string)
    (changeSetId: string)
    : string =
    computeEpisodeId beforeCap afterCap beforeTree afterTree changeSetId

let private verificationIdFor
    (cmd: string)
    (episodeId: string)
    (kind: VerificationKind)
    : string =
    let sb = StringBuilder()
    let prefix (s: string) =
        sb.Append(s.Length.ToString("x8", System.Globalization.CultureInfo.InvariantCulture)) |> ignore
        sb.Append(':') |> ignore
        sb.Append s |> ignore
    prefix VerificationEvidenceSchemaVersion
    prefix episodeId
    prefix (verificationKindToken kind)
    prefix cmd
    sha256OfUtf8 (sb.ToString())

let runEpisodeEngine
    (repoRoot: string)
    (options: EpisodeEngineOptions)
    : EpisodeEngineResult =
    clearObjectFormatCache ()

    let declarations = loadDeclarations repoRoot

    let keyCounts =
        declarations
        |> List.choose (fun (_, d) -> d.Declaration |> Option.map (fun d -> d.EpisodeKey))
        |> List.groupBy id
        |> List.map (fun (k, xs) -> k, List.length xs)
        |> Map.ofList
    let duplicateKeys =
        keyCounts
        |> Map.filter (fun _ c -> c > 1)
        |> Map.keys
        |> Seq.toList

    let validDeclarations =
        declarations
        |> List.choose (fun (_, d) -> d.Declaration)
        |> List.filter (fun d -> not (List.contains d.EpisodeKey duplicateKeys))

    let invalidCount =
        declarations
        |> List.filter (fun (_, d) -> not (List.isEmpty d.Issues))
        |> List.length

    let captures =
        validDeclarations
        |> List.map (fun d -> d.BeforeCaptureId, d.AfterCaptureId)
        |> List.collect (fun (b, a) -> [ b; a ])
        |> List.distinct
        |> List.map (fun id -> id, tryLoadCapture repoRoot id)
        |> Map.ofList

    let missingCaptures =
        captures
        |> Map.filter (fun _ v -> v.IsNone)
        |> Map.keys
        |> Seq.toList

    let mutable transitions : DiagnosticTransition list = []
    let mutable episodes : RepairEpisode list = []
    let mutable changeSets : GitChangeSet list = []
    let mutable evidence : VerificationEvidence list = []
    let mutable missingGitObjects = 0
    let mutable duplicateIds = 0
    let episodeIds = System.Collections.Generic.HashSet<string>()

    for decl in validDeclarations do
        if List.contains decl.BeforeCaptureId missingCaptures
           || List.contains decl.AfterCaptureId missingCaptures then
            ()
        else
            try
                let identity =
                    resolveGitIdentity repoRoot options.GitRunOptions decl.BeforeCommitOid decl.AfterCommitOid
                let beforeCap =
                    Map.find decl.BeforeCaptureId captures |> Option.get
                let afterCap =
                    Map.find decl.AfterCaptureId captures |> Option.get
                let changeSet =
                    buildChangeSet repoRoot options.GitRunOptions identity.ObjectFormat
                        identity.BeforeTreeOid identity.AfterTreeOid
                changeSets <- changeSet :: changeSets
                let compat = computeCompatibility beforeCap.Manifest afterCap.Manifest
                let projectPath = afterCap.Manifest.WorkingDirectory
                let afterOk = afterScopeOk changeSet.Entries projectPath
                let verificationLevel = verificationLevelFromEvidence []
                let episodeId =
                    buildEpisodeId decl.BeforeCaptureId decl.AfterCaptureId
                        identity.BeforeTreeOid identity.AfterTreeOid changeSet.ChangeSetId
                if not (episodeIds.Add episodeId) then
                    duplicateIds <- duplicateIds + 1
                let qual =
                    qualification compat changeSet.Entries afterOk verificationLevel transitions
                let contractBefore = commandContract beforeCap.Manifest
                let contractAfter = commandContract afterCap.Manifest
                let counts = emptyTransitionCounts ()
                let episode : RepairEpisode =
                    { SchemaVersion = RepairEpisodeSchemaVersion
                      EpisodeId = episodeId
                      EpisodeKey = decl.EpisodeKey
                      BeforeCaptureId = decl.BeforeCaptureId
                      AfterCaptureId = decl.AfterCaptureId
                      BeforeCommitOid = identity.BeforeCommitOid
                      BeforeTreeOid = identity.BeforeTreeOid
                      AfterCommitOid = identity.AfterCommitOid
                      AfterTreeOid = identity.AfterTreeOid
                      CommitRange = identity.CommitRange
                      ChangeSetId = changeSet.ChangeSetId
                      CommandContractBefore = contractBefore
                      CommandContractAfter = contractAfter
                      Compatibility = compat
                      TransitionCounts = counts
                      VerificationLevel = verificationLevel
                      VerificationEvidenceIds = decl.VerificationEvidenceIds
                      Qualification = qual }
                episodes <- episode :: episodes
                let beforeCommitOk =
                    match beforeCap.Manifest.RepositoryCommitOid with
                    | Some c -> c = identity.BeforeCommitOid
                    | None -> true
                let afterCommitOk =
                    match afterCap.Manifest.RepositoryCommitOid with
                    | Some c -> c = identity.AfterCommitOid
                    | None -> true
                if not beforeCommitOk || not afterCommitOk then
                    missingGitObjects <- missingGitObjects + 1
            with
            | GitIdentityFailure _ -> missingGitObjects <- missingGitObjects + 1
            | GitObjectFormatFailure _ -> missingGitObjects <- missingGitObjects + 1
            | GitChangeParseFailure _ -> missingGitObjects <- missingGitObjects + 1

    let sortedEpisodes = episodes |> List.sortBy (fun e -> e.EpisodeId)
    let sortedChangeSets = changeSets |> List.sortBy (fun cs -> cs.ChangeSetId)
    let sortedTransitions = transitions |> List.sortBy (fun t -> t.EpisodeId, t.ExactFingerprint)
    let sortedEvidence = evidence |> List.sortBy (fun e -> e.EvidenceId)

    let episodesBody =
        sortedEpisodes
        |> List.map renderRepairEpisode
        |> String.concat "\n"
    let transitionsBody =
        sortedTransitions
        |> List.map renderDiagnosticTransition
        |> String.concat "\n"
    let changeSetsBody =
        sortedChangeSets
        |> List.map renderGitChangeSet
        |> String.concat "\n"
    let evidenceBody =
        sortedEvidence
        |> List.map renderVerificationEvidence
        |> String.concat "\n"

    let summary =
        { SchemaVersion = RepairEpisodeSummarySchemaVersion
          DeclarationsTotal = List.length declarations
          ValidDeclarations = List.length validDeclarations
          InvalidDeclarations = invalidCount
          MissingCaptures = List.length missingCaptures
          MissingGitObjects = missingGitObjects
          DuplicateEpisodeKeys = List.length duplicateKeys
          DuplicateEpisodeIds = duplicateIds
          EpisodesTotal = List.length sortedEpisodes
          EpisodesQualified = sortedEpisodes |> List.filter (fun e -> e.Qualification.Status = Qualified) |> List.length
          EpisodesQualifiedWithLimitations = sortedEpisodes |> List.filter (fun e -> e.Qualification.Status = QualifiedWithLimitations) |> List.length
          EpisodesAmbiguous = sortedEpisodes |> List.filter (fun e -> e.Qualification.Status = Ambiguous) |> List.length
          EpisodesRejected = sortedEpisodes |> List.filter (fun e -> e.Qualification.Status = Rejected) |> List.length
          ChangeSetsTotal = List.length sortedChangeSets
          TransitionsTotal = List.length sortedTransitions
          PersistedSameCount = 0
          PersistedCountDecreased = 0
          PersistedCountIncreased = 0
          EliminatedAfter = 0
          IntroducedAfter = 0
          ResolutionCandidates = 0
          RegressionCandidates = 0
          UnassessableTransitions = 0
          VerificationEvidenceTotal = List.length sortedEvidence }
    let summaryBody = renderRepairEpisodeSummary summary

    let normalizedDir = repoRelative repoRoot normalizedSubdir
    if not (Directory.Exists normalizedDir) then
        Directory.CreateDirectory normalizedDir |> ignore

    let files =
        [
            { CanonicalFileName = repairEpisodesFile; Body = episodesBody }
            { CanonicalFileName = diagnosticTransitionsFile; Body = transitionsBody }
            { CanonicalFileName = gitChangeSetsFile; Body = changeSetsBody }
            { CanonicalFileName = repairEpisodeSummaryFile; Body = summaryBody }
            { CanonicalFileName = verificationEvidenceFile; Body = evidenceBody }
        ]
    let outcome = publish normalizedDir true false files
    { Summary = summary
      RepairEpisodes = sortedEpisodes
      Transitions = sortedTransitions
      ChangeSets = sortedChangeSets
      Verification = sortedEvidence
      Outcome = outcome
      Declarations = declarations }

type VerificationIssue =
    | EpisodeIdMismatch
    | ChangeSetIdMismatch
    | TransitionCountMismatch
    | TransitionEpisodeIdMismatch
    | FileMissing of path: string
    | HashMismatch of path: string
    | ManifestMissing of path: string
    | SummaryMismatch
    | DeclarationInvalid of issues: int

type VerificationResult = {
    Issues: VerificationIssue list
    RepairEpisodesValidated: int
    TransitionsValidated: int
}

let verifyPipeline
    (repoRoot: string)
    (options: EpisodeEngineOptions)
    : VerificationResult =
    let r = runEpisodeEngine repoRoot options
    let mutable issues : VerificationIssue list = []
    let expectedPaths =
        [ repairEpisodesCanonicalPath
          diagnosticTransitionsCanonicalPath
          gitChangeSetsCanonicalPath
          repairEpisodeSummaryCanonicalPath
          verificationEvidenceCanonicalPath ]
    for p in expectedPaths do
        let full = repoRelative repoRoot p
        if not (File.Exists full) then
            issues <- FileMissing p :: issues
    let invalidDecls =
        r.Declarations
        |> List.filter (fun (_, d) -> not (List.isEmpty d.Issues))
        |> List.length
    if invalidDecls > 0 then
        issues <- DeclarationInvalid invalidDecls :: issues
    { Issues = issues
      RepairEpisodesValidated = r.RepairEpisodes |> List.length
      TransitionsValidated = r.Transitions |> List.length }

let publicChangeSetId
    (beforeTree: string)
    (afterTree: string)
    (entries: GitChangeEntry list)
    : string =
    computeChangeSetIdentity beforeTree afterTree entries

let publicEpisodeId
    (beforeCap: string)
    (afterCap: string)
    (beforeTree: string)
    (afterTree: string)
    (changeSetId: string)
    : string =
    buildEpisodeId beforeCap afterCap beforeTree afterTree changeSetId

let publicEvidenceId
    (cmd: string)
    (episodeId: string)
    (kind: VerificationKind)
    : string =
    verificationIdFor cmd episodeId kind
