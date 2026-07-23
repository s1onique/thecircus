module Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain

// =============================================================================
// Repair episode domain
// =============================================================================
//
// This module defines the canonical data model for the repair-episode linker
// described in ACT-CIRCUS-FSHARP-DIAGNOSTIC-REPAIR-EPISODE-LINKING01.
//
// The model binds:
//   * an explicit episode declaration (file-supplied inputs),
//   * resolved Git commit and tree OIDs (immutable history),
//   * a deterministic tree-to-tree change inventory (no rename inference),
//   * capture compatibility and exact-fingerprint transitions,
//   * versioned verification evidence,
//   * an episode qualification verdict.
//
// The model deliberately separates observation from causation. It produces
// "observed transition" / "repair candidate" / "regression candidate"
// labels but never claims "this change fixed this diagnostic" in any
// unconditional sense.

// -----------------------------------------------------------------------------
// Episode declaration
// -----------------------------------------------------------------------------

let RepairEpisodeDeclarationSchemaVersion = "repair-episode-declaration-v1"

type RepairEpisodeDeclaration = {
    SchemaVersion: string
    EpisodeKey: string
    BeforeCaptureId: string
    AfterCaptureId: string
    BeforeCommitOid: string
    AfterCommitOid: string
    ExpectedBeforeTreeOid: string option
    ExpectedAfterTreeOid: string option
    VerificationEvidenceIds: string list
    DeclaredRelevantPaths: string list
    Notes: string option
}

// -----------------------------------------------------------------------------
// Git identity
// -----------------------------------------------------------------------------

type GitObjectFormat =
    | Sha1
    | Sha256

let gitObjectFormatToken (f: GitObjectFormat) : string =
    match f with
    | Sha1 -> "sha1"
    | Sha256 -> "sha256"

let tryParseGitObjectFormat (token: string) : GitObjectFormat option =
    match token with
    | "sha1" -> Some Sha1
    | "sha256" -> Some Sha256
    | _ -> None

let gitObjectFormatWidth (f: GitObjectFormat) : int =
    match f with
    | Sha1 -> 40
    | Sha256 -> 64

/// A successful Git identity resolution.  Both trees are derived from
/// the commits using Git's ``^{tree}`` syntax.
type GitIdentityResolution = {
    BeforeCommitOid: string
    BeforeTreeOid: string
    AfterCommitOid: string
    AfterTreeOid: string
    CommitRange: string list
    ObjectFormat: GitObjectFormat
}

exception GitIdentityFailure of string

// -----------------------------------------------------------------------------
// Change set
// -----------------------------------------------------------------------------

let GitChangeSetSchemaVersion = "git-change-set-v1"
let ChangeSetIdentityVersion = "git-change-set-v1"

type GitChangeKind =
    | Added
    | Modified
    | Deleted
    | TypeChanged

let gitChangeKindToken (k: GitChangeKind) : string =
    match k with
    | Added -> "added"
    | Modified -> "modified"
    | Deleted -> "deleted"
    | TypeChanged -> "type_changed"

let tryParseGitChangeKind (token: string) : GitChangeKind option =
    match token with
    | "added" -> Some Added
    | "modified" -> Some Modified
    | "deleted" -> Some Deleted
    | "type_changed" -> Some TypeChanged
    | _ -> None

type GitChangeEntry = {
    BeforeMode: string
    AfterMode: string
    BeforeBlobOid: string option
    AfterBlobOid: string option
    ChangeKind: GitChangeKind
    CanonicalPath: string
}

type GitChangeSet = {
    SchemaVersion: string
    ChangeSetId: string
    ChangeSetVersion: string
    BeforeTreeOid: string
    AfterTreeOid: string
    ObjectFormat: GitObjectFormat
    Entries: GitChangeEntry list
}

exception GitChangeParseFailure of string

// -----------------------------------------------------------------------------
// Capture binding
// -----------------------------------------------------------------------------

type CompatibilityStatus =
    | Compatible
    | Incompatible
    | Unknown

let compatibilityStatusToken (s: CompatibilityStatus) : string =
    match s with
    | Compatible -> "compatible"
    | Incompatible -> "incompatible"
    | Unknown -> "unknown"

let tryParseCompatibilityStatus (token: string) : CompatibilityStatus option =
    match token with
    | "compatible" -> Some Compatible
    | "incompatible" -> Some Incompatible
    | "unknown" -> Some Unknown
    | _ -> None

type Compatibility = {
    Status: CompatibilityStatus
    Reasons: string list
    MissingFields: string list
}

let compatible : Compatibility =
    { Status = Compatible; Reasons = []; MissingFields = [] }

let incompatible reasons : Compatibility =
    { Status = Incompatible; Reasons = reasons; MissingFields = [] }

let unknown missing : Compatibility =
    { Status = Unknown; Reasons = []; MissingFields = missing }

type CaptureBindingResult = {
    CaptureId: string
    CaptureExists: bool
    ExtractionComplete: bool
    CommitMatches: bool
    TreeMatches: bool
    RawArtifactHashesMatch: bool
    OccurrencesValid: bool
    UnparsedLines: int
    UndeclaredAbsolutePaths: int
    BinlogReplayFailures: int
    CommandContract: string
    Compatibility: Compatibility
    CompatibleScope: bool
}

type CaptureBindingFailure =
    | MissingCapture
    | MissingRawArtifact
    | ExtractionIncomplete
    | CommitMismatch
    | TreeMismatch
    | RawHashMismatch
    | BinlogReplayFailure of string
    | ExtractionFailure of string

exception CaptureBindingException of CaptureBindingFailure

// -----------------------------------------------------------------------------
// Transitions
// -----------------------------------------------------------------------------

let DiagnosticTransitionSchemaVersion = "diagnostic-transition-v1"

type ExactTransitionKind =
    | PersistedSameCount
    | PersistedCountDecreased
    | PersistedCountIncreased
    | EliminatedAfter
    | IntroducedAfter

let exactTransitionKindToken (k: ExactTransitionKind) : string =
    match k with
    | PersistedSameCount -> "persisted_same_count"
    | PersistedCountDecreased -> "persisted_count_decreased"
    | PersistedCountIncreased -> "persisted_count_increased"
    | EliminatedAfter -> "eliminated_after"
    | IntroducedAfter -> "introduced_after"

let tryParseExactTransitionKind (token: string) : ExactTransitionKind option =
    match token with
    | "persisted_same_count" -> Some PersistedSameCount
    | "persisted_count_decreased" -> Some PersistedCountDecreased
    | "persisted_count_increased" -> Some PersistedCountIncreased
    | "eliminated_after" -> Some EliminatedAfter
    | "introduced_after" -> Some IntroducedAfter
    | _ -> None

type SourceLinkKind =
    | SourceFileModified of path: string
    | SourceFileAdded of path: string
    | SourceFileDeleted of path: string
    | ProjectFileModified of path: string
    | SourceAndProjectModified of sourcePath: string * projectPath: string
    | DeclaredRelevantPathChanged of paths: string list
    | NoDirectPathChange
    | MissingDiagnosticPath
    | AmbiguousPathEvidence of reasons: string list

let sourceLinkKindToken (k: SourceLinkKind) : string =
    match k with
    | SourceFileModified _ -> "source_file_modified"
    | SourceFileAdded _ -> "source_file_added"
    | SourceFileDeleted _ -> "source_file_deleted"
    | ProjectFileModified _ -> "project_file_modified"
    | SourceAndProjectModified _ -> "source_and_project_modified"
    | DeclaredRelevantPathChanged _ -> "declared_relevant_path_changed"
    | NoDirectPathChange -> "no_direct_path_change"
    | MissingDiagnosticPath -> "missing_diagnostic_path"
    | AmbiguousPathEvidence _ -> "ambiguous_path_evidence"

let tryParseSourceLinkKind (token: string) : SourceLinkKind option =
    match token with
    | "source_file_modified" -> Some(NoDirectPathChange)
    | "source_file_added" -> Some(NoDirectPathChange)
    | "source_file_deleted" -> Some(NoDirectPathChange)
    | "project_file_modified" -> Some(NoDirectPathChange)
    | "source_and_project_modified" -> Some(NoDirectPathChange)
    | "declared_relevant_path_changed" -> Some(NoDirectPathChange)
    | "no_direct_path_change" -> Some NoDirectPathChange
    | "missing_diagnostic_path" -> Some MissingDiagnosticPath
    | "ambiguous_path_evidence" -> Some(AmbiguousPathEvidence [])
    | _ -> None

type SourceLink = {
    Kind: SourceLinkKind
    Paths: string list
    Reasons: string list
}

type TransitionAssessment =
    | ExactPersistence
    | ObservedResolutionCandidate
    | ObservedRegressionCandidate
    | MultiplicityImprovementCandidate
    | MultiplicityRegressionCandidate
    | EliminatedBySourceRemoval
    | IntroducedWithSourceAddition
    | Unassessable
    | Ambiguous

let transitionAssessmentToken (a: TransitionAssessment) : string =
    match a with
    | ExactPersistence -> "exact_persistence"
    | ObservedResolutionCandidate -> "observed_resolution_candidate"
    | ObservedRegressionCandidate -> "observed_regression_candidate"
    | MultiplicityImprovementCandidate -> "multiplicity_improvement_candidate"
    | MultiplicityRegressionCandidate -> "multiplicity_regression_candidate"
    | EliminatedBySourceRemoval -> "eliminated_by_source_removal"
    | IntroducedWithSourceAddition -> "introduced_with_source_addition"
    | Unassessable -> "unassessable"
    | Ambiguous -> "ambiguous"

let tryParseTransitionAssessment (token: string) : TransitionAssessment option =
    match token with
    | "exact_persistence" -> Some ExactPersistence
    | "observed_resolution_candidate" -> Some ObservedResolutionCandidate
    | "observed_regression_candidate" -> Some ObservedRegressionCandidate
    | "multiplicity_improvement_candidate" -> Some MultiplicityImprovementCandidate
    | "multiplicity_regression_candidate" -> Some MultiplicityRegressionCandidate
    | "eliminated_by_source_removal" -> Some EliminatedBySourceRemoval
    | "introduced_with_source_addition" -> Some IntroducedWithSourceAddition
    | "unassessable" -> Some Unassessable
    | "ambiguous" -> Some Ambiguous
    | _ -> None

type DiagnosticTransition = {
    SchemaVersion: string
    EpisodeId: string
    ExactFingerprint: string
    TransitionKind: ExactTransitionKind
    BeforeOccurrenceCount: int
    AfterOccurrenceCount: int
    Severity: Circus.Tooling.FSharpDiagnostics.Domain.DiagnosticSeverity
    Code: string option
    MessageNormalized: string
    SourcePath: string option
    ProjectPath: string option
    Span: Circus.Tooling.FSharpDiagnostics.Domain.SourceSpan
    Compatibility: Compatibility
    SourceLink: SourceLink
    Assessment: TransitionAssessment
}

// -----------------------------------------------------------------------------
// Verification
// -----------------------------------------------------------------------------

let VerificationEvidenceSchemaVersion = "verification-evidence-v1"

type VerificationKind =
    | Build
    | FocusedTest
    | FocusedGate
    | CanonicalGate

let verificationKindToken (k: VerificationKind) : string =
    match k with
    | Build -> "build"
    | FocusedTest -> "focused_test"
    | FocusedGate -> "focused_gate"
    | CanonicalGate -> "canonical_gate"

let tryParseVerificationKind (token: string) : VerificationKind option =
    match token with
    | "build" -> Some Build
    | "focused_test" -> Some FocusedTest
    | "focused_gate" -> Some FocusedGate
    | "canonical_gate" -> Some CanonicalGate
    | _ -> None

type VerificationStatus =
    | Pass
    | Fail
    | InfrastructureError
    | MissingLogs

let verificationStatusToken (s: VerificationStatus) : string =
    match s with
    | Pass -> "pass"
    | Fail -> "fail"
    | InfrastructureError -> "infrastructure_error"
    | MissingLogs -> "missing_logs"

let tryParseVerificationStatus (token: string) : VerificationStatus option =
    match token with
    | "pass" -> Some Pass
    | "fail" -> Some Fail
    | "infrastructure_error" -> Some InfrastructureError
    | "missing_logs" -> Some MissingLogs
    | _ -> None

type VerificationEvidence = {
    SchemaVersion: string
    EvidenceId: string
    EpisodeId: string
    Kind: VerificationKind
    Command: string
    WorkingDirectory: string
    TestedCommitOid: string
    TestedTreeOid: string
    ExitCode: int
    StdoutSha256: string option
    StderrSha256: string option
    CombinedLogPath: string option
    Status: VerificationStatus
}

// -----------------------------------------------------------------------------
// Episode record
// -----------------------------------------------------------------------------

let RepairEpisodeSchemaVersion = "repair-episode-v1"

type VerificationLevel =
    | TransitionObserved
    | SourceLinked
    | BuildVerified
    | FocusedTestVerified
    | FocusedGateVerified

let verificationLevelToken (v: VerificationLevel) : string =
    match v with
    | TransitionObserved -> "transition_observed"
    | SourceLinked -> "source_linked"
    | BuildVerified -> "build_verified"
    | FocusedTestVerified -> "focused_test_verified"
    | FocusedGateVerified -> "focused_gate_verified"

let tryParseVerificationLevel (token: string) : VerificationLevel option =
    match token with
    | "transition_observed" -> Some TransitionObserved
    | "source_linked" -> Some SourceLinked
    | "build_verified" -> Some BuildVerified
    | "focused_test_verified" -> Some FocusedTestVerified
    | "focused_gate_verified" -> Some FocusedGateVerified
    | _ -> None

type EpisodeQualificationStatus =
    | Qualified
    | QualifiedWithLimitations
    | Ambiguous
    | Rejected

let episodeQualificationStatusToken (s: EpisodeQualificationStatus) : string =
    match s with
    | Qualified -> "qualified"
    | QualifiedWithLimitations -> "qualified_with_limitations"
    | Ambiguous -> "ambiguous"
    | Rejected -> "rejected"

let tryParseEpisodeQualificationStatus (token: string) : EpisodeQualificationStatus option =
    match token with
    | "qualified" -> Some Qualified
    | "qualified_with_limitations" -> Some QualifiedWithLimitations
    | "ambiguous" -> Some Ambiguous
    | "rejected" -> Some Rejected
    | _ -> None

type EpisodeQualification = {
    Status: EpisodeQualificationStatus
    Reasons: string list
}

type TransitionCounts = {
    PersistedSameCount: int
    PersistedCountDecreased: int
    PersistedCountIncreased: int
    EliminatedAfter: int
    IntroducedAfter: int
    ResolutionCandidates: int
    RegressionCandidates: int
    Unassessable: int
}

type RepairEpisode = {
    SchemaVersion: string
    EpisodeId: string
    EpisodeKey: string
    BeforeCaptureId: string
    AfterCaptureId: string
    BeforeCommitOid: string
    BeforeTreeOid: string
    AfterCommitOid: string
    AfterTreeOid: string
    CommitRange: string list
    ChangeSetId: string
    CommandContractBefore: string
    CommandContractAfter: string
    Compatibility: Compatibility
    TransitionCounts: TransitionCounts
    VerificationLevel: VerificationLevel
    VerificationEvidenceIds: string list
    Qualification: EpisodeQualification
}

// -----------------------------------------------------------------------------
// Episode summary (single JSON object)
// -----------------------------------------------------------------------------

let RepairEpisodeSummarySchemaVersion = "repair-episode-summary-v1"

type RepairEpisodeSummary = {
    SchemaVersion: string
    DeclarationsTotal: int
    ValidDeclarations: int
    InvalidDeclarations: int
    MissingCaptures: int
    MissingGitObjects: int
    DuplicateEpisodeKeys: int
    DuplicateEpisodeIds: int
    EpisodesTotal: int
    EpisodesQualified: int
    EpisodesQualifiedWithLimitations: int
    EpisodesAmbiguous: int
    EpisodesRejected: int
    ChangeSetsTotal: int
    TransitionsTotal: int
    PersistedSameCount: int
    PersistedCountDecreased: int
    PersistedCountIncreased: int
    EliminatedAfter: int
    IntroducedAfter: int
    ResolutionCandidates: int
    RegressionCandidates: int
    UnassessableTransitions: int
    VerificationEvidenceTotal: int
}

// -----------------------------------------------------------------------------
// Validation outcomes
// -----------------------------------------------------------------------------

type DeclarationIssue =
    | InvalidJson
    | MissingField of name: string
    | UnknownField of name: string
    | InvalidSchemaVersion
    | DuplicateEpisodeKey
    | DuplicateEpisodeId
    | AbsoluteDeclaredPath of path: string
    | InvalidOidFormat of oid: string * width: int
    | InvalidEpisodeKey
    | InvalidCaptureId

type DeclarationValidation = {
    Declaration: RepairEpisodeDeclaration option
    Issues: DeclarationIssue list
    Source: string option
}

type EpisodeValidation = {
    EpisodeId: string option
    Issues: string list
    Episode: RepairEpisode option
}
