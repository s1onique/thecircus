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
    // ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01:
    // Typed failure taxonomy required by the fail-closed matrix.  These
    // variants coexist with the existing string-only variants above and
    // are emitted by the new typed checks in addition to (or instead of,
    // depending on the call site) the generic ones.
    | RequiredCorpusMissing of corpusKind: string * path: string
    | CorpusPathNotFile of corpusKind: string * path: string
    | CorpusUnreadable of corpusKind: string * path: string * operation: string * exceptionType: string
    | EmptyRequiredCorpus of corpusKind: string * path: string
    | MalformedJsonlRecord of corpusKind: string * path: string * lineNumber: int * detail: string
    | UnsupportedInputSchema of corpusKind: string * path: string * lineNumber: int * actualVersion: string * expectedVersion: string
    | EmptyInputIdentity of identityKind: string * path: string * lineNumber: int
    | DuplicateInputIdentity of identityKind: string * identity: string * occurrences: int list
    | DuplicateEpisodeKey of episodeKey: string * episodeIds: string list
    | UnresolvedInputReference of ownerKind: string * ownerIdentity: string * fieldName: string * missingIdentity: string
    | DuplicateReferenceWithinOwner of ownerKind: string * ownerIdentity: string * fieldName: string * duplicateIdentity: string
    | VerificationBindingRejected of episodeId: string * evidenceId: string * reason: string
    | NoCandidatesProduced of excludedReasons: string list
    | AmbiguousCandidateSelection of episodeId: string * equallyRankedCandidateKeys: string list
    | CardinalityMismatch of expected: int * actual: int
    | PublicationFailure of operation: string * path: string * detail: string
    | CanonicalStateMayHaveChanged of detail: string

// ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01:
// Typed failure taxonomy — exposed so tests can pattern-match without
// relying on string-only discrimination.
type RuleCandidateCorpusKind =
    | RepairEpisodes
    | ChangeSets
    | DiagnosticTransitions
    | VerificationEvidence
    | CanonicalCandidates
    | CanonicalSummary

let corpusKindToken (k: RuleCandidateCorpusKind) : string =
    match k with
    | RuleCandidateCorpusKind.RepairEpisodes -> "repair_episodes"
    | RuleCandidateCorpusKind.ChangeSets -> "change_sets"
    | RuleCandidateCorpusKind.DiagnosticTransitions -> "diagnostic_transitions"
    | RuleCandidateCorpusKind.VerificationEvidence -> "verification_evidence"
    | RuleCandidateCorpusKind.CanonicalCandidates -> "canonical_candidates"
    | RuleCandidateCorpusKind.CanonicalSummary -> "canonical_summary"

type VerificationBindingFailure =
    | VerificationStatusNotPass of actualStatus: string
    | VerificationExitCodeNotZero of actualExitCode: int
    | TestedCommitMismatch of expected: string * actual: string
    | TestedTreeMismatch of expected: string * actual: string
    | EvidenceEpisodeMismatch of expected: string * actual: string
    | RequiredVerificationFieldMissing of fieldName: string
    | InconsistentVerificationOutcome of status: string * exitCode: int

type RuleCandidateSelectionFailure =
    | NoEligibleEpisodes
    | NoCandidatesProduced of excludedReasons: string list
    | AmbiguousCandidateSelection of episodeId: string * equallyRankedCandidateKeys: string list
    | CardinalityMismatch of expected: int * actual: int

type RuleCandidatePublicationFailure =
    | StagingFailure of operation: string * path: string * detail: string
    | FlushFailure of path: string * detail: string
    | CommitFailure of operation: string * path: string * detail: string
    | RollbackFailure of operation: string * path: string * detail: string
    | CleanupFailure of path: string * detail: string
    | PreviousCanonicalSnapshotUnavailable of path: string * detail: string
    | CanonicalStateMayHaveChanged of detail: string

/// Success result of a typed publication.
type RuleCandidatePublicationSuccess =
    { CanonicalJsonlSha256: string
      CanonicalSummarySha256: string
      OutputHashes: (string * string) list
      RetainedTempPaths: string list }

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

// ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01:
// Typed publication outcome.  Delegates to the shared `AtomicPublish.publish`
// and projects the underlying `PublishOutcome` to the typed authority
// required by the matrix.  On any failure the canonical outputs remain
// byte-identical to the pre-publication state; the typed failure list is
// never collapsed into a Boolean.
let publishCandidatesDetailed
    (repoRoot: string)
    (result: ExtractionResult)
    : Result<RuleCandidatePublicationSuccess, RuleCandidatePublicationFailure list> =
    let canonicalDir =
        Path.GetDirectoryName(toAbsolutePath repoRoot ruleCandidatesJsonlRelativePath)

    let canonicalJsonl = Path.Combine(canonicalDir, Path.GetFileName ruleCandidatesJsonlRelativePath)
    let canonicalSummary = Path.Combine(canonicalDir, Path.GetFileName ruleCandidatesSummaryRelativePath)

    let pending = buildPending canonicalDir result.Candidates result.EligibleEpisodes result.EpisodesWithCandidates

    let files =
        [ { CanonicalFileName = Path.GetFileName ruleCandidatesJsonlRelativePath
            Body = pending.JsonlBody }
          { CanonicalFileName = Path.GetFileName ruleCandidatesSummaryRelativePath
            Body = pending.SummaryBody } ]

    try
        match publish canonicalDir true false files with
        | outcome when outcome.Success ->
            let jsonlHash =
                match List.tryFind (fun (n, _) -> n = Path.GetFileName ruleCandidatesJsonlRelativePath) outcome.OutputHashes with
                | Some (_, h) -> h
                | None -> ""
            let summaryHash =
                match List.tryFind (fun (n, _) -> n = Path.GetFileName ruleCandidatesSummaryRelativePath) outcome.OutputHashes with
                | Some (_, h) -> h
                | None -> ""
            Ok
                { CanonicalJsonlSha256 = jsonlHash
                  CanonicalSummarySha256 = summaryHash
                  OutputHashes = outcome.OutputHashes
                  RetainedTempPaths = outcome.RetainedTempPaths }
        | outcome ->
            let typed =
                if not outcome.CanonicalByteIdenticalAfterFailure then
                    [ RuleCandidatePublicationFailure.CanonicalStateMayHaveChanged "atomic publish reported non-byte-identical canonical state" ]
                elif not (List.isEmpty outcome.RetainedTempPaths) then
                    outcome.RetainedTempPaths
                    |> List.map (fun p -> RuleCandidatePublicationFailure.CleanupFailure(p, "staging residue after publish failure"))
                else
                    [ RuleCandidatePublicationFailure.CommitFailure("publish", canonicalJsonl, "atomic publish reported failure") ]
            Error typed
    with _ex ->
        Error
            [ RuleCandidatePublicationFailure.CommitFailure(
                "publish",
                canonicalJsonl,
                sprintf "unexpected exception during publish: %s" _ex.Message) ]

/// Backwards-compatible Boolean wrapper.  Returns true on success and on
/// failure delegates exactly once to `publishCandidatesDetailed`.  The
/// failure list is rendered for the operator via stderr; the boolean value
/// is preserved so existing callers do not need to be rewritten.
let publishCandidates (repoRoot: string) (result: ExtractionResult) : bool =
    match publishCandidatesDetailed repoRoot result with
    | Ok _ -> true
    | Error failures ->
        for f in failures do
            eprintfn "error: rule-candidate publication failure: %A" f
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

// ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01-CORRECTION05:
// Map upstream `EpisodeEngineFailure` to one or more `EngineError`.  The
// mapper NEVER collapses multiple errors into one.  For verification-
// evidence load errors the duplicate and non-duplicate cases are preserved
// independently so the rule-candidate adapter can emit BOTH a typed
// `DuplicateInputIdentities` and a typed `VerificationEvidenceLoadFailed`.
// For `DuplicateInputIdentities` the upstream kind is mapped 1:1 to the
// rule-candidate `InputIdentityKind`.  Output order:
//   EpisodeIdentity, ChangeSetIdentity, TransitionIdentity,
//   VerificationEvidenceIdentity, then all other evidence errors.
/// Explicit rank for the rule-candidate InputIdentityKind.  Lower
/// rank = earlier in the resulting EngineError list.  Matches the
/// documented output order:
///   EpisodeIdentity < ChangeSetIdentity < TransitionIdentity
///   < VerificationEvidenceIdentity
let private kindRank (k: InputIdentityKind) : int =
    match k with
    | InputIdentityKind.EpisodeIdentity -> 0
    | InputIdentityKind.ChangeSetIdentity -> 1
    | InputIdentityKind.TransitionIdentity -> 2
    | InputIdentityKind.VerificationEvidenceIdentity -> 3

let private mapEpisodeInputIdentityKind (k: EpisodeInputIdentityKind) : InputIdentityKind =
    match k with
    | EpisodeInputIdentityKind.RepairEpisode -> EpisodeIdentity
    | EpisodeInputIdentityKind.ChangeSet -> ChangeSetIdentity
    | EpisodeInputIdentityKind.DiagnosticTransition -> TransitionIdentity

let mapEpisodeEngineFailure (failure: EpisodeEngineFailure) : EngineError list =
    match failure with
    | EpisodeEngineFailure.DuplicateInputIdentities dups ->
        // Explicit cross-kind ordering.  The adapter is defensive:
        // it sorts the kind buckets by `compare kindRank` so that
        // mixed-kind failures always produce the same
        // `EngineError list` regardless of the upstream grouping
        // order.  `String.CompareOrdinal` is used for identity
        // strings because ordinal comparison is independent of
        // language and culture (Microsoft Learn, StringComparer.Ordinal).
        let grouped =
            dups
            |> List.groupBy (fun d -> mapEpisodeInputIdentityKind d.Kind)
            |> List.sortBy (fun (kind, _) -> kindRank kind)
            |> List.map (fun (kind, items) ->
                let ids =
                    items
                    |> List.map (fun d -> d.Identity)
                    |> List.distinct
                    |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
                DuplicateInputIdentities(kind, ids))
        grouped
    | EpisodeEngineFailure.VerificationEvidenceLoadFailed errors ->
        // Lossless mapping with deterministic non-duplicate ordering.
        // Duplicates are partitioned into a typed
        // DuplicateInputIdentities.  Non-duplicate errors are sorted
        // by a typed key (kind, source path, line number, field name)
        // using ordinal comparison so the mapped list is invariant
        // under input-record order reversal.
        let dups, nonDups =
            errors
            |> List.partition (function
                | VerificationEvidenceLoadError.DuplicateEvidenceId(_, _, _, _) -> true
                | _ -> false)
        let output = ResizeArray<EngineError>()
        match dups with
        | [] -> ()
        | _ ->
            let ids =
                dups
                |> List.choose (function
                    | VerificationEvidenceLoadError.DuplicateEvidenceId(_, evidId, _, _) -> Some evidId
                    | _ -> None)
                |> List.distinct
                |> List.sortWith (fun a b -> String.CompareOrdinal(a, b))
            output.Add(DuplicateInputIdentities(VerificationEvidenceIdentity, ids))

        // Length-prefixed framing for collision-free sort keys.  Every
        // value is encoded as `~len~value` where `len` is the number
        // of UTF-16 code units and `~` is a separator that cannot
        // appear in a length integer.  This guarantees:
        //   * No two different discriminator tuples can produce the
        //     same key, regardless of whether their textual contents
        //     contain `|` or other separator characters.
        //   * Ordinal string comparison via `String.CompareOrdinal`
        //     preserves the intended order.
        let frame (s: string) : string =
            let n = s.Length
            sprintf "%d~%s" n s
        let join (kind: string) (parts: string list) : string =
            String.concat "~" (kind :: parts |> List.map frame)
        let ord (s: string) (s2: string) : string = s + "~" + s2
        let nonDupKey (e: VerificationEvidenceLoadError) : string =
            match e with
            | VerificationEvidenceLoadError.EvidenceFileMissing p ->
                join "missing" [ p ]
            | VerificationEvidenceLoadError.EvidenceFileUnreadable(p, msg) ->
                join "unreadable" [ p; msg ]
            | VerificationEvidenceLoadError.DuplicateEvidenceId(p, id, l1, l2) ->
                join "duplicate" [ p; id; string l1; string l2 ]
            | VerificationEvidenceLoadError.ConflictingEvidenceRecord(p, id, l1, l2) ->
                join "conflicting" [ p; id; string l1; string l2 ]
            | VerificationEvidenceLoadError.UnsupportedEvidenceSchemaVersion(p, v) ->
                join "unsupported_schema" [ p; v ]
            | VerificationEvidenceLoadError.ParseError pe ->
                match pe with
                | VerificationEvidenceParseError.MalformedJson(s, l, m) ->
                    join "malformed" [ s; string l; m ]
                | VerificationEvidenceParseError.ExpectedObject(s, l) ->
                    join "expected_object" [ s; string l ]
                | VerificationEvidenceParseError.MissingField(s, l, f) ->
                    join "missing_field" [ s; string l; f ]
                | VerificationEvidenceParseError.WrongFieldType(s, l, f, e, a) ->
                    join "wrong_type" [ s; string l; f; e; a ]
                | VerificationEvidenceParseError.UnsupportedSchemaVersion(s, l, v) ->
                    join "schema_v" [ s; string l; v ]
                | VerificationEvidenceParseError.UnknownVerificationKind(s, l, v) ->
                    join "unknown_kind" [ s; string l; v ]
                | VerificationEvidenceParseError.UnknownVerificationStatus(s, l, v) ->
                    join "unknown_status" [ s; string l; v ]
                | VerificationEvidenceParseError.InvalidExitCode(s, l, v) ->
                    join "invalid_exit" [ s; string l; v ]
                | VerificationEvidenceParseError.InvalidCommitOid(s, l, f, v) ->
                    join "invalid_commit" [ s; string l; f; v ]
                | VerificationEvidenceParseError.InvalidTreeOid(s, l, f, v) ->
                    join "invalid_tree" [ s; string l; f; v ]
                | VerificationEvidenceParseError.InvalidSha256(s, l, f, v) ->
                    join "invalid_sha" [ s; string l; f; v ]
                | VerificationEvidenceParseError.InvalidEvidenceId(s, l, v) ->
                    join "invalid_evidence_id" [ s; string l; v ]
                | VerificationEvidenceParseError.PlaceholderEvidenceId(s, l, v) ->
                    join "placeholder_evidence_id" [ s; string l; v ]
                | VerificationEvidenceParseError.JsonException(s, l, m) ->
                    join "json_exception" [ s; string l; m ]
                | VerificationEvidenceParseError.DuplicateRawProperty(s, l, p, n) ->
                    join "dup_raw_prop" [ s; string l; p; string n ]
                | VerificationEvidenceParseError.DuplicateSemanticField(s, l, c, a) ->
                    join "dup_sem_field" [ s; string l; c; a ]
                | VerificationEvidenceParseError.ConflictingSemanticFields(s, l, c, a, cv, av) ->
                    join "conf_sem_field" [ s; string l; c; a; cv; av ]
        match nonDups with
        | [] -> ()
        | _ ->
            let sortedNonDups =
                nonDups
                |> List.sortBy nonDupKey
            output.Add(VerificationEvidenceLoadFailed(sortedNonDups |> List.map string))
        output |> Seq.toList
    | EpisodeEngineFailure.DeclarationLoadFailed issues ->
        [ EngineError.Internal(sprintf "Declaration load failed: %A" issues) ]
    | EpisodeEngineFailure.PublicationFailed(_, msg) -> [ EngineError.PublicationFailed msg ]
    | EpisodeEngineFailure.InternalFailure(op, msg) ->
        [ EngineError.Internal(sprintf "Internal failure in %s: %s" op msg) ]

let private loadFromEpisodeEngine (repoRoot: string) : Result<RuleCandidateInputs, EngineError list> =
    // ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01-CORRECTION05:
    // Upstream duplicate-identity authority lives in the repair-episode
    // engine.  When `runEpisodeEngine` succeeds, every list is already
    // unique-by-identity; we only have to validate non-empty identities
    // and build look-up maps.  Any collected non-empty-identity errors are
    // returned as a list (never collapsed to a single error).
    match runEpisodeEngine repoRoot defaultEngineOptions with
    | EpisodeEngineExecution.Failed failure -> Error(mapEpisodeEngineFailure failure)
    | EpisodeEngineExecution.Completed result ->
        let collected = ResizeArray<EngineError>()

        for idx, ep in List.mapi (fun i e -> i, e) result.RepairEpisodes do
            match validateEpisodeIdentity idx ep with
            | Some err -> collected.Add err
            | None -> ()

        for idx, t in List.mapi (fun i x -> i, x) result.Transitions do
            match validateTransitionIdentity idx t with
            | Some err -> collected.Add err
            | None -> ()

        for idx, cs in List.mapi (fun i x -> i, x) result.ChangeSets do
            match validateChangeSetIdentity idx cs with
            | Some err -> collected.Add err
            | None -> ()

        for idx, lv in List.mapi (fun i x -> i, x) result.Verification do
            match validateVerificationIdentity idx lv with
            | Some err -> collected.Add err
            | None -> ()

        let errList = collected |> Seq.toList

        if not (List.isEmpty errList) then
            Error errList
        else
            let cssMap =
                result.ChangeSets
                |> List.map (fun cs -> cs.ChangeSetId, cs)
                |> Map.ofList

            let evidMap =
                result.Verification
                |> List.map (fun lv -> lv.Evidence.EvidenceId, lv)
                |> Map.ofList

            Ok
                { Episodes = result.RepairEpisodes
                  Transitions = result.Transitions
                  ChangeSets = cssMap
                  VerificationEvidence = evidMap }

let loadAllInputs (repoRoot: string) : Result<RuleCandidateInputs, EngineError list> =
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
    | Result.Error upstreamErrors ->
        // ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01-CORRECTION05:
        // Upstream errors arrive as a list.  Every mapped error is added
        // to the result; we never collapse to a single error.  When at
        // least one DuplicateInputIdentities is present we MUST NOT also
        // emit NoEligibleEpisodes or convert anything to Internal.
        for ue in upstreamErrors do
            errs.Add ue

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

// -----------------------------------------------------------------------------
// Typed binding authority (ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01)
// -----------------------------------------------------------------------------

/// Render a `VerificationBindingFailure` to a deterministic string.  Used
/// to embed the typed reason in the legacy `EngineError.VerificationBindingRejected`
/// variant for backward compatibility without leaking the typed DU into
/// existing error rendering.
let renderVerificationBindingFailure (f: VerificationBindingFailure) : string =
    match f with
    | VerificationBindingFailure.VerificationStatusNotPass s -> "verification_status_not_pass:" + s
    | VerificationBindingFailure.VerificationExitCodeNotZero c -> "verification_exit_code_not_zero:" + string c
    | VerificationBindingFailure.TestedCommitMismatch(e, a) -> "tested_commit_mismatch:" + e + "|" + a
    | VerificationBindingFailure.TestedTreeMismatch(e, a) -> "tested_tree_mismatch:" + e + "|" + a
    | VerificationBindingFailure.EvidenceEpisodeMismatch(e, a) -> "evidence_episode_mismatch:" + e + "|" + a
    | VerificationBindingFailure.RequiredVerificationFieldMissing n -> "required_field_missing:" + n
    | VerificationBindingFailure.InconsistentVerificationOutcome(s, c) -> "inconsistent_outcome:" + s + "|" + string c

/// Typed variant of `validateVerificationBinding`.  Returns a typed
/// `VerificationBindingRejected` for the first failing record in the
/// ordered reference list.  Determinism is preserved by sorting the
/// `VerificationBindingFailure` details using `String.CompareOrdinal`.
let validateVerificationBindingTyped
    (ep: RepairEpisode)
    (verificationMap: Map<string, LocatedVerificationEvidence>)
    : EngineError option =
    let evidIds = ep.VerificationEvidenceIds |> List.sort

    if List.isEmpty evidIds then
        Some(VerificationBindingRejected(ep.EpisodeId, "", renderVerificationBindingFailure (VerificationBindingFailure.VerificationStatusNotPass "no_references")))
    else
        let mutable firstTypedError: EngineError option = None

        let sortedRefs =
            evidIds
            |> List.sortWith (fun a b -> String.Compare(a, b, StringComparison.Ordinal))

        for evid in sortedRefs do
            match Map.tryFind evid verificationMap with
            | None ->
                if firstTypedError.IsNone then
                    firstTypedError <-
                        Some(
                            UnresolvedInputReference(
                                "verification_evidence",
                                ep.EpisodeId,
                                "verification_evidence_ids",
                                evid
                            )
                        )
            | Some locatedRecord ->
                let record = locatedRecord.Evidence
                if firstTypedError.IsNone then
                    if record.EpisodeId <> ep.EpisodeId then
                        firstTypedError <-
                            Some(
                                VerificationBindingRejected(
                                    ep.EpisodeId,
                                    evid,
                                    renderVerificationBindingFailure (VerificationBindingFailure.EvidenceEpisodeMismatch(ep.EpisodeId, record.EpisodeId))
                                )
                            )
                    elif record.Status <> VerificationStatus.Pass then
                        firstTypedError <-
                            Some(
                                VerificationBindingRejected(
                                    ep.EpisodeId,
                                    evid,
                                    renderVerificationBindingFailure (VerificationBindingFailure.VerificationStatusNotPass(verificationStatusToken record.Status))
                                )
                            )
                    elif record.ExitCode <> 0 then
                        firstTypedError <-
                            Some(
                                VerificationBindingRejected(
                                    ep.EpisodeId,
                                    evid,
                                    renderVerificationBindingFailure (VerificationBindingFailure.VerificationExitCodeNotZero record.ExitCode)
                                )
                            )
                    elif record.TestedCommitOid <> ep.AfterCommitOid then
                        firstTypedError <-
                            Some(
                                VerificationBindingRejected(
                                    ep.EpisodeId,
                                    evid,
                                    renderVerificationBindingFailure (VerificationBindingFailure.TestedCommitMismatch(ep.AfterCommitOid, record.TestedCommitOid))
                                )
                            )
                    elif record.TestedTreeOid <> ep.AfterTreeOid then
                        firstTypedError <-
                            Some(
                                VerificationBindingRejected(
                                    ep.EpisodeId,
                                    evid,
                                    renderVerificationBindingFailure (VerificationBindingFailure.TestedTreeMismatch(ep.AfterTreeOid, record.TestedTreeOid))
                                )
                            )

        firstTypedError

let runExtraction (repoRoot: string) : ExtractionResult =
    let result = extractCandidates repoRoot

    if result.Errors.IsEmpty && not (List.isEmpty result.Candidates) then
        match publishCandidatesDetailed repoRoot result with
        | Ok _ -> result
        | Error failures ->
            let mapped =
                failures
                |> List.map (function
                    | RuleCandidatePublicationFailure.CommitFailure(op, p, d) ->
                        EngineError.PublicationFailure(op, p, d)
                    | RuleCandidatePublicationFailure.CleanupFailure(p, d) ->
                        EngineError.PublicationFailure("cleanup", p, d)
                    | RuleCandidatePublicationFailure.CanonicalStateMayHaveChanged d ->
                        EngineError.CanonicalStateMayHaveChanged d
                    | RuleCandidatePublicationFailure.StagingFailure(op, p, d) ->
                        EngineError.PublicationFailure(op, p, d)
                    | RuleCandidatePublicationFailure.FlushFailure(p, d) ->
                        EngineError.PublicationFailure("flush", p, d)
                    | RuleCandidatePublicationFailure.RollbackFailure(op, p, d) ->
                        EngineError.PublicationFailure(op, p, d)
                    | RuleCandidatePublicationFailure.PreviousCanonicalSnapshotUnavailable(p, d) ->
                        EngineError.PublicationFailure("snapshot", p, d))
            { result with Errors = mapped }
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
