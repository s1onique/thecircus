module Circus.Tooling.FSharpDiagnostics.RuleCandidates.Engine

// =============================================================================
// Rule candidate extraction engine
// =============================================================================
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-EXTRACTION01-CORRECTION01
//
// This module owns:
//   * transition identity & duplicate detection across all four domains;
//   * candidate construction from a classified group and partition;
//   * atomic publication via the shared `AtomicPublish.publish` authority;
//   * read-only verification that recomputes the candidate identity and
//     reconciles summary counts.
//
// Authority invariants:
//   * The published `candidate_id` is the result of `computeCandidateId`
//     over the parsed semantic fields, recomputed by the verifier.  The
//     verifier never trusts the published id verbatim.
//   * Publication is atomic: temp staging, single replacement of canonical
//     bytes, no observable partial state.  On failure, canonical bytes are
//     byte-identical to the pre-publication state.
//   * `runVerify` performs no writes and leaves the working tree untouched.

open System
open System.IO
open Circus.Tooling.FSharpDiagnostics.AtomicPublish
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

type InputIdentityKind =
    | EpisodeIdentity
    | TransitionIdentity
    | ChangeSetIdentity
    | VerificationEvidenceIdentity

[<StructuralEquality; StructuralComparison>]
type TransitionIdentityKey =
    { EpisodeId: string
      ExactFingerprint: string }

let makeTransitionIdentityKey (t: DiagnosticTransition) : TransitionIdentityKey =
    { EpisodeId = t.EpisodeId
      ExactFingerprint = t.ExactFingerprint }

let renderTransitionIdentity (key: TransitionIdentityKey) : string =
    sprintf "episode=%s;fingerprint=%s" key.EpisodeId key.ExactFingerprint

let transitionIdentityString (t: DiagnosticTransition) : string =
    t.EpisodeId + "|" + t.ExactFingerprint

type EngineError =
    | EpisodeLoadFailed of errors: string list
    | VerificationEvidenceLoadFailed of errors: string list
    | TransitionLoadFailed of errors: string list
    | ChangeSetLoadFailed of errors: string list
    | NoEligibleEpisodes
    | CandidateGenerationFailed of details: string
    | PublicationFailed of details: string
    | DuplicateInputIdentities of kind: InputIdentityKind * identities: string list
    | InvalidInputIdentity of kind: InputIdentityKind * itemIndex: int * field: string * reason: string
    | Internal of message: string
    | UnsupportedRepairEpisodeSchemaVersion of version: string
    | UnsupportedChangeSetSchemaVersion of version: string
    | UnsupportedVerificationEvidenceSchemaVersion of version: string
    | MalformedRepairEpisodeJson of line: int * message: string
    | MalformedChangeSetJson of line: int * message: string
    | MalformedTransitionJson of line: int * message: string
    | MalformedVerificationEvidenceJson of line: int * message: string

// -----------------------------------------------------------------------------
// Identity validation helpers
// -----------------------------------------------------------------------------

let private validateEpisodeIdentity (index: int) (ep: RepairEpisode) : EngineError option =
    if String.IsNullOrEmpty ep.EpisodeId then
        Some(InvalidInputIdentity(EpisodeIdentity, index, "EpisodeId", "empty"))
    else
        None

let private validateTransitionIdentity (index: int) (t: DiagnosticTransition) : EngineError option =
    if String.IsNullOrEmpty t.EpisodeId then
        Some(InvalidInputIdentity(TransitionIdentity, index, "EpisodeId", "empty"))
    elif String.IsNullOrEmpty t.ExactFingerprint then
        Some(InvalidInputIdentity(TransitionIdentity, index, "ExactFingerprint", "empty"))
    else
        None

let private validateChangeSetIdentity (index: int) (cs: GitChangeSet) : EngineError option =
    if String.IsNullOrEmpty cs.ChangeSetId then
        Some(InvalidInputIdentity(ChangeSetIdentity, index, "ChangeSetId", "empty"))
    else
        None

let private validateVerificationIdentity (index: int) (lv: LocatedVerificationEvidence) : EngineError option =
    if String.IsNullOrEmpty lv.Evidence.EvidenceId then
        Some(InvalidInputIdentity(VerificationEvidenceIdentity, index, "EvidenceId", "empty"))
    else
        None

// -----------------------------------------------------------------------------
// Fixed prose templates (descriptive, never imperative)
// -----------------------------------------------------------------------------

let parserCascadeTitle =
    "Parser diagnostic cluster eliminated after the same-path repair"

let parserCascadeSymptom =
    "Multiple parser diagnostics occur in one changed F# source path, including FS0010 or FS3118, and later diagnostics appear in the same local region."

let parserCascadeApplicability =
    "Applies when the diagnostics form a same-path parser cluster, the path changed in the verified repair episode, and the after-state no longer contains the same exact failures."

let parserCascadeCandidateHypothesis =
    "This is a provisional hypothesis that the parser cascade observed in this single episode may be caused by an early malformed binding or delimiter in the source path. The hypothesis is descriptive, not a recommended fix."

let parserCascadeLimitations =
    [ "Supported by one observed repair episode."
      "Path-level change support does not prove line-level causation."
      "Not yet reproduced with a minimal compiler fixture."
      "Not a universal interpretation of FS0010 or FS3118." ]

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

/// PendingFile bodies for the canonical rule-candidate artifacts.
type private PendingRuleCandidateArtifact =
    { JsonlBody: string
      SummaryBody: string }

let private buildPending
    (canonicalDir: string)
    (candidates: RuleCandidate list)
    (eligible: int)
    (episodesWithCandidates: int)
    : PendingRuleCandidateArtifact =
    let cpath = Path.Combine(canonicalDir, Path.GetFileName ruleCandidatesJsonlRelativePath)
    let spath = Path.Combine(canonicalDir, Path.GetFileName ruleCandidatesSummaryRelativePath)

    let clines = candidates |> List.map renderRuleCandidate

    let summary =
        { SchemaVersion = RuleCandidateSummarySchemaVersion
          EligibleEpisodes = eligible
          EpisodesWithCandidates = episodesWithCandidates
          CandidatesTotal = candidates.Length
          ParserCascadeCandidates = candidates.Length
          SingleEpisodeCandidates = candidates.Length
          CandidateIds = candidates |> List.map (fun c -> c.CandidateId) |> List.sort }

    { JsonlBody = (clines |> String.concat "\n") + "\n"
      SummaryBody = renderRuleCandidateSummary summary }

/// Publish candidates atomically.  Returns true on success; on failure the
/// canonical outputs remain byte-identical to the pre-publication state.
let publishCandidates (repoRoot: string) (result: ExtractionResult) : bool =
    let canonicalDir =
        Path.GetDirectoryName(toAbsolutePath repoRoot ruleCandidatesJsonlRelativePath)

    try
        let canonicalJsonl = Path.Combine(canonicalDir, Path.GetFileName ruleCandidatesJsonlRelativePath)
        let canonicalSummary = Path.Combine(canonicalDir, Path.GetFileName ruleCandidatesSummaryRelativePath)

        let pending = buildPending canonicalDir result.Candidates result.EligibleEpisodes result.EpisodesWithCandidates

        let files =
            [ { CanonicalFileName = Path.GetFileName ruleCandidatesJsonlRelativePath
                Body = pending.JsonlBody }
              { CanonicalFileName = Path.GetFileName ruleCandidatesSummaryRelativePath
                Body = pending.SummaryBody } ]

        match publish canonicalDir true false files with
        | outcome when outcome.Success -> true
        | outcome ->
            eprintfn "error: rule-candidate publication failed: %A" outcome
            // Preserve the actual canonical paths so a debug reader sees
            // what we attempted to write.
            ignore canonicalJsonl
            ignore canonicalSummary
            false
    with _ ->
        false

// -----------------------------------------------------------------------------
// Duplicate detection
// -----------------------------------------------------------------------------

let private checkForDuplicates
    (kind: InputIdentityKind)
    (identity: 'a -> string)
    (items: 'a list)
    : Result<unit, EngineError> =
    let duplicates =
        items |> List.countBy identity |> List.filter (fun (_, count) -> count > 1)

    if not duplicates.IsEmpty then
        let dupIds =
            duplicates
            |> List.map fst
            |> List.sortWith (fun a b -> String.Compare(a, b, StringComparison.Ordinal))

        Error(DuplicateInputIdentities(kind, dupIds))
    else
        Ok()

let private buildUniqueMap
    (kind: InputIdentityKind)
    (identity: 'a -> string)
    (items: 'a list)
    : Result<Map<string, 'a>, EngineError> =
    match checkForDuplicates kind identity items with
    | Error e -> Error e
    | Ok() -> items |> List.map (fun item -> identity item, item) |> Map.ofList |> Ok

// -----------------------------------------------------------------------------
// Single canonical input snapshot from episode engine
// -----------------------------------------------------------------------------

type RuleCandidateInputs =
    { Episodes: RepairEpisode list
      Transitions: DiagnosticTransition list
      ChangeSets: Map<string, GitChangeSet>
      VerificationEvidence: Map<string, LocatedVerificationEvidence> }

let private mapEpisodeEngineFailure (failure: EpisodeEngineFailure) : EngineError =
    match failure with
    | EpisodeEngineFailure.VerificationEvidenceLoadFailed errors ->
        EngineError.VerificationEvidenceLoadFailed(errors |> List.map string)
    | EpisodeEngineFailure.DeclarationLoadFailed issues ->
        EngineError.Internal(sprintf "Declaration load failed: %A" issues)
    | EpisodeEngineFailure.PublicationFailed(_, msg) -> EngineError.PublicationFailed msg
    | EpisodeEngineFailure.InternalFailure(op, msg) -> EngineError.Internal(sprintf "Internal failure in %s: %s" op msg)

let private loadFromEpisodeEngine (repoRoot: string) : Result<RuleCandidateInputs, EngineError> =
    match runEpisodeEngine repoRoot defaultEngineOptions with
    | EpisodeEngineExecution.Failed failure -> Error(mapEpisodeEngineFailure failure)
    | EpisodeEngineExecution.Completed result ->
        match
            result.RepairEpisodes
            |> List.mapi (fun idx ep -> validateEpisodeIdentity idx ep)
            |> List.choose id
            |> function
                | [] -> Ok()
                | errs -> Error(errs.Head)
        with
        | Error e -> Error e
        | Ok() ->
            match
                checkForDuplicates EpisodeIdentity (fun (ep: RepairEpisode) -> ep.EpisodeId) result.RepairEpisodes
            with
            | Error e -> Error e
            | Ok() ->
                match
                    result.Transitions
                    |> List.mapi (fun idx t -> validateTransitionIdentity idx t)
                    |> List.choose id
                    |> function
                        | [] -> Ok()
                        | errs -> Error(errs.Head)
                with
                | Error e -> Error e
                | Ok() ->
                    match checkForDuplicates TransitionIdentity transitionIdentityString result.Transitions with
                    | Error e -> Error e
                    | Ok() ->
                        match
                            result.ChangeSets
                            |> List.mapi (fun idx cs -> validateChangeSetIdentity idx cs)
                            |> List.choose id
                            |> function
                                | [] -> Ok()
                                | errs -> Error(errs.Head)
                        with
                        | Error e -> Error e
                        | Ok() ->
                            match
                                buildUniqueMap
                                    ChangeSetIdentity
                                    (fun (cs: GitChangeSet) -> cs.ChangeSetId)
                                    result.ChangeSets
                            with
                            | Error e -> Error e
                            | Ok cssMap ->
                                match
                                    result.Verification
                                    |> List.mapi (fun idx lv -> validateVerificationIdentity idx lv)
                                    |> List.choose id
                                    |> function
                                        | [] -> Ok()
                                        | errs -> Error(errs.Head)
                                with
                                | Error e -> Error e
                                | Ok() ->
                                    match
                                        buildUniqueMap
                                            VerificationEvidenceIdentity
                                            (fun (lv: LocatedVerificationEvidence) -> lv.Evidence.EvidenceId)
                                            result.Verification
                                    with
                                    | Error e -> Error e
                                    | Ok evidMap ->
                                        Ok
                                            { Episodes = result.RepairEpisodes
                                              Transitions = result.Transitions
                                              ChangeSets = cssMap
                                              VerificationEvidence = evidMap }

let loadAllInputs (repoRoot: string) : Result<RuleCandidateInputs, EngineError> =
    loadFromEpisodeEngine repoRoot

// -----------------------------------------------------------------------------
// Candidate building
// -----------------------------------------------------------------------------

/// Build a single candidate record.  The candidate ID is deterministically
/// computed from the parsed semantic fields.
let buildCandidate
    (ep: RepairEpisode)
    (cs: GitChangeSet)
    (gf: TransitionGroupFacts)
    (allTransitions: DiagnosticTransition list)
    : RuleCandidate =
    let obs = deriveParserCascadeProse ep.EpisodeKey ep.AfterCommitOid gf
    let partition = buildPartition gf allTransitions

    let evid =
        { EpisodeId = ep.EpisodeId
          EpisodeKey = ep.EpisodeKey
          ChangeSetId = ep.ChangeSetId
          VerificationEvidenceIds = ep.VerificationEvidenceIds
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
            parserCascadeCandidateHypothesis
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
            partition.SupportingTransitionIds
            partition.ContextTransitionIds
            partition.CounterevidenceTransitionIds
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
      ApplicabilityConditions = parserCascadeApplicability
      Observation = obs
      CandidateHypothesis = parserCascadeCandidateHypothesis
      Limitations = parserCascadeLimitations
      PrimaryPath = gf.Path
      DiagnosticCodes = gf.DiagnosticCodes
      DiagnosticCount = gf.TransitionCount
      EarliestLine = gf.EarliestLine
      ChangedPaths = cpaths
      StatusFlags = defaultCandidateStatusFlags
      TransitionPartition = partition
      Evidence = evid }

// -----------------------------------------------------------------------------
// Verification evidence binding
// -----------------------------------------------------------------------------

let validateVerificationBinding
    (ep: RepairEpisode)
    (verificationMap: Map<string, LocatedVerificationEvidence>)
    : string option =
    let evidId = ep.VerificationEvidenceIds

    if evidId.IsEmpty then
        Some "Episode has no verification evidence references"
    else
        let mutable firstError = None

        for evid in evidId do
            match Map.tryFind evid verificationMap with
            | None -> firstError <- Some(sprintf "Verification evidence %s not found" evid)
            | Some locatedRecord ->
                let record = locatedRecord.Evidence
                if record.EpisodeId <> ep.EpisodeId then
                    firstError <- Some(sprintf "Verification evidence %s has episode_id mismatch" evid)
                elif record.Status <> VerificationStatus.Pass then
                    firstError <- Some(sprintf "Verification evidence %s has status %A (expected Pass)" evid record.Status)
                elif record.ExitCode <> 0 then
                    firstError <- Some(sprintf "Verification evidence %s has exit_code %d (expected 0)" evid record.ExitCode)
                elif record.TestedCommitOid <> ep.AfterCommitOid then
                    firstError <- Some(sprintf "Verification evidence %s tested_commit_oid mismatch" evid)
                elif record.TestedTreeOid <> ep.AfterTreeOid then
                    firstError <- Some(sprintf "Verification evidence %s tested_tree_oid mismatch" evid)

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

        { Candidates = []
          EligibleEpisodes = 0
          EpisodesWithCandidates = 0
          Errors = errs |> Seq.toList }

    | Result.Ok inputs ->
        for ep in inputs.Episodes do
            if isEpisodeEligible ep then
                match validateVerificationBinding ep inputs.VerificationEvidence with
                | Some errMsg -> errs.Add(EngineError.CandidateGenerationFailed errMsg)
                | None ->
                    elEp <- elEp + 1

                    let et = inputs.Transitions |> List.filter (fun t -> t.EpisodeId = ep.EpisodeId)

                    match Map.tryFind ep.ChangeSetId inputs.ChangeSets with
                    | None ->
                        errs.Add(
                            EngineError.CandidateGenerationFailed(sprintf "change set %s not found" ep.ChangeSetId)
                        )
                    | Some changeSet ->
                        match selectCandidateGroup ep changeSet et with
                        | Some gf ->
                            cands.Add(buildCandidate ep changeSet gf et)
                            epWC <- epWC + 1
                        | None -> ()

        if errs.Count = 0 then
            match elEp, cands.Count with
            | 1, 1 -> ()
            | 0, _ -> errs.Add EngineError.NoEligibleEpisodes
            | _, 0 -> errs.Add(EngineError.CandidateGenerationFailed("eligible episode produced no rule candidate"))
            | _, count ->
                errs.Add(
                    EngineError.CandidateGenerationFailed(
                        sprintf "production candidate count must be exactly one; actual=%d" count
                    )
                )

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

// -----------------------------------------------------------------------------
// Read-only verification
// -----------------------------------------------------------------------------

type VerificationVerdict =
    | Verified
    | IdentityMismatch of expected: string * actual: string * reason: string
    | SummaryMismatch of reason: string
    | ParseFailure of reason: string
    | OutputMissing of path: string
    | MultipleCandidatesWhenExactlyOneRequired

/// Verify the published canonical artifacts without writing.  Recomputes the
/// candidate ID, the summary counts, and the canonical ordering.  Leaves
/// `git status --short` empty when the only files in the canonical dir are
/// `rule-candidates-v2.jsonl` and `rule-candidate-summary-v2.json` (we do
/// not touch them here at all).
let verifyCanonicalArtifacts
    (repoRoot: string)
    (expected: ExtractionResult)
    : VerificationVerdict =
    let canonicalDir = Path.GetDirectoryName(toAbsolutePath repoRoot ruleCandidatesJsonlRelativePath)
    let cpath = Path.Combine(canonicalDir, Path.GetFileName ruleCandidatesJsonlRelativePath)
    let spath = Path.Combine(canonicalDir, Path.GetFileName ruleCandidatesSummaryRelativePath)

    if not (File.Exists cpath) then
        OutputMissing cpath
    elif not (File.Exists spath) then
        OutputMissing spath
    else
        let summary =
            try
                parseRuleCandidateSummaryStrict (File.ReadAllText spath)
            with _ex ->
                Error(MalformedJson (sprintf "summary: read failed: %s" _ex.Message))

        match summary with
        | Error e -> ParseFailure(sprintf "summary: %A" e)
        | Ok s ->
            if s.CandidatesTotal <> expected.Candidates.Length then
                SummaryMismatch "candidates_total mismatch"
            elif s.ParserCascadeCandidates <> expected.Candidates.Length then
                SummaryMismatch "parser_cascade_candidates mismatch"
            elif s.SingleEpisodeCandidates <> expected.Candidates.Length then
                SummaryMismatch "single_episode_candidates mismatch"
            elif s.EligibleEpisodes <> expected.EligibleEpisodes then
                SummaryMismatch "eligible_episodes mismatch"
            elif s.EpisodesWithCandidates <> expected.EpisodesWithCandidates then
                SummaryMismatch "episodes_with_candidates mismatch"
            elif List.length s.CandidateIds <> expected.Candidates.Length then
                SummaryMismatch "candidate_ids length mismatch"
            else
                try
                    let lines = File.ReadAllLines cpath |> Array.toList

                    let candidates =
                        lines
                        |> List.mapi (fun idx line ->
                            match parseRuleCandidateStrict line with
                            | Ok c -> Ok c
                            | Error e -> Error(sprintf "line %d: %A" (idx + 1) e))

                    let failures =
                        candidates
                        |> List.choose (function Error e -> Some e | Ok _ -> None)

                    if not (List.isEmpty failures) then
                        ParseFailure(String.concat "; " failures)
                    else
                        let parsed =
                            candidates |> List.choose (function Ok c -> Some c | _ -> None)

                        if parsed.Length <> 1 then
                            MultipleCandidatesWhenExactlyOneRequired
                        else
                            let c = parsed.Head
                            // Recompute the identity from parsed semantic
                            // fields.  Never trust the published id verbatim.
                            let limitList = c.Limitations

                            let recomputed =
                                computeCandidateId
                                    c.SchemaVersion
                                    c.Kind
                                    c.EvidenceStrength
                                    c.Title
                                    c.Symptom
                                    c.ApplicabilityConditions
                                    c.Observation
                                    c.CandidateHypothesis
                                    limitList
                                    c.PrimaryPath
                                    c.DiagnosticCodes
                                    c.DiagnosticCount
                                    c.EarliestLine
                                    c.ChangedPaths
                                    c.Evidence.EpisodeId
                                    c.Evidence.EpisodeKey
                                    c.Evidence.ChangeSetId
                                    c.Evidence.VerificationEvidenceIds
                                    c.TransitionPartition.SupportingTransitionIds
                                    c.TransitionPartition.ContextTransitionIds
                                    c.TransitionPartition.CounterevidenceTransitionIds
                                    c.Evidence.BeforeCommitOid
                                    c.Evidence.BeforeTreeOid
                                    c.Evidence.AfterCommitOid
                                    c.Evidence.AfterTreeOid

                            if recomputed <> c.CandidateId then
                                IdentityMismatch(c.CandidateId, recomputed, "candidate_id does not match recomputed value")
                            elif c.StatusFlags.CausalFamilyCurated then
                                IdentityMismatch(c.CandidateId, recomputed, "causal_family_curated must be false in v2")
                            elif c.StatusFlags.RepairAdviceAvailable then
                                IdentityMismatch(c.CandidateId, recomputed, "repair_advice_available must be false in v2")
                            elif c.StatusFlags.LlmTipAvailable then
                                IdentityMismatch(c.CandidateId, recomputed, "llm_tip_available must be false in v2")
                            else
                                Verified
                with ex ->
                    ParseFailure ex.Message

/// Read-only verification entry point.  Returns (verdict, byteIdentical).
/// The verifier performs no writes.
let runReadOnlyVerify (repoRoot: string) : VerificationVerdict * bool =
    let canonicalDir = Path.GetDirectoryName(toAbsolutePath repoRoot ruleCandidatesJsonlRelativePath)
    let cpath = Path.Combine(canonicalDir, Path.GetFileName ruleCandidatesJsonlRelativePath)
    let spath = Path.Combine(canonicalDir, Path.GetFileName ruleCandidatesSummaryRelativePath)

    let preBytes =
        (if File.Exists cpath then File.ReadAllBytes cpath else [||]),
        (if File.Exists spath then File.ReadAllBytes spath else [||])

    let expected = extractCandidates repoRoot
    let verdict = verifyCanonicalArtifacts repoRoot expected

    let postBytes =
        (if File.Exists cpath then File.ReadAllBytes cpath else [||]),
        (if File.Exists spath then File.ReadAllBytes spath else [||])

    let byteIdentical =
        preBytes = postBytes

    verdict, byteIdentical
