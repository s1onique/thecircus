module Circus.Tooling.NoForcePush.PrePush

open System
open System.Diagnostics

// ============================================================================
// Ancestry result type
// ============================================================================

/// Result of Git ancestry check.
type AncestryResult =
    | Ancestor
    | NotAncestor
    | GitFailure of exitCode: int * stderr: string

// ============================================================================
// Git seam (injectable for testing)
// ============================================================================

/// Seam for executing Git commands. Default implementation.
type GitSeam =
    abstract member CheckAncestry: repoPath: string -> remoteOid: string -> localOid: string -> AncestryResult
    abstract member ResolveObject: repoPath: string -> oid: string -> bool
    abstract member GetObjectFormat: repoPath: string -> int option

/// Default Git process seam.
type DefaultGitSeam =
    interface GitSeam with
        member this.CheckAncestry repoPath remoteOid localOid =
            try
                let psi = ProcessStartInfo()
                psi.FileName <- "git"
                psi.Arguments <- sprintf "merge-base --is-ancestor %s %s" remoteOid localOid
                psi.WorkingDirectory <- repoPath
                psi.RedirectStandardOutput <- true
                psi.RedirectStandardError <- true
                psi.UseShellExecute <- false
                psi.CreateNoWindow <- true
                psi.Set_RedirectStandardInput(false) |> ignore

                use proc = Process.Start(psi)
                // Timeout after 30 seconds
                if not (proc.WaitForExit(30000)) then
                    proc.Kill()
                    GitFailure(-1, "timeout")
                else
                    let stderr = proc.StandardError.ReadToEnd()
                    match proc.ExitCode with
                    | 0 -> Ancestor
                    | 1 -> NotAncestor
                    | other -> GitFailure(other, stderr)
            with ex ->
                GitFailure(-1, ex.Message)

        member this.ResolveObject repoPath oid =
            try
                let psi = ProcessStartInfo()
                psi.FileName <- "git"
                psi.Arguments <- sprintf "rev-parse --verify %s^{commit}" oid
                psi.WorkingDirectory <- repoPath
                psi.RedirectStandardOutput <- true
                psi.RedirectStandardError <- true
                psi.UseShellExecute <- false
                psi.CreateNoWindow <- true
                psi.Set_RedirectStandardInput(false) |> ignore

                use proc = Process.Start(psi)
                if not (proc.WaitForExit(30000)) then
                    proc.Kill()
                    false
                else
                    proc.ExitCode = 0
            with _ ->
                false

        member this.GetObjectFormat repoPath =
            try
                let psi = ProcessStartInfo()
                psi.FileName <- "git"
                psi.Arguments <- "config core.repositoryformatversion"
                psi.WorkingDirectory <- repoPath
                psi.RedirectStandardOutput <- true
                psi.RedirectStandardError <- true
                psi.UseShellExecute <- false
                psi.CreateNoWindow <- true
                psi.Set_RedirectStandardInput(false) |> ignore

                use proc = Process.Start(psi)
                if not (proc.WaitForExit(10000)) then
                    proc.Kill()
                    None
                else
                    let output = proc.StandardOutput.ReadToEnd().Trim()
                    match Int32.TryParse(output) with
                    | true, 0 -> Some 40 // SHA-1 (40 hex chars)
                    | true, 1 -> Some 40 // SHA-1
                    | true, _ -> Some 64 // SHA-256 (64 hex chars)
                    | false, _ -> Some 40 // Default to SHA-1
            with _ ->
                None

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
    | InvalidOid of line: string * oid: string
    | UnexpectedOidWidth of line: string * expected: int * actual: int
    | MixedOidWidths of line: string * widths: int list
    | EmptyInput of unit

/// Parse a pre-push hook input line with full validation.
let parsePrePushLine
    (expectedOidWidth: int option)
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
            
            // Validate ref names (basic check for valid Git ref format)
            let isValidRef (refName: string) : bool =
                not (String.IsNullOrWhiteSpace refName) &&
                (refName.StartsWith("refs/") || refName.StartsWith("HEAD"))
            
            if not (isValidRef localRef) then
                Error(PrePushInputFailure.InvalidRefName(line, localRef))
            elif not (isValidRef remoteRef) then
                Error(PrePushInputFailure.InvalidRefName(line, remoteRef))
            else
                // Validate OID format (hex characters only)
                let isValidOid (oid: string) : bool =
                    not (String.IsNullOrWhiteSpace oid) &&
                    oid.Length >= 4 &&
                    oid.ToLowerInvariant() |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))
                
                if not (isValidOid localOid) then
                    Error(PrePushInputFailure.InvalidOid(line, localOid))
                elif not (isValidOid remoteOid) then
                    Error(PrePushInputFailure.InvalidOid(line, remoteOid))
                else
                    // Check OID widths if expected width is known
                    match expectedOidWidth with
                    | Some expected ->
                        let actualWidths = [localOid.Length; remoteOid.Length] |> List.distinct
                        if actualWidths.Length > 1 then
                            Error(PrePushInputFailure.MixedOidWidths(line, actualWidths))
                        elif actualWidths.Head <> expected && actualWidths.Head <> 40 then
                            // Allow both expected and SHA-1 (40) widths
                            Error(PrePushInputFailure.UnexpectedOidWidth(line, expected, actualWidths.Head))
                        else
                            Ok { Types.PrePushRefUpdate.LocalRef = localRef
                                 Types.PrePushRefUpdate.LocalOid = localOid
                                 Types.PrePushRefUpdate.RemoteRef = remoteRef
                                 Types.PrePushRefUpdate.RemoteOid = remoteOid }
                    | None ->
                        // No expected width known - accept what we have
                        Ok { Types.PrePushRefUpdate.LocalRef = localRef
                             Types.PrePushRefUpdate.LocalOid = localOid
                             Types.PrePushRefUpdate.RemoteRef = remoteRef
                             Types.PrePushRefUpdate.RemoteOid = remoteOid }

// ============================================================================
// Main parsing entry point
// ============================================================================

/// Parse all pre-push input from stdin with fail-closed validation.
let parsePrePushInput
    (repoPath: string)
    (gitSeam: GitSeam option)
    (input: string)
    : Result<Types.PrePushRefUpdate list, PrePushInputFailure> =
    let lines = input.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
    
    if Array.isEmpty lines then
        Ok [] // Empty input is valid (no updates proposed)
    else
        // Try to get expected OID width from repo
        let seam = defaultArg gitSeam defaultGitSeam
        let expectedWidth = seam.GetObjectFormat repoPath
        
        let results = ResizeArray<Result<Types.PrePushRefUpdate, PrePushInputFailure>>()
        
        for line in lines do
            let result = parsePrePushLine expectedWidth line
            results.Add(result)
        
        // Check for any failures (fail-closed)
        let failures = results |> Seq.choose (function Error e -> Some e | Ok _ -> None) |> List.ofSeq
        match failures with
        | [] ->
            results |> Seq.choose (function Ok u -> Some u | Error _ -> None) |> List.ofSeq |> Ok
        | first :: _ ->
            Error first

// ============================================================================
// OID helpers
// ============================================================================

/// Check if an OID is null (all zeros).
let isNullOid (oid: string) : bool =
    oid.Trim().ToLowerInvariant() = "0000000000000000000000000000000000000000" ||
    oid.Trim() = "0000000000000000000000000000000000000000" ||
    (oid.Trim().Length >= 4 && oid.Trim() |> Seq.forall (fun c -> c = '0'))

/// Check if a ref is a branch ref.
let isBranchRef (refName: string) : bool =
    refName.StartsWith("refs/heads/")

/// Check if a ref is a tag ref.
let isTagRef (refName: string) : bool =
    refName.StartsWith("refs/tags/")

/// Check if a ref is a new branch creation.
let isNewBranch (update: Types.PrePushRefUpdate) : bool =
    isBranchRef update.RemoteRef && isNullOid update.RemoteOid

/// Check if a ref update is a deletion.
let isDeletion (update: Types.PrePushRefUpdate) : bool =
    isNullOid update.LocalOid

/// Check if a ref is an existing tag.
let isExistingTagUpdate (update: Types.PrePushRefUpdate) : bool =
    isTagRef update.RemoteRef && not (isNullOid update.RemoteOid)

// ============================================================================
// Verification
// ============================================================================

/// Verify a single ref update with injectable Git seam.
let verifyUpdate
    (repoPath: string)
    (update: Types.PrePushRefUpdate)
    (gitSeam: GitSeam option)
    : Types.PrePushOutcome =
    
    let seam = defaultArg gitSeam defaultGitSeam
    
    // Reject unknown namespaces
    let isKnownNamespace =
        isBranchRef update.RemoteRef || isTagRef update.RemoteRef
    
    if not isKnownNamespace then
        Types.Rejected(update, sprintf "unknown ref namespace: %s" update.RemoteRef)
    
    // Reject all deletions
    elif isDeletion update then
        Types.Rejected(update, sprintf "deletion of remote ref not allowed: %s" update.RemoteRef)
    
    // Handle new branch creation
    elif isNewBranch update then
        // New branch: verify local OID is resolvable to a commit
        if isNullOid update.LocalOid then
            Types.OperationalFailure(update, sprintf "new branch with null OID: %s" update.LocalRef)
        elif seam.ResolveObject(repoPath, update.LocalOid) then
            Types.Allowed update
        else
            Types.OperationalFailure(update, sprintf "local OID not resolvable: %s" update.LocalOid)
    
    // Handle existing tag update
    elif isExistingTagUpdate update then
        // Existing tag replacement is not allowed
        Types.Rejected(update, sprintf "replacement of existing tag not allowed: %s" update.RemoteRef)
    
    // Handle existing branch update
    elif isBranchRef update.RemoteRef then
        // Check fast-forward
        match seam.CheckAncestry(repoPath, update.RemoteOid, update.LocalOid) with
        | Ancestor ->
            // remote is ancestor of local - fast-forward allowed
            Types.Allowed update
        | NotAncestor ->
            // remote is NOT ancestor of local - non-fast-forward rejected
            Types.Rejected(update, sprintf "non-fast-forward update blocked: %s" update.RemoteRef)
        | GitFailure(exitCode, stderr) ->
            Types.OperationalFailure(update, sprintf "git ancestry check failed (exit %d): %s" exitCode stderr)
    
    // Tag creation (new tag)
    elif isTagRef update.RemoteRef && isNullOid update.RemoteOid then
        Types.Allowed update
    
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
        // Parsing failure - return single operational failure
        [{ Types.PrePushRefUpdate.LocalRef = ""; LocalOid = ""; RemoteRef = ""; RemoteOid = "" }
         |> Types.OperationalFailure("", sprintf "input parsing failed: %A" e)]
    | Ok updates ->
        List.map (fun u -> verifyUpdate repoPath u gitSeam) updates

/// Check if any update was rejected or had operational failure.
let hasBlockingOutcome (outcomes: Types.PrePushOutcome list) : bool =
    outcomes |> List.exists (function
        | Types.Rejected _ -> true
        | Types.OperationalFailure _ -> true
        | Types.Allowed _ -> false)

/// Get all rejection reasons.
let getRejectionReasons (outcomes: Types.PrePushOutcome list) : string list =
    outcomes |> List.choose (function
        | Types.Rejected(_, reason) -> Some reason
        | Types.OperationalFailure(_, detail) -> Some(sprintf "operational failure: %s" detail)
        | Types.Allowed _ -> None)

/// Run pre-push verification and return exit code.
let runPrePush
    (repoPath: string)
    (remoteName: string)
    (remoteUrl: string)
    : int =
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
