module Circus.Tooling.NoForcePush.Types

/// Discriminated union of all no-force-push diagnostic identities.
/// Each code is stable and corresponds to a specific policy rule.
type DiagnosticId =
    /// NFP-001: Force option on Git publication (--force, -f, -uf)
    | NFP_001 of detail: string
    /// NFP-002: Force refspec using leading '+'
    | NFP_002 of detail: string
    /// NFP-003: Remote deletion option (--delete, -d)
    | NFP_003 of detail: string
    /// NFP-004: Empty-source deletion refspec (:refs/heads/x)
    | NFP_004 of detail: string
    /// NFP-005: Mirror or prune publication
    | NFP_005 of detail: string
    /// NFP-006: Hook or verification bypass
    | NFP_006 of detail: string
    /// NFP-007: Dynamic or unclassifiable push arguments
    | NFP_007 of detail: string
    /// NFP-008: Low-level send-pack invocation
    | NFP_008 of detail: string
    /// NFP-009: GitHub/API forced ref mutation
    | NFP_009 of detail: string
    /// NFP-010: GitHub/API ref deletion
    | NFP_010 of detail: string
    /// NFP-011: Unclassified executable surface
    | NFP_011 of path: string
    /// NFP-012: Doctrine or required-file drift
    | NFP_012 of detail: string
    /// NFP-013: Inventory or parser operational failure
    | NFP_013 of detail: string

    member this.RuleId: string =
        match this with
        | NFP_001 _ -> "NFP-001"
        | NFP_002 _ -> "NFP-002"
        | NFP_003 _ -> "NFP-003"
        | NFP_004 _ -> "NFP-004"
        | NFP_005 _ -> "NFP-005"
        | NFP_006 _ -> "NFP-006"
        | NFP_007 _ -> "NFP-007"
        | NFP_008 _ -> "NFP-008"
        | NFP_009 _ -> "NFP-009"
        | NFP_010 _ -> "NFP-010"
        | NFP_011 _ -> "NFP-011"
        | NFP_012 _ -> "NFP-012"
        | NFP_013 _ -> "NFP-013"

    member this.Detail: string =
        match this with
        | NFP_001 d | NFP_002 d | NFP_003 d | NFP_004 d
        | NFP_005 d | NFP_006 d | NFP_007 d | NFP_008 d
        | NFP_009 d | NFP_010 d | NFP_012 d | NFP_013 d -> d
        | NFP_011 p -> sprintf "unclassified executable: %s" p

/// Surface kinds that may contain governed publication commands.
type SurfaceKind =
    | Executable
    | Workflow
    | Make
    | Container
    | AgentExecutable

/// Parser kinds for command extraction.
type ParserKind =
    | Shell
    | Make
    | YamlRun
    | Dockerfile
    | PlaintextCommand

/// Authority level of the surface.
type AuthorityLevel =
    | Repository
    | GitHub

/// A row from the surface inventory CSV.
type SurfaceEntry = {
    Path: string
    SurfaceKind: SurfaceKind
    ParserKind: ParserKind
    Authority: AuthorityLevel
    Reason: string
}

/// A structured diagnostic finding from the static verifier.
type Diagnostic = {
    Id: DiagnosticId
    Path: string
    Line: int
    Column: int
    NormalizedCommand: string
}

    member this.RuleId = this.Id.RuleId
    member this.Detail = this.Id.Detail

/// Result of static policy verification.
type StaticPolicyResult = {
    RepositoryRoot: string
    FilesExamined: int
    Diagnostics: Diagnostic list
    OperationalErrors: string list
}

/// Pre-push ref update record.
type PrePushRefUpdate = {
    LocalRef: string
    LocalOid: string
    RemoteRef: string
    RemoteOid: string
}

/// Pre-push verification outcome.
type PrePushOutcome =
    | Allowed of refUpdate: PrePushRefUpdate
    | Rejected of refUpdate: PrePushRefUpdate * reason: string
    | OperationalFailure of refUpdate: PrePushRefUpdate * detail: string

/// GitHub branch rule for no-force-push enforcement.
type GitHubBranchRule = {
    RepositoryId: string
    BranchName: string
    EnforcementActive: bool
    BlocksNonFastForward: bool
    BlocksDeletion: bool
    BypassActors: string list
    CheckedAt: System.DateTimeOffset
}

/// GitHub ruleset verification result.
type GitHubRulesResult =
    | RulesVerified of rule: GitHubBranchRule * sha256: string
    | RulesMissingNonFastForwardRule of rule: GitHubBranchRule
    | RulesMissingDeletionRule of rule: GitHubBranchRule
    | RulesEnforcementInactive of rule: GitHubBranchRule
    | RulesHasBypassActor of rule: GitHubBranchRule * actor: string
    | RepositoryMismatch of expected: string * actual: string
    | BranchMismatch of expected: string * actual: string
    | MalformedJson of detail: string
    | NetworkFailure of detail: string
    | AuthFailure of detail: string
    | UnknownError of detail: string

/// Exit code contract for pre-push verifier.
module ExitCode =
    let pass = 0
    let policyFailure = 1
    let operationalError = 2
