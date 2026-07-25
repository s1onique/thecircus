module Circus.Tooling.CanonicalEvidence.Provider

// =============================================================================
// Canonical evidence – provider (slices 4–6)
//
// ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER-FOUNDATION01-CORRECTION01
//
// This module composes the existing repository authorities to
// produce canonical evidence:
//
//   * identity resolution flows through the bounded Git adapter
//     ``Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Git``;
//   * every check flows through
//     ``Circus.Tooling.FSharpDiagnostics.RepairEpisodes.BoundedProcess.run``;
//   * generation is atomic and fail-closed: a failed regeneration
//     preserves the previous artifact byte-identically.
//
// The provider owns evidence orchestration, NOT process lifecycle
// management. There is no use of ``Process.Start``,
// ``DataReceivedEventHandler``, ``BeginOutputReadLine``,
// ``BeginErrorReadLine``, ``WaitForExit``, ``Kill``,
// ``StandardOutput.BaseStream``, ``StandardError.BaseStream``, or
// ``Task.Run`` stream readers in this module.
// =============================================================================

open System
open System.IO
open System.Threading

open Circus.Tooling.FSharpDiagnostics.Hashing
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.BoundedProcess
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Git

open Circus.Tooling.CanonicalEvidence.Domain
open Circus.Tooling.CanonicalEvidence.Serialization
open Circus.Tooling.CanonicalEvidence.Validation

// -----------------------------------------------------------------------------
// Identity resolution
// -----------------------------------------------------------------------------

type IdentityFailure =
    | IdentityRepositoryNotFound of path: string
    | IdentityGitFailure of detail: string
    | IdentityUnsupportedFormat of token: string
    | IdentityInvalidOid of oid: string * objectFormat: string
    | IdentityDirtyWorktree

type ResolvedIdentity = {
    CommitOid: string
    TreeOid: string
    ObjectFormat: string
}

let identityFailureToString (f: IdentityFailure) : string =
    match f with
    | IdentityRepositoryNotFound path -> sprintf "repository not found: %s" path
    | IdentityGitFailure detail -> sprintf "git identity failure: %s" detail
    | IdentityUnsupportedFormat token -> sprintf "unsupported object format: %s" token
    | IdentityInvalidOid (oid, fmt) -> sprintf "invalid %s OID: %s" fmt oid
    | IdentityDirtyWorktree -> "working tree is dirty (regeneration requires a clean tree)"

let private runGit (repoRoot: string) (args: string list) =
    runGitTyped repoRoot defaultGitRunOptions args

let resolveIdentity (repoRoot: string) : Result<ResolvedIdentity, IdentityFailure> =
    if String.IsNullOrWhiteSpace repoRoot then
        Result.Error(IdentityRepositoryNotFound repoRoot)
    elif not (Directory.Exists repoRoot) then
        Result.Error(IdentityRepositoryNotFound repoRoot)
    else
        // Step 1: detect object format
        match runGit repoRoot [ "rev-parse"; "--show-object-format=storage" ] with
        | Error err ->
            Result.Error(IdentityGitFailure(sprintf "object-format: %A" err))
        | Ok fmtRun ->
            if fmtRun.ExitCode <> 0 then
                Result.Error(IdentityGitFailure(sprintf "object-format exit %d: %s" fmtRun.ExitCode fmtRun.Stderr))
            else
                let formatToken = fmtRun.Stdout.Trim()
                match parseObjectFormat formatToken with
                | None -> Result.Error(IdentityUnsupportedFormat formatToken)
                | Some fmtStr ->
                    // Step 2: resolve commit
                    match runGit repoRoot [ "rev-parse"; "--verify"; "--end-of-options"; "HEAD^{commit}" ] with
                    | Error err ->
                        Result.Error(IdentityGitFailure(sprintf "commit: %A" err))
                    | Ok commitRun ->
                        if commitRun.ExitCode <> 0 then
                            Result.Error(IdentityGitFailure(sprintf "commit exit %d: %s" commitRun.ExitCode commitRun.Stderr))
                        else
                            let commit = commitRun.Stdout.Trim()
                            if not (isValidOid fmtStr commit) then
                                Result.Error(IdentityInvalidOid(commit, fmtStr))
                            else
                                // Step 3: resolve tree
                                match runGit repoRoot [ "rev-parse"; "--verify"; "--end-of-options"; "HEAD^{tree}" ] with
                                | Error err ->
                                    Result.Error(IdentityGitFailure(sprintf "tree: %A" err))
                                | Ok treeRun ->
                                    if treeRun.ExitCode <> 0 then
                                        Result.Error(IdentityGitFailure(sprintf "tree exit %d: %s" treeRun.ExitCode treeRun.Stderr))
                                    else
                                        let tree = treeRun.Stdout.Trim()
                                        if not (isValidOid fmtStr tree) then
                                            Result.Error(IdentityInvalidOid(tree, fmtStr))
                                        else
                                            // Step 4: reject dirty worktree
                                            match runGit repoRoot [ "status"; "--porcelain=v1" ] with
                                            | Error err ->
                                                Result.Error(IdentityGitFailure(sprintf "status: %A" err))
                                            | Ok statusRun ->
                                                if statusRun.ExitCode <> 0 then
                                                    Result.Error(IdentityGitFailure(sprintf "status exit %d: %s" statusRun.ExitCode statusRun.Stderr))
                                                elif not (String.IsNullOrEmpty(statusRun.Stdout.Trim())) then
                                                    Result.Error IdentityDirtyWorktree
                                                else
                                                    Result.Ok {
                                                        CommitOid = commit
                                                        TreeOid = tree
                                                        ObjectFormat = fmtStr
                                                    }

// -----------------------------------------------------------------------------
// Failure translation
// -----------------------------------------------------------------------------

let boundedFailureKind (failure: BoundedProcessFailure) : string =
    match failure with
    | InvalidRequest detail -> sprintf "invalid_request:%s" detail
    | LaunchFailed (_, detail) -> sprintf "launch_failed:%s" detail
    | TimedOut _ -> "timed_out"
    | Cancelled -> "cancelled"
    | StdoutLimitExceeded limit -> sprintf "stdout_limit_exceeded:%d" limit
    | StderrLimitExceeded limit -> sprintf "stderr_limit_exceeded:%d" limit
    | NonZeroExit (code, _, _) -> sprintf "non_zero_exit:%d" code
    | StdoutReaderFailed detail -> sprintf "stdout_reader_failed:%s" detail
    | StderrReaderFailed detail -> sprintf "stderr_reader_failed:%s" detail
    | WaitFailed detail -> sprintf "wait_failed:%s" detail
    | KillFailed detail -> sprintf "kill_failed:%s" detail
    | IncompleteOutput _ -> "incomplete_output"
    | TerminationCleanupFailed _ -> "termination_cleanup_failed"

let mapFailureToStatus (failure: BoundedProcessFailure) : EvidenceStatus =
    match failure with
    | NonZeroExit _ -> Fail
    | _ -> Unavailable

// -----------------------------------------------------------------------------
// Check execution
// -----------------------------------------------------------------------------

/// Run a single check definition through BoundedProcess.run and
/// translate the result into an ``EvidenceCheckResult``.
///
/// The function is a thin adapter around the bounded process
/// surface: no ``Process.Start``, no event handlers, no stream
/// reader races. The exit code is the only authority for
/// ``Pass``/``Fail``; every other bounded-process failure is
/// translated to ``Unavailable`` with a distinct
/// ``FailureKind`` token.
let runCheck (def: EvidenceCheckDefinition) : EvidenceCheckResult =
    let request: BoundedProcessRequest = {
        Executable = def.Executable
        WorkingDirectory = def.WorkingDirectory
        Arguments = def.Arguments
        Environment = []
        Limits = {
            Timeout = def.Timeout
            StdoutLimitBytes = def.StdoutLimitBytes
            StderrLimitBytes = def.StderrLimitBytes
        }
    }
    let start = DateTimeOffset.UtcNow
    let result =
        run request CancellationToken.None
        |> Async.AwaitTask
        |> Async.RunSynchronously
    let elapsed = DateTimeOffset.UtcNow - start
    let durationMs =
        let ms = elapsed.Ticks / TimeSpan.TicksPerMillisecond
        if ms < 0L then 0L else ms
    let fullArgv = def.Executable :: def.Arguments
    match result with
    | Ok success ->
        let stdoutHash = sha256Hex success.Stdout
        let stderrHash = sha256Hex success.Stderr
        let status = if success.ExitCode = 0 then Pass else Fail
        {
            Id = def.Id
            CommandArgv = fullArgv
            WorkingDirectory = def.WorkingDirectory
            DurationMilliseconds = durationMs
            ExitCode = Some success.ExitCode
            Status = status
            StdoutSha256 = Some stdoutHash
            StderrSha256 = Some stderrHash
            FailureKind =
                if status = Fail then
                    Some(sprintf "non_zero_exit:%d" success.ExitCode)
                else None
        }
    | Error failure ->
        let status = mapFailureToStatus failure
        {
            Id = def.Id
            CommandArgv = fullArgv
            WorkingDirectory = def.WorkingDirectory
            DurationMilliseconds = durationMs
            ExitCode = None
            Status = status
            StdoutSha256 = None
            StderrSha256 = None
            FailureKind = Some(boundedFailureKind failure)
        }

let runAllChecks (defs: EvidenceCheckDefinition list) : EvidenceCheckResult list =
    defs
    |> List.map runCheck
    |> sortChecksDeterministic

// -----------------------------------------------------------------------------
// Canonical check definitions
//
// The 9 checks registered in this correction. The ``baselineCommit``
/// argument is the canonical baseline that defines the
/// ``committed-range-diff-check`` and ``protected-scope`` ranges.
/// -----------------------------------------------------------------------------

let CanonicalCheckDefinitions (repoRoot: string) (baselineCommit: string) : EvidenceCheckDefinition list =
    let workDir = repoRoot
    let longTimeout = TimeSpan.FromMinutes(60.0)
    let shortTimeout = TimeSpan.FromMinutes(15.0)
    let quickTimeout = TimeSpan.FromMinutes(5.0)
    let stdOut = 32 * 1024 * 1024
    let stdErr = 32 * 1024 * 1024

    let protectedScopePaths =
        [
            "tools/Circus.Tooling/NoForcePush/"
            "src/Circus.Persistence.Postgres/"
            "tests/Circus.Persistence.Postgres.Tests/"
            "factory/evidence/fsharp-diagnostics/corpus/raw/"
        ]

    let protectedScopeArgs =
        let prefix = [ "diff"; "--quiet"; "--exit-code"; baselineCommit + "..HEAD"; "--" ]
        prefix @ protectedScopePaths

    [
        {
            Id = "tooling-build"
            Executable = "dotnet"
            Arguments = [ "build"; "tools/Circus.Tooling/Circus.Tooling.fsproj"; "-c"; "Release"; "--no-restore" ]
            WorkingDirectory = workDir
            Required = true
            Timeout = shortTimeout
            StdoutLimitBytes = stdOut
            StderrLimitBytes = stdErr
        }
        {
            Id = "tooling-tests-build"
            Executable = "dotnet"
            Arguments = [ "build"; "tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj"; "-c"; "Release"; "--no-restore" ]
            WorkingDirectory = workDir
            Required = true
            Timeout = shortTimeout
            StdoutLimitBytes = stdOut
            StderrLimitBytes = stdErr
        }
        {
            Id = "bounded-process-tests"
            Executable = "dotnet"
            Arguments = [ "run"; "--project"; "tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj"; "-c"; "Release"; "--no-build"; "--no-restore"; "--"; "--summary"; "--filter-test-list"; "FSharpDiagnostics.RepairEpisodes.BoundedProcess" ]
            WorkingDirectory = workDir
            Required = true
            Timeout = longTimeout
            StdoutLimitBytes = stdOut
            StderrLimitBytes = stdErr
        }
        {
            Id = "git-adapter-tests"
            Executable = "dotnet"
            Arguments = [ "run"; "--project"; "tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj"; "-c"; "Release"; "--no-build"; "--no-restore"; "--"; "--summary"; "--filter-test-list"; "FSharpDiagnostics.RepairEpisodes.GitAdapter" ]
            WorkingDirectory = workDir
            Required = true
            Timeout = longTimeout
            StdoutLimitBytes = stdOut
            StderrLimitBytes = stdErr
        }
        {
            Id = "repair-episodes-tests"
            Executable = "dotnet"
            Arguments = [ "run"; "--project"; "tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj"; "-c"; "Release"; "--no-build"; "--no-restore"; "--"; "--summary"; "--filter-test-list"; "FSharpDiagnostics.RepairEpisodes" ]
            WorkingDirectory = workDir
            Required = true
            Timeout = longTimeout
            StdoutLimitBytes = stdOut
            StderrLimitBytes = stdErr
        }
        {
            Id = "fsharp-diagnostics-tests"
            Executable = "dotnet"
            Arguments = [ "run"; "--project"; "tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj"; "-c"; "Release"; "--no-build"; "--no-restore"; "--"; "--summary"; "--filter-test-list"; "FSharpDiagnostics" ]
            WorkingDirectory = workDir
            Required = true
            Timeout = longTimeout
            StdoutLimitBytes = stdOut
            StderrLimitBytes = stdErr
        }
        {
            Id = "repair-episodes-gate"
            Executable = "make"
            Arguments = [ "gate-fsharp-repair-episodes" ]
            WorkingDirectory = workDir
            Required = true
            Timeout = shortTimeout
            StdoutLimitBytes = stdOut
            StderrLimitBytes = stdErr
        }
        {
            Id = "committed-range-diff-check"
            Executable = "git"
            Arguments = [ "diff"; "--check"; baselineCommit + "..HEAD" ]
            WorkingDirectory = workDir
            Required = true
            Timeout = quickTimeout
            StdoutLimitBytes = stdOut
            StderrLimitBytes = stdErr
        }
        {
            Id = "protected-scope"
            Executable = "git"
            Arguments = protectedScopeArgs
            WorkingDirectory = workDir
            Required = true
            Timeout = quickTimeout
            StdoutLimitBytes = stdOut
            StderrLimitBytes = stdErr
        }
    ]

// -----------------------------------------------------------------------------
// Generation
// -----------------------------------------------------------------------------

type GenerateFailure =
    | IdentityFailure of IdentityFailure
    | CheckListEmpty
    | UnexpectedCheckId of id: string

let generateFailureToString (f: GenerateFailure) : string =
    match f with
    | IdentityFailure id -> sprintf "identity: %s" (identityFailureToString id)
    | CheckListEmpty -> "canonical check list is empty"
    | UnexpectedCheckId id -> sprintf "unexpected check id: %s" id

let buildCanonicalEvidence (identity: ResolvedIdentity) (checks: EvidenceCheckResult list) : CanonicalEvidence =
    let sortedChecks = sortChecksDeterministic checks
    let overallStatus = computeOverallStatus sortedChecks
    let doc = {
        SchemaVersion = SchemaVersionValue
        ProviderName = ProviderNameValue
        ProviderVersion = ProviderVersionValue
        TestedCommitOid = identity.CommitOid
        TestedTreeOid = identity.TreeOid
        ObjectFormat = identity.ObjectFormat
        Checks = sortedChecks
        OverallStatus = overallStatus
        SemanticSha256 = ""
    }
    withSemanticHash doc

let runCanonicalChecks (defs: EvidenceCheckDefinition list) : Result<EvidenceCheckResult list, GenerateFailure> =
    if List.isEmpty defs then
        Result.Error CheckListEmpty
    else
        let known = SupportedCheckIdSet
        let mutable badId : string option = None
        for d in defs do
            if not (Set.contains d.Id known) then
                badId <- Some d.Id
        match badId with
        | Some id -> Result.Error(UnexpectedCheckId id)
        | None ->
            let checks = runAllChecks defs
            Result.Ok checks

let generate
    (repoRoot: string)
    (baselineCommit: string)
    : Result<CanonicalEvidence, GenerateFailure> =
    match resolveIdentity repoRoot with
    | Result.Error err -> Result.Error(IdentityFailure err)
    | Result.Ok identity ->
        let defs = CanonicalCheckDefinitions repoRoot baselineCommit
        match runCanonicalChecks defs with
        | Result.Error err -> Result.Error err
        | Result.Ok checks -> Result.Ok(buildCanonicalEvidence identity checks)

// -----------------------------------------------------------------------------
// Atomic write
// -----------------------------------------------------------------------------

type WriteFailure =
    | DirectoryCreationFailed of message: string
    | SerializationFailed of message: string
    | TempWriteFailed of message: string
    | ReplacementFailed of message: string
    | ReReadFailed of message: string
    | PostReadValidationFailed of issues: string list

let writeFailureToString (f: WriteFailure) : string =
    match f with
    | DirectoryCreationFailed msg -> sprintf "directory creation failed: %s" msg
    | SerializationFailed msg -> sprintf "serialization failed: %s" msg
    | TempWriteFailed msg -> sprintf "temp write failed: %s" msg
    | ReplacementFailed msg -> sprintf "atomic replacement failed: %s" msg
    | ReReadFailed msg -> sprintf "post-write re-read failed: %s" msg
    | PostReadValidationFailed issues -> sprintf "post-write validation failed: %s" (String.concat "; " issues)

let private tryCreateDirectory (dir: string) : Result<unit, WriteFailure> =
    if String.IsNullOrEmpty dir then
        Result.Ok ()
    elif Directory.Exists dir then
        Result.Ok ()
    else
        try
            Directory.CreateDirectory dir |> ignore
            Result.Ok ()
        with ex ->
            Result.Error(DirectoryCreationFailed(sprintf "%s: %s" (ex.GetType().Name) ex.Message))

let private safeDelete (path: string) : unit =
    try if File.Exists path then File.Delete path with _ -> ()

let private snapshotFileBytes (path: string) : byte array option =
    if File.Exists path then Some(File.ReadAllBytes path) else None

let private byteIdentical (a: byte array option) (b: byte array option) : bool =
    match a, b with
    | None, None -> true
    | Some x, Some y ->
        if x.Length <> y.Length then false
        else
            let mutable ok = true
            for i in 0 .. x.Length - 1 do
                if x.[i] <> y.[i] then ok <- false
            ok
    | _ -> false

type WriteOutcome = {
    Success: bool
    Path: string
    CanonicalSha256: string
    PreviousBytes: byte array option
    CurrentBytes: byte array
    Failure: WriteFailure option
    CanonicalByteIdenticalAfterFailure: bool
}

/// Write the canonical evidence to ``path`` atomically. The write
/// follows the same contract as the rest of the project:
///
///  1. snapshot the previous canonical bytes (when the target exists);
///  2. write the serialized body to a temporary sibling file;
///  3. flush and re-read the temp file;
///  4. re-validate the re-read body against the schema;
///  5. atomically replace the target (move-with-backup);
///  6. on any failure, restore the previous canonical bytes.
///
/// On any failure path the previous artifact is preserved
/// byte-identically.
let writeAtomic (path: string) (e: CanonicalEvidence) : Result<WriteOutcome, WriteFailure> =
    let dir = Path.GetDirectoryName path
    match tryCreateDirectory dir with
    | Error e -> Result.Error e
    | Result.Ok () ->
        let previousBytes = snapshotFileBytes path
        let bodyResult =
            try Ok (renderWireJson e)
            with ex ->
                Error(SerializationFailed(sprintf "%s: %s" (ex.GetType().Name) ex.Message))
        match bodyResult with
        | Error err -> Result.Error err
        | Result.Ok body ->
            let tmp =
                let guid = Guid.NewGuid().ToString("n")
                Path.Combine(dir, (Path.GetFileName path) + ".tmp." + guid)
            let bytes = System.Text.Encoding.UTF8.GetBytes(body + "\n")
            let tempWriteResult =
                try
                    File.WriteAllBytes(tmp, bytes)
                    Ok ()
                with ex ->
                    safeDelete tmp
                    Error(TempWriteFailed(sprintf "%s: %s" (ex.GetType().Name) ex.Message))
            match tempWriteResult with
            | Error err -> Result.Error err
            | Result.Ok () ->
                let readResult =
                    try
                        Ok (File.ReadAllBytes tmp)
                    with ex ->
                        safeDelete tmp
                        Error(ReReadFailed(sprintf "%s: %s" (ex.GetType().Name) ex.Message))
                match readResult with
                | Error err -> Result.Error err
                | Result.Ok writtenBytes ->
                    let writtenText : string = System.Text.Encoding.UTF8.GetString writtenBytes
                    let rawKeys = collectRawJsonKeys writtenText
                    match parseWireJson writtenText with
                    | Result.Error err ->
                        safeDelete tmp
                        Result.Error(SerializationFailed(sprintf "post-write parse: %s" err))
                    | Result.Ok parsed ->
                        let vr = validate rawKeys parsed
                        if not (isValid vr) then
                            safeDelete tmp
                            Result.Error(PostReadValidationFailed(vr.Issues |> List.map issueToString))
                        else
                            // Atomic replacement. Try to move tmp to target.
                            let replacementResult =
                                try
                                    if File.Exists path then
                                        let backup = path + ".bak"
                                        if File.Exists backup then File.Delete backup
                                        File.Move(path, backup)
                                        try
                                            File.Move(tmp, path)
                                            File.Delete backup
                                            Ok ()
                                        with ex ->
                                            if File.Exists backup then
                                                if File.Exists path then File.Delete path
                                                File.Move(backup, path)
                                            Error(ReplacementFailed(sprintf "%s: %s" (ex.GetType().Name) ex.Message))
                                    else
                                        File.Move(tmp, path)
                                        Ok ()
                                with ex ->
                                    safeDelete tmp
                                    Error(ReplacementFailed(sprintf "%s: %s" (ex.GetType().Name) ex.Message))
                            match replacementResult with
                            | Error err -> Result.Error err
                            | Ok () ->
                                let currentBytes = File.ReadAllBytes path
                                let outcome = {
                                    Success = true
                                    Path = path
                                    CanonicalSha256 = sha256Hex currentBytes
                                    PreviousBytes = previousBytes
                                    CurrentBytes = currentBytes
                                    Failure = None
                                    CanonicalByteIdenticalAfterFailure = true
                                }
                                Result.Ok outcome

/// Try to write atomically; on failure, snapshot the previous canonical
/// bytes and report it. The previous artifact is preserved by
/// construction (the write either succeeds or the target is never
/// touched).
let tryWriteAtomic (path: string) (e: CanonicalEvidence) : WriteOutcome =
    match writeAtomic path e with
    | Result.Ok outcome -> outcome
    | Result.Error failure ->
        let previousBytes = snapshotFileBytes path
        let currentBytes = previousBytes |> Option.defaultValue [||]
        let preserve =
            // The previous artifact is preserved if it matches the
            // bytes we snapshotted before the attempt.
            byteIdentical previousBytes (if File.Exists path then Some (File.ReadAllBytes path) else None)
        {
            Success = false
            Path = path
            CanonicalSha256 = sha256Hex currentBytes
            PreviousBytes = previousBytes
            CurrentBytes = currentBytes
            Failure = Some failure
            CanonicalByteIdenticalAfterFailure = preserve
        }

// -----------------------------------------------------------------------------
// Verification
// -----------------------------------------------------------------------------

type VerifyFailure =
    | ReadFailed of message: string
    | ParseFailed of message: string
    | ValidationFailed of issues: string list
    | IdentityMismatch of field: string * expected: string * actual: string
    | IdentityUnresolved of detail: string

let verifyFailureToString (f: VerifyFailure) : string =
    match f with
    | ReadFailed msg -> sprintf "read failed: %s" msg
    | ParseFailed msg -> sprintf "parse failed: %s" msg
    | ValidationFailed issues -> sprintf "validation failed: %s" (String.concat "; " issues)
    | IdentityMismatch (field, expected, actual) -> sprintf "identity mismatch for %s: expected=%s actual=%s" field expected actual
    | IdentityUnresolved detail -> sprintf "identity unresolved: %s" detail

type VerifyOutcome = {
    Path: string
    Evidence: CanonicalEvidence option
    Validation: ValidationResult option
    Failure: VerifyFailure option
    BindingMatch: bool option
}

let verify (path: string) (repoRoot: string) : VerifyOutcome =
    if not (File.Exists path) then
        {
            Path = path
            Evidence = None
            Validation = None
            Failure = Some(ReadFailed(sprintf "file not found: %s" path))
            BindingMatch = None
        }
    else
        let readResult =
            try
                Ok (File.ReadAllText path)
            with ex ->
                Error(sprintf "%s: %s" (ex.GetType().Name) ex.Message)
        match readResult with
        | Error msg ->
            { Path = path; Evidence = None; Validation = None; Failure = Some(ReadFailed msg); BindingMatch = None }
        | Ok rawText when String.IsNullOrEmpty rawText ->
            { Path = path; Evidence = None; Validation = None; Failure = Some(ReadFailed "empty file"); BindingMatch = None }
        | Ok rawText ->
            let rawKeys = collectRawJsonKeys rawText
            match parseWireJson rawText with
            | Result.Error err ->
                { Path = path; Evidence = None; Validation = None; Failure = Some(ParseFailed err); BindingMatch = None }
            | Result.Ok evidence ->
                let vr = validate rawKeys evidence
                if not (isValid vr) then
                    {
                        Path = path
                        Evidence = Some evidence
                        Validation = Some vr
                        Failure = Some(ValidationFailed(vr.Issues |> List.map issueToString))
                        BindingMatch = None
                    }
                else
                    // Cross-check identities against the current
                    // repository state. This proves the artifact
                    // is not stale.
                    let bindingResult =
                        match resolveIdentity repoRoot with
                        | Result.Error idErr ->
                            Some(IdentityUnresolved(identityFailureToString idErr))
                        | Result.Ok current ->
                            if current.CommitOid <> evidence.TestedCommitOid then
                                Some(IdentityMismatch("tested_commit_oid", current.CommitOid, evidence.TestedCommitOid))
                            elif current.TreeOid <> evidence.TestedTreeOid then
                                Some(IdentityMismatch("tested_tree_oid", current.TreeOid, evidence.TestedTreeOid))
                            else
                                None
                    match bindingResult with
                    | Some failure ->
                        {
                            Path = path
                            Evidence = Some evidence
                            Validation = Some vr
                            Failure = Some failure
                            BindingMatch = Some false
                        }
                    | None ->
                        {
                            Path = path
                            Evidence = Some evidence
                            Validation = Some vr
                            Failure = None
                            BindingMatch = Some true
                        }
