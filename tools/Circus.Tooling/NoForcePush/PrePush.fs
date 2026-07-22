module Circus.Tooling.NoForcePush.PrePush

open System
open Circus.Tooling.NoForcePush.BoundedProcess

// ============================================================================
// Object format model
// ============================================================================

/// Git object storage format.
type ObjectFormat =
    | Sha1
    | Sha256

    member this.OidWidth =
        match this with
        | Sha1 -> 40
        | Sha256 -> 64

/// Failure result for Git operations.
type GitOperationFailure =
    | FormatDetectionFailed of detail: string
    | ObjectNotFound of oid: string * exitCode: int * stderr: string
    | CommitNotFound of oid: string * exitCode: int * stderr: string
    | AncestryCheckFailed of exitCode: int * stderr: string
    | OperationFailed of detail: string

// ============================================================================
// Git seam (injectable for testing)
// ============================================================================

/// Seam for executing Git commands. Default implementation.
type GitSeam =
    abstract member GetObjectFormat: repoPath: string -> Result<ObjectFormat, GitOperationFailure>
    abstract member ResolveCommit: repoPath: string -> oid: string -> Result<unit, GitOperationFailure>
    abstract member ResolveObject: repoPath: string -> oid: string -> Result<unit, GitOperationFailure>
    abstract member CheckAncestry: repoPath: string -> remoteOid: string -> localOid: string -> Result<bool, GitOperationFailure>

/// Default Git process seam using BoundedProcess.
type DefaultGitSeam() =
    interface GitSeam with
        member this.GetObjectFormat repoPath =
            match BoundedProcess.runGitQuery repoPath [ "rev-parse"; "--show-object-format=storage" ] 10000 with
            | Ok output ->
                // EXACT matching only - case-sensitive, exact tokens only
                match output.Trim() with
                | "sha1" -> Ok Sha1
                | "sha256" -> Ok Sha256
                | unexpected ->
                    Error(FormatDetectionFailed(sprintf "Unknown object format: '%s'" unexpected))
            | Error e ->
                Error(FormatDetectionFailed(sprintf "BoundedProcess failure: %A" e))

        member this.ResolveCommit repoPath oid =
            match BoundedProcess.runGitWithExit repoPath [ "rev-parse"; "--verify"; "--end-of-options"; sprintf "%s^{commit}" oid ] BoundedProcess.defaultTimeoutMs with
            | Ok completion ->
                match completion.ExitCode with
                | 0 -> Ok()
                | other ->
                    Error(CommitNotFound(oid, other, completion.Stderr))
            | Error e ->
                Error(OperationFailed(sprintf "BoundedProcess failure: %A" e))

        member this.ResolveObject repoPath oid =
            match BoundedProcess.runGitWithExit repoPath [ "cat-file"; "-e"; sprintf "%s^{object}" oid ] BoundedProcess.defaultTimeoutMs with
            | Ok completion ->
                match completion.ExitCode with
                | 0 -> Ok()
                | other ->
                    Error(ObjectNotFound(oid, other, completion.Stderr))
            | Error e ->
                Error(OperationFailed(sprintf "BoundedProcess failure: %A" e))

        member this.CheckAncestry repoPath remoteOid localOid =
            match BoundedProcess.runGitWithExit repoPath [ "merge-base"; "--is-ancestor"; remoteOid; localOid ] BoundedProcess.defaultTimeoutMs with
            | Ok completion ->
                match completion.ExitCode with
                | 0 -> Ok true // remote is ancestor of local
                | 1 -> Ok false // remote is NOT ancestor of local
                | other ->
                    Error(AncestryCheckFailed(other, completion.Stderr))
            | Error e ->
                Error(OperationFailed(sprintf "BoundedProcess failure: %A" e))

/// Default seam instance.
let defaultGitSeam = DefaultGitSeam() :> GitSeam

// ============================================================================
// Input parsing (fail-closed)
// ============================================================================

/// Failures that can occur during pre-push input parsing.
type PrePushInputFailure =
    | MalformedLine of line: string * detail: string
    | WrongFieldCount of line: string * count: int
    | InvalidRefName of line: string * refName: string
    | InvalidOid of line: string * oid: string * reason: string
    | UnexpectedOidWidth of line: string * expected: int * actual: int
    | MixedOidWidths of line: string * widths: int list
    | EmptyInput of unit
    | FormatDetectionFailed of detail: string

/// Parsed pre-push input - no synthetic authority for empty input.
type ParsedPrePushInput =
    | NoUpdates
    | ProposedUpdates of objectFormat: ObjectFormat * updates: Types.PrePushRefUpdate list

/// Check if an OID is exactly all zeros of the given width.
let isExactNullOid (oid: string) (width: int) =
    oid.Length = width && oid |> Seq.forall ((=) '0')

/// Check if an OID is valid hexadecimal of exactly the given width.
let isValidOid (oid: string) (width: int) =
    oid.Length = width && oid |> Seq.forall (fun c ->
        (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))

/// Parse a pre-push hook input line with full validation.
let parsePrePushLine
    (format: ObjectFormat)
    (line: string)
    : Result<Types.PrePushRefUpdate, PrePushInputFailure> =
    let trimmed = line.Trim()

    if String.IsNullOrEmpty trimmed then
        Error(PrePushInputFailure.MalformedLine(line, "empty or whitespace"))
    else
        let parts = trimmed.Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)

        if parts.Length <> 4 then
            Error(PrePushInputFailure.WrongFieldCount(line, parts.Length))
        else
            let localRef, localOid, remoteRef, remoteOid = parts.[0], parts.[1], parts.[2], parts.[3]
            let expectedWidth = format.OidWidth

            // Validate ref names (basic check for valid Git ref format)
            let isValidRef (refName: string) =
                not (String.IsNullOrWhiteSpace refName)
                && (refName.StartsWith("refs/") || refName.StartsWith("HEAD"))

            if not (isValidRef localRef) then
                Error(PrePushInputFailure.InvalidRefName(line, localRef))
            elif not (isValidRef remoteRef) then
                Error(PrePushInputFailure.InvalidRefName(line, remoteRef))
            else
                // Validate OID format - must be exact width
                let localValid = isValidOid localOid expectedWidth
                let remoteValid = isValidOid remoteOid expectedWidth

                if not localValid && not (isExactNullOid localOid expectedWidth) then
                    Error(PrePushInputFailure.InvalidOid(line, localOid, sprintf "must be %d hex chars or %d zeros" expectedWidth expectedWidth))
                elif not remoteValid && not (isExactNullOid remoteOid expectedWidth) then
                    Error(PrePushInputFailure.InvalidOid(line, remoteOid, sprintf "must be %d hex chars or %d zeros" expectedWidth expectedWidth))
                else
                    // Check for mixed widths (all must be same width - already enforced by exact width above)
                    Ok { Types.PrePushRefUpdate.LocalRef = localRef
                         Types.PrePushRefUpdate.LocalOid = localOid
                         Types.PrePushRefUpdate.RemoteRef = remoteRef
                         Types.PrePushRefUpdate.RemoteOid = remoteOid }

// ============================================================================
// Main parsing entry point - binds format once for nonempty input
// ============================================================================

/// Parse all pre-push input from stdin with fail-closed validation.
/// Empty input returns NoUpdates (no synthetic format claim).
/// Nonempty input queries the repository format exactly once.
let parsePrePushInput
    (repoPath: string)
    (gitSeam: GitSeam option)
    (input: string)
    : Result<ParsedPrePushInput, PrePushInputFailure> =
    let lines = input.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)

    if Array.isEmpty lines then
        // Empty input is valid - no format authority claimed
        Ok NoUpdates
    else
        // Get authoritative object format from repository - exactly once
        let seam = defaultArg gitSeam defaultGitSeam
        match seam.GetObjectFormat repoPath with
        | Error e ->
            Error(FormatDetectionFailed(sprintf "Object format detection failed: %A" e))
        | Ok format ->
            let results = ResizeArray<Result<Types.PrePushRefUpdate, PrePushInputFailure>>()

            for line in lines do
                // Use the already-bound format for each line
                let result = parsePrePushLine format line
                results.Add(result)

            // Check for any failures (fail-closed)
            let failures =
                results
                |> Seq.choose (function
                    | Error e -> Some e
                    | Ok _ -> None)
                |> List.ofSeq

            match failures with
            | [] ->
                let updates =
                    results
                    |> Seq.choose (function
                        | Ok u -> Some u
                        | Error _ -> None)
                    |> List.ofSeq
                Ok(ProposedUpdates(format, updates))
            | first :: _ -> Error first

// ============================================================================
// OID helpers
// ============================================================================

/// Check if a ref is a branch ref.
let isBranchRef (refName: string) = refName.StartsWith("refs/heads/")

/// Check if a ref is a tag ref.
let isTagRef (refName: string) = refName.StartsWith("refs/tags/")

/// Check if a ref update is a deletion (all-zeros local OID).
let isDeletion (update: Types.PrePushRefUpdate) (format: ObjectFormat) =
    isExactNullOid update.LocalOid format.OidWidth

/// Check if a ref is an existing tag update.
let isExistingTagUpdate (update: Types.PrePushRefUpdate) (format: ObjectFormat) =
    isTagRef update.RemoteRef && not (isExactNullOid update.RemoteOid format.OidWidth)

/// Check if a ref is a new tag creation.
let isNewTag (update: Types.PrePushRefUpdate) (format: ObjectFormat) =
    isTagRef update.RemoteRef && isExactNullOid update.RemoteOid format.OidWidth

/// Check if a ref is a new branch creation.
let isNewBranch (update: Types.PrePushRefUpdate) (format: ObjectFormat) =
    isBranchRef update.RemoteRef && isExactNullOid update.RemoteOid format.OidWidth

// ============================================================================
// Verification - accepts already-bound format
// ============================================================================

/// Verify a single ref update with injectable Git seam.
/// Takes the already-bound format to avoid repeated GetObjectFormat calls.
let verifyUpdate
    (repoPath: string)
    (objectFormat: ObjectFormat)
    (update: Types.PrePushRefUpdate)
    (gitSeam: GitSeam option)
    : Types.PrePushOutcome =

    let seam = defaultArg gitSeam defaultGitSeam

    // Reject unknown namespaces first
    if not (isBranchRef update.RemoteRef || isTagRef update.RemoteRef) then
        Types.Rejected(update, sprintf "unknown ref namespace: %s" update.RemoteRef)
    else
        // Check for deletion first
        if isDeletion update objectFormat then
            Types.Rejected(update, sprintf "deletion of remote ref not allowed: %s" update.RemoteRef)
        // Handle new branch creation
        elif isNewBranch update objectFormat then
            // New branch: verify local OID is a valid commit
            if isExactNullOid update.LocalOid objectFormat.OidWidth then
                Types.OperationalFailure(update, sprintf "new branch with null OID: %s" update.LocalRef)
            else
                match seam.ResolveCommit repoPath update.LocalOid with
                | Ok() ->
                    Types.Allowed update
                | Error(CommitNotFound _) ->
                    Types.OperationalFailure(update, sprintf "local OID is not a commit: %s" update.LocalOid)
                | Error e ->
                    Types.OperationalFailure(update, sprintf "commit resolution failed: %A" e)
        // Handle existing tag update (rejection - do not resolve)
        elif isExistingTagUpdate update objectFormat then
            // Existing tag replacement is not allowed
            Types.Rejected(update, sprintf "replacement of existing tag not allowed: %s" update.RemoteRef)
        // Handle new tag creation
        elif isNewTag update objectFormat then
            // New tag: verify local OID is an existing Git object
            if isExactNullOid update.LocalOid objectFormat.OidWidth then
                Types.OperationalFailure(update, sprintf "new tag with null OID: %s" update.LocalRef)
            else
                match seam.ResolveObject repoPath update.LocalOid with
                | Ok() ->
                    Types.Allowed update
                | Error(ObjectNotFound _) ->
                    Types.OperationalFailure(update, sprintf "local OID is not a Git object: %s" update.LocalOid)
                | Error e ->
                    Types.OperationalFailure(update, sprintf "object resolution failed: %A" e)
        // Handle existing branch update
        elif isBranchRef update.RemoteRef then
            // Check fast-forward
            match seam.CheckAncestry repoPath update.RemoteOid update.LocalOid with
            | Ok true ->
                // remote is ancestor of local - fast-forward allowed
                Types.Allowed update
            | Ok false ->
                // remote is NOT ancestor of local - non-fast-forward rejected
                Types.Rejected(update, sprintf "non-fast-forward update blocked: %s" update.RemoteRef)
            | Error(AncestryCheckFailed(exitCode, stderr)) ->
                Types.OperationalFailure(update, sprintf "git ancestry check failed (exit %d): %s" exitCode stderr)
            | Error e ->
                Types.OperationalFailure(update, sprintf "ancestry check failed: %A" e)
        else
            Types.Rejected(update, sprintf "unsupported ref update: %s" update.RemoteRef)

/// Verify all pre-push updates from stdin.
let verifyPrePush
    (repoPath: string)
    (remoteName: string)
    (remoteUrl: string)
    (gitSeam: GitSeam option)
    : Types.PrePushOutcome list =
    // Read stdin
    let stdin = Console.In.ReadToEnd()

    match parsePrePushInput repoPath gitSeam stdin with
    | Error e ->
        // Format detection or parsing failure - return single operational failure
        let emptyUpdate: Types.PrePushRefUpdate =
            { Types.PrePushRefUpdate.LocalRef = ""
              Types.PrePushRefUpdate.LocalOid = ""
              Types.PrePushRefUpdate.RemoteRef = ""
              Types.PrePushRefUpdate.RemoteOid = "" }

        [ Types.OperationalFailure(emptyUpdate, sprintf "input parsing failed: %A" e) ]
    | Ok NoUpdates ->
        // No updates proposed - this is valid, return empty list
        []
    | Ok(ProposedUpdates(objectFormat, updates)) ->
        // Use the already-bound format for all updates
        List.map (fun u -> verifyUpdate repoPath objectFormat u gitSeam) updates

/// Check if any update was rejected or had operational failure.
let hasBlockingOutcome (outcomes: Types.PrePushOutcome list) =
    outcomes
    |> List.exists (function
        | Types.Rejected _ -> true
        | Types.OperationalFailure _ -> true
        | Types.Allowed _ -> false)

/// Get all rejection reasons.
let getRejectionReasons (outcomes: Types.PrePushOutcome list) =
    outcomes
    |> List.choose (function
        | Types.Rejected(_, reason) -> Some reason
        | Types.OperationalFailure(_, detail) -> Some(sprintf "operational failure: %s" detail)
        | Types.Allowed _ -> None)

/// Run pre-push verification and return exit code.
let runPrePush (repoPath: string) (remoteName: string) (remoteUrl: string) =
    let outcomes = verifyPrePush repoPath remoteName remoteUrl None

    if List.isEmpty outcomes then
        // No updates proposed - this is valid
        0
    elif hasBlockingOutcome outcomes then
        // At least one blocking outcome
        let reasons = getRejectionReasons outcomes

        for reason in reasons do
            stderr.WriteLine(sprintf "pre-push rejected: %s" reason)

        // Check if any was operational failure
        if outcomes |> List.exists (function Types.OperationalFailure _ -> true | _ -> false) then
            2
        else
            1
    else
        // All allowed
        0
