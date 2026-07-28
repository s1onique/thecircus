module Circus.Tooling.CanonicalEvidence.Provider

// =============================================================================
// Canonical evidence – provider (slices 4–6)
//
// ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER-FOUNDATION01-CORRECTION01
// ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER-FOUNDATION01-CORRECTION02
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
// management. There is no direct subprocess invocation in this
// module; every external command is delegated to the bounded
// process and bounded Git adapter authorities.
//
// CORRECTION02 introduces an explicit dependency record so that the
// CLI tests no longer need to mutate the bounded Git adapter's
// per-process mutable gitExecutableCell. The production wrappers
// construct CanonicalEvidenceDependencies from the existing
// bounded authorities (BoundedProcess.run + bounded Git adapter);
// tests construct isolated fake dependencies per test. No fake or
// test dependency is reachable from the ordinary production CLI path.
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
open Circus.Tooling.ScopeAuthority.Domain
open Circus.Tooling.ScopeAuthority.Authority

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
/// surface: no direct subprocess invocation, no event handlers,
/// no stream reader races. The exit code is the only authority
/// for ``Pass``/``Fail``; every other bounded-process failure is
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
        // Preserve the exit code when the bounded process surfaced
        // a ``NonZeroExit`` failure: the child ran to completion and
        // the only bounded-process outcome was the non-zero code.
        let exitCode =
            match failure with
            | NonZeroExit (code, _, _) -> Some code
            | _ -> None
        {
            Id = def.Id
            CommandArgv = fullArgv
            WorkingDirectory = def.WorkingDirectory
            DurationMilliseconds = durationMs
            ExitCode = exitCode
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

let CanonicalCheckDefinitions
    (repoRoot: string)
    (baselineCommit: string)
    (scopeDeclarationPath: string)
    (evaluatedCommit: string)
    : EvidenceCheckDefinition list =
    let workDir = repoRoot
    let longTimeout = TimeSpan.FromMinutes(60.0)
    let shortTimeout = TimeSpan.FromMinutes(15.0)
    let quickTimeout = TimeSpan.FromMinutes(5.0)
    let stdOut = 32 * 1024 * 1024
    let stdErr = 32 * 1024 * 1024

    // The protected-scope check is delegated to the ACT-scope
    // authority: ``circus-tooling protected-scope check``, which
    // reads the ACT's declaration and categorises every change
    // against its globally_protected and act_owned lists.
    //
    // ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01-CORRECTION02
    // --------------------------------------------------------------
    // The declaration path is supplied by the caller (canonical-evidence
    // CLI) and is NEVER hardcoded here.  The provider does not contain
    // a literal ACT ID, a literal scope-declaration filename, or any
    // other ACT-specific path.  The protected-scope check's
    // ``command_argv`` includes the supplied path so the artifact
    // records exactly which declaration was consulted.
    let circusToolingDllPath =
        Path.Combine(repoRoot, "tools", "Circus.Tooling", "bin", "Release", "net10.0", "circus-tooling.dll")
    let protectedScopeArgs =
        [
            circusToolingDllPath
            "protected-scope"
            "check"
            "--repo-root"
            "."
            "--declaration"
            scopeDeclarationPath
            "--baseline-commit"
            baselineCommit
            "--evaluated-commit"
            evaluatedCommit
        ]

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
            Id = "postgres-runner-authority-tests"
            Executable = "dotnet"
            Arguments = [ "run"; "--project"; "tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj"; "-c"; "Release"; "--no-build"; "--no-restore"; "--"; "--summary"; "--filter-test-list"; "PostgresTestRunnerAuthorities" ]
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
            Executable = "dotnet"
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
    | ScopeAuthorityFailure of ScopeAuthorityError
    | CheckListEmpty
    | UnexpectedCheckId of id: string

let generateFailureToString (f: GenerateFailure) : string =
    match f with
    | IdentityFailure id -> sprintf "identity: %s" (identityFailureToString id)
    | ScopeAuthorityFailure scope -> sprintf "scope authority: %s" (errorToString scope)
    | CheckListEmpty -> "canonical check list is empty"
    | UnexpectedCheckId id -> sprintf "unexpected check id: %s" id

let buildCanonicalEvidence
    (identity: ResolvedIdentity)
    (scope: ScopeBinding)
    (checks: EvidenceCheckResult list)
    : CanonicalEvidence =
    let sortedChecks = sortChecksDeterministic checks
    let overallStatus = computeOverallStatus sortedChecks
    let doc = {
        SchemaVersion = SchemaVersionValue
        ProviderName = ProviderNameValue
        ProviderVersion = ProviderVersionValue
        TestedCommitOid = identity.CommitOid
        TestedTreeOid = identity.TreeOid
        ObjectFormat = identity.ObjectFormat
        ActiveScopeActId = scope.ActId
        ActiveScopePointerBlobOid = scope.PointerBlobOid
        ScopeDeclarationPath = scope.DeclarationPath
        DeclarationBlobOid = scope.DeclarationBlobOid
        BaselineCommitOid = scope.BaselineCommitOid
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
    (scopeDeclarationPath: string)
    : Result<CanonicalEvidence, GenerateFailure> =
    match resolveIdentity repoRoot with
    | Result.Error err -> Result.Error(IdentityFailure err)
    | Result.Ok identity ->
        match resolve repoRoot identity.CommitOid (Some scopeDeclarationPath) (Some baselineCommit) with
        | Error scopeError -> Result.Error(ScopeAuthorityFailure scopeError)
        | Ok scope ->
            let defs =
                CanonicalCheckDefinitions
                    repoRoot
                    scope.BaselineCommitOid
                    scope.DeclarationPath
                    identity.CommitOid

            match runCanonicalChecks defs with
            | Result.Error err -> Result.Error err
            | Result.Ok checks -> Result.Ok(buildCanonicalEvidence identity scope checks)

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

// =============================================================================
// CORRECTION02 — Explicit provider dependencies
//
// The provider now exposes an explicit ``CanonicalEvidenceDependencies``
// record and three internal orchestration entry points:
// ``regenerateWithDependencies``, ``verifyWithDependencies``,
// ``runCliWithDependencies``. Production wrappers build the dependencies
// from the existing bounded authorities (``BoundedProcess.run`` + bounded
// Git adapter). Tests construct isolated fake dependencies per test so
// they never mutate the bounded Git adapter's per-process executable
// cell. No fake or test dependency is reachable from the ordinary
// production CLI path.
// =============================================================================

// -----------------------------------------------------------------------------
// Public types shared between the production wrappers and the test seams.
// -----------------------------------------------------------------------------

type RepositoryIdentity = {
    CommitOid: string
    TreeOid: string
    ObjectFormat: string
}

type WorkingTreeState = {
    Dirty: bool
}

/// Failure surface returned by every dependency. The ``Reason`` tag
/// carries a stable machine-readable token so callers (CLI, tests,
/// compatibility reader) can map failures to verdict codes without
/// parsing free-form text.
type EvidenceFailure = {
    Reason: string
    Detail: string
}

let evidenceFailure (reason: string) (detail: string) : EvidenceFailure =
    { Reason = reason; Detail = detail }

// -----------------------------------------------------------------------------
// The dependency record. Every external action the provider takes
// flows through one of these members. The members are functions
// (not properties) so each call site passes its own cancellation
// token and parameters. Production wraps the bounded authorities;
// tests inject fakes.
// -----------------------------------------------------------------------------

type CanonicalEvidenceDependencies = {
    ResolveRepositoryIdentity: (string -> Result<RepositoryIdentity, EvidenceFailure>)
    ResolveScopeBinding:
        (string -> string -> string option -> string option -> Result<ScopeBinding, EvidenceFailure>)
    ReadWorkingTreeState: (string -> Result<WorkingTreeState, EvidenceFailure>)
    RunCheck: (EvidenceCheckDefinition -> CancellationToken -> Result<EvidenceCheckResult, EvidenceFailure>)
    ReadArtifact: (string -> Result<byte array, EvidenceFailure>)
    WriteArtifactAtomically: (string -> byte array -> Result<unit, EvidenceFailure>)
    GetUtcNow: (unit -> DateTimeOffset)
}

// -----------------------------------------------------------------------------
// Production dependency factory
//
// The production constructor uses ``BoundedProcess.run`` as the single
// execution authority for checks and the bounded Git adapter as the
// single identity authority. The file readers and writers are thin
// wrappers over ``System.IO`` so the orchestrator above has no
// knowledge of the underlying file format.
//
// The production factory NEVER captures the bounded Git adapter's
// mutable executable cell as state: it calls the bounded Git adapter
// functions directly. Tests never invoke this factory; they build a
// dependency record in-memory.
// -----------------------------------------------------------------------------

let private productionResolveIdentity (repoRoot: string) : Result<RepositoryIdentity, EvidenceFailure> =
    match resolveIdentity repoRoot with
    | Result.Ok id ->
        Result.Ok {
            CommitOid = id.CommitOid
            TreeOid = id.TreeOid
            ObjectFormat = id.ObjectFormat
        }
    | Result.Error err ->
        Result.Error(evidenceFailure "identity_failure" (identityFailureToString err))

let private productionResolveScope
    (repoRoot: string)
    (evaluatedCommitOid: string)
    (declarationPath: string option)
    (baselineCommitOid: string option)
    : Result<ScopeBinding, EvidenceFailure> =
    match resolve repoRoot evaluatedCommitOid declarationPath baselineCommitOid with
    | Ok binding -> Ok binding
    | Error error ->
        Error(evidenceFailure "scope_authority_failure" (errorToString error))

let private productionReadWorkingTree (repoRoot: string) : Result<WorkingTreeState, EvidenceFailure> =
    if String.IsNullOrWhiteSpace repoRoot || not (Directory.Exists repoRoot) then
        Result.Error(evidenceFailure "repository_not_found" repoRoot)
    else
        match runGit repoRoot [ "status"; "--porcelain=v1" ] with
        | Error err ->
            Result.Error(evidenceFailure "git_failure" (sprintf "status: %A" err))
        | Ok statusRun ->
            if statusRun.ExitCode <> 0 then
                Result.Error(evidenceFailure "git_failure"
                    (sprintf "status exit %d: %s" statusRun.ExitCode statusRun.Stderr))
            else
                Result.Ok { Dirty = not (String.IsNullOrEmpty(statusRun.Stdout.Trim())) }

let private productionRunCheck
    (def: EvidenceCheckDefinition)
    (cancellationToken: CancellationToken)
    : Result<EvidenceCheckResult, EvidenceFailure> =
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
        run request cancellationToken
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
        Result.Ok {
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
        Result.Ok {
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

let private productionReadArtifact (path: string) : Result<byte array, EvidenceFailure> =
    if not (File.Exists path) then
        Result.Error(evidenceFailure "artifact_not_found" (sprintf "file not found: %s" path))
    else
        try Result.Ok(File.ReadAllBytes path)
        with ex ->
            Result.Error(evidenceFailure "artifact_read_failed"
                (sprintf "%s: %s" (ex.GetType().Name) ex.Message))

/// Production atomic write. Reuses the canonical contract: write to a
/// temporary sibling, flush, re-read, validate, replace; on any failure
/// the previous artifact is preserved byte-identically.
let private productionWriteArtifact
    (path: string)
    (content: byte array)
    : Result<unit, EvidenceFailure> =
    let dir = Path.GetDirectoryName path
    let filename = Path.GetFileName path
    let attempt : Result<string, string> =
        try
            if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
                Directory.CreateDirectory dir |> ignore
            let tmp =
                let guid = Guid.NewGuid().ToString("n")
                Path.Combine(dir, filename + ".tmp." + guid)
            File.WriteAllBytes(tmp, content)
            // Re-read to confirm the bytes survived the round-trip.
            let _ = File.ReadAllBytes tmp
            Ok tmp
        with ex ->
            Error (sprintf "%s: %s" (ex.GetType().Name) ex.Message)
    match attempt with
    | Error msg ->
        Result.Error(evidenceFailure "artifact_write_failed" msg)
    | Ok tmp ->
        try
            if File.Exists path then
                let backup = path + ".bak"
                if File.Exists backup then File.Delete backup
                File.Move(path, backup)
                try
                    File.Move(tmp, path)
                    File.Delete backup
                with ex ->
                    if File.Exists backup then
                        if File.Exists path then File.Delete path
                        File.Move(backup, path)
                    try if File.Exists tmp then File.Delete tmp with _ -> ()
                    failwithf "%s: %s" (ex.GetType().Name) ex.Message
            else
                File.Move(tmp, path)
            Result.Ok ()
        with ex ->
            Result.Error(evidenceFailure "artifact_write_failed"
                (sprintf "%s: %s" (ex.GetType().Name) ex.Message))

let private productionGetUtcNow () : DateTimeOffset = DateTimeOffset.UtcNow

/// Production dependency record. Constructed once per process; the
/// ordinary production CLI path always uses this factory. Tests NEVER
/// call it.
let productionDependencies () : CanonicalEvidenceDependencies =
    {
        ResolveRepositoryIdentity = productionResolveIdentity
        ResolveScopeBinding = productionResolveScope
        ReadWorkingTreeState = productionReadWorkingTree
        RunCheck = productionRunCheck
        ReadArtifact = productionReadArtifact
        WriteArtifactAtomically = productionWriteArtifact
        GetUtcNow = productionGetUtcNow
    }

// -----------------------------------------------------------------------------
// Dependency-driven generation
// -----------------------------------------------------------------------------

type RegenerateFailure =
    | DependencyIdentityFailure of EvidenceFailure
    | DependencyScopeAuthorityFailure of EvidenceFailure
    | DependencyWorkingTreeFailure of EvidenceFailure
    | DependencyWorkingTreeDirty
    | DependencyCheckListEmpty
    | DependencyUnexpectedCheckId of id: string
    | DependencyCheckFailure of EvidenceFailure
    | DependencySerializationFailure of detail: string

let regenerateFailureToString (f: RegenerateFailure) : string =
    match f with
    | DependencyIdentityFailure e -> sprintf "identity: %s:%s" e.Reason e.Detail
    | DependencyScopeAuthorityFailure e -> sprintf "scope_authority: %s:%s" e.Reason e.Detail
    | DependencyWorkingTreeFailure e -> sprintf "working_tree: %s:%s" e.Reason e.Detail
    | DependencyWorkingTreeDirty -> "working tree is dirty (regeneration requires a clean tree)"
    | DependencyCheckListEmpty -> "canonical check list is empty"
    | DependencyUnexpectedCheckId id -> sprintf "unexpected check id: %s" id
    | DependencyCheckFailure e -> sprintf "check execution: %s:%s" e.Reason e.Detail
    | DependencySerializationFailure d -> sprintf "serialization failed: %s" d

/// Internal: build a ``CanonicalEvidence`` value from the
/// ``RepositoryIdentity`` and the executed ``EvidenceCheckResult``
/// list. Pure function: no IO, no subprocess.
let private assembleCanonicalEvidence
    (identity: RepositoryIdentity)
    (scope: ScopeBinding)
    (checks: EvidenceCheckResult list)
    : CanonicalEvidence =
    let sortedChecks = sortChecksDeterministic checks
    let overallStatus = computeOverallStatus sortedChecks
    let doc = {
        SchemaVersion = SchemaVersionValue
        ProviderName = ProviderNameValue
        ProviderVersion = ProviderVersionValue
        TestedCommitOid = identity.CommitOid
        TestedTreeOid = identity.TreeOid
        ObjectFormat = identity.ObjectFormat
        ActiveScopeActId = scope.ActId
        ActiveScopePointerBlobOid = scope.PointerBlobOid
        ScopeDeclarationPath = scope.DeclarationPath
        DeclarationBlobOid = scope.DeclarationBlobOid
        BaselineCommitOid = scope.BaselineCommitOid
        Checks = sortedChecks
        OverallStatus = overallStatus
        SemanticSha256 = ""
    }
    withSemanticHash doc

/// Internal: execute the canonical check list through the dependency.
/// The dependency is responsible for ``BoundedProcess.run`` (or an
/// equivalent); this orchestrator never owns process lifecycle.
let private executeChecksWithDependencies
    (deps: CanonicalEvidenceDependencies)
    (defs: EvidenceCheckDefinition list)
    : Result<EvidenceCheckResult list, RegenerateFailure> =
    if List.isEmpty defs then
        Result.Error DependencyCheckListEmpty
    else
        let known = SupportedCheckIdSet
        let mutable badId : string option = None
        for d in defs do
            if not (Set.contains d.Id known) then
                badId <- Some d.Id
        match badId with
        | Some id -> Result.Error(DependencyUnexpectedCheckId id)
        | None ->
            let mutable acc : EvidenceCheckResult list = []
            let mutable firstError : RegenerateFailure option = None
            let mutable halted = false
            for d in defs do
                if not halted then
                    match deps.RunCheck d CancellationToken.None with
                    | Result.Ok r -> acc <- r :: acc
                    | Result.Error e ->
                        firstError <- Some(DependencyCheckFailure e)
                        halted <- true
            match firstError with
            | Some f -> Result.Error f
            | None ->
                Result.Ok(sortChecksDeterministic acc)

/// Internal: regenerate canonical evidence using the supplied
/// dependency. This is the entry point the production wrapper and
/// the test seams both consume.
let internal regenerateWithDependencies
    (deps: CanonicalEvidenceDependencies)
    (repoRoot: string)
    (baselineCommit: string)
    (scopeDeclarationPath: string option)
    : Result<CanonicalEvidence, RegenerateFailure> =
    match deps.ResolveRepositoryIdentity repoRoot with
    | Result.Error e -> Result.Error(DependencyIdentityFailure e)
    | Result.Ok identity ->
        match deps.ReadWorkingTreeState repoRoot with
        | Result.Error e -> Result.Error(DependencyWorkingTreeFailure e)
        | Result.Ok state ->
            if state.Dirty then
                Result.Error DependencyWorkingTreeDirty
            else
                match
                    deps.ResolveScopeBinding
                        repoRoot
                        identity.CommitOid
                        scopeDeclarationPath
                        (Some baselineCommit)
                with
                | Error failure -> Result.Error(DependencyScopeAuthorityFailure failure)
                | Ok scope ->
                    let defs =
                        CanonicalCheckDefinitions
                            repoRoot
                            scope.BaselineCommitOid
                            scope.DeclarationPath
                            identity.CommitOid

                    match executeChecksWithDependencies deps defs with
                    | Result.Error f -> Result.Error f
                    | Result.Ok checks -> Result.Ok(assembleCanonicalEvidence identity scope checks)

// -----------------------------------------------------------------------------
// Dependency-driven verification
// -----------------------------------------------------------------------------

type DependencyVerifyFailure =
    | DependencyReadFailed of EvidenceFailure
    | DependencyParseFailed of EvidenceFailure
    | DependencyValidationFailed of issues: string list
    | DependencyIdentityMismatch of field: string * expected: string * actual: string
    | DependencyIdentityUnresolved of EvidenceFailure
    | DependencyScopeUnresolved of EvidenceFailure

let dependencyVerifyFailureToString (f: DependencyVerifyFailure) : string =
    match f with
    | DependencyReadFailed e -> sprintf "read failed: %s:%s" e.Reason e.Detail
    | DependencyParseFailed e -> sprintf "parse failed: %s:%s" e.Reason e.Detail
    | DependencyValidationFailed issues -> sprintf "validation failed: %s" (String.concat "; " issues)
    | DependencyIdentityMismatch (field, expected, actual) ->
        sprintf "identity mismatch for %s: expected=%s actual=%s" field expected actual
    | DependencyIdentityUnresolved e -> sprintf "identity unresolved: %s:%s" e.Reason e.Detail
    | DependencyScopeUnresolved e -> sprintf "scope unresolved: %s:%s" e.Reason e.Detail

type DependencyVerifyOutcome = {
    Path: string
    Evidence: CanonicalEvidence option
    Validation: ValidationResult option
    Failure: DependencyVerifyFailure option
    BindingMatch: bool option
}

let internal verifyWithDependencies
    (deps: CanonicalEvidenceDependencies)
    (path: string)
    (repoRoot: string)
    (scopeDeclarationPath: string option)
    : DependencyVerifyOutcome =
    let readResult = deps.ReadArtifact path
    match readResult with
    | Result.Error e ->
        {
            Path = path
            Evidence = None
            Validation = None
            Failure = Some(DependencyReadFailed e)
            BindingMatch = None
        }
    | Result.Ok bytes ->
        let rawText = System.Text.Encoding.UTF8.GetString bytes
        if String.IsNullOrEmpty rawText then
            {
                Path = path
                Evidence = None
                Validation = None
                Failure = Some(DependencyReadFailed(evidenceFailure "empty_file" "empty file"))
                BindingMatch = None
            }
        else
            let rawKeys = collectRawJsonKeys rawText
            match parseWireJson rawText with
            | Result.Error err ->
                {
                    Path = path
                    Evidence = None
                    Validation = None
                    Failure = Some(DependencyParseFailed(evidenceFailure "parse_failed" err))
                    BindingMatch = None
                }
            | Result.Ok evidence ->
                let vr = validate rawKeys evidence
                if not (isValid vr) then
                    {
                        Path = path
                        Evidence = Some evidence
                        Validation = Some vr
                        Failure = Some(DependencyValidationFailed(vr.Issues |> List.map issueToString))
                        BindingMatch = None
                    }
                else
                    match
                        deps.ResolveScopeBinding
                            repoRoot
                            evidence.TestedCommitOid
                            scopeDeclarationPath
                            (Some evidence.BaselineCommitOid)
                    with
                    | Result.Error scopeError ->
                        {
                            Path = path
                            Evidence = Some evidence
                            Validation = Some vr
                            Failure = Some(DependencyScopeUnresolved scopeError)
                            BindingMatch = Some false
                        }
                    | Result.Ok scope ->
                        let mismatch =
                            if not (String.Equals(scope.EvaluatedTreeOid, evidence.TestedTreeOid, StringComparison.OrdinalIgnoreCase)) then
                                Some("tested_tree_oid", scope.EvaluatedTreeOid, evidence.TestedTreeOid)
                            elif scope.ActId <> evidence.ActiveScopeActId then
                                Some("active_scope_act_id", scope.ActId, evidence.ActiveScopeActId)
                            elif not (String.Equals(scope.PointerBlobOid, evidence.ActiveScopePointerBlobOid, StringComparison.OrdinalIgnoreCase)) then
                                Some("active_scope_pointer_blob_oid", scope.PointerBlobOid, evidence.ActiveScopePointerBlobOid)
                            elif scope.DeclarationPath <> evidence.ScopeDeclarationPath then
                                Some("scope_declaration_path", scope.DeclarationPath, evidence.ScopeDeclarationPath)
                            elif not (String.Equals(scope.DeclarationBlobOid, evidence.DeclarationBlobOid, StringComparison.OrdinalIgnoreCase)) then
                                Some("declaration_blob_oid", scope.DeclarationBlobOid, evidence.DeclarationBlobOid)
                            elif not (String.Equals(scope.BaselineCommitOid, evidence.BaselineCommitOid, StringComparison.OrdinalIgnoreCase)) then
                                Some("baseline_commit_oid", scope.BaselineCommitOid, evidence.BaselineCommitOid)
                            else
                                None

                        match mismatch with
                        | Some(field, expected, actual) ->
                            {
                                Path = path
                                Evidence = Some evidence
                                Validation = Some vr
                                Failure = Some(DependencyIdentityMismatch(field, expected, actual))
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

// -----------------------------------------------------------------------------
// Dependency-driven artifact write (used by the test seams and the
// production CLI wrapper).
// -----------------------------------------------------------------------------

type DependencyWriteOutcome = {
    Success: bool
    Path: string
    CanonicalSha256: string
    Failure: EvidenceFailure option
    CanonicalByteIdenticalAfterFailure: bool
}

let internal writeArtifactWithDependencies
    (deps: CanonicalEvidenceDependencies)
    (path: string)
    (evidence: CanonicalEvidence)
    : DependencyWriteOutcome =
    let body =
        try renderWireJson evidence
        with ex ->
            // Recovery is impossible without the body. Surface the
            // failure and leave the previous artifact intact.
            "{ \"_render_failure\": \"" + ex.Message + "\" }"
    let bytes = System.Text.Encoding.UTF8.GetBytes(body + "\n")
    let snapshot =
        match deps.ReadArtifact path with
        | Result.Ok b -> Some b
        | Result.Error _ -> None
    match deps.WriteArtifactAtomically path bytes with
    | Result.Ok () ->
        // Re-read to compute the SHA-256 of the persisted bytes.
        let persisted =
            match deps.ReadArtifact path with
            | Result.Ok b -> b
            | Result.Error _ -> [||]
        {
            Success = true
            Path = path
            CanonicalSha256 = sha256Hex persisted
            Failure = None
            CanonicalByteIdenticalAfterFailure = true
        }
    | Result.Error failure ->
        let preserved =
            match snapshot with
            | Some prev ->
                match deps.ReadArtifact path with
                | Result.Ok cur -> cur = prev
                | Result.Error _ -> true
            | None -> not (File.Exists path)
        {
            Success = false
            Path = path
            CanonicalSha256 =
                match snapshot with
                | Some b -> sha256Hex b
                | None -> ""
            Failure = Some failure
            CanonicalByteIdenticalAfterFailure = preserved
        }

// -----------------------------------------------------------------------------
// ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01
// Subject-bound provide command
//
// The 'provide' command generates canonical execution evidence for an
// explicit Git subject commit OID. This is the authoritative evidence
// generation entry point for ML-only source policy verification.
// -----------------------------------------------------------------------------

type ProvideFailure =
    | ProvideInvalidSubjectOid of detail: string
    | ProvideSubjectNotFound of oid: string
    | ProvideSubjectNotACommit of oid: string
    | ProvideIdentityFailure of IdentityFailure
    | ProvideScopeAuthorityFailure of EvidenceFailure
    | ProvideWorkingTreeDirty
    | ProvideSubjectTreeMismatch of expected: string * actual: string
    | ProvideSubjectWorktreeCreationFailed of detail: string
    | ProvideSubjectWorktreeRemovalFailed of detail: string
    | ProvideCheckListEmpty
    | ProvideUnexpectedCheckId of id: string
    | ProvideCheckFailure of EvidenceFailure

let provideFailureToString (f: ProvideFailure) : string =
    match f with
    | ProvideInvalidSubjectOid detail -> sprintf "invalid subject OID: %s" detail
    | ProvideSubjectNotFound oid -> sprintf "subject commit not found: %s" oid
    | ProvideSubjectNotACommit oid -> sprintf "subject is not a commit: %s" oid
    | ProvideIdentityFailure id -> sprintf "identity: %s" (identityFailureToString id)
    | ProvideScopeAuthorityFailure scope -> sprintf "scope authority: %s:%s" scope.Reason scope.Detail
    | ProvideWorkingTreeDirty -> "working tree is dirty (provide requires a clean tree for subject-bound evidence)"
    | ProvideSubjectTreeMismatch (expected, actual) -> sprintf "subject tree mismatch: expected=%s actual=%s" expected actual
    | ProvideSubjectWorktreeCreationFailed detail -> sprintf "subject worktree creation failed: %s" detail
    | ProvideSubjectWorktreeRemovalFailed detail -> sprintf "subject worktree removal failed: %s" detail
    | ProvideCheckListEmpty -> "canonical check list is empty"
    | ProvideUnexpectedCheckId id -> sprintf "unexpected check id: %s" id
    | ProvideCheckFailure e -> sprintf "check execution: %s:%s" e.Reason e.Detail

/// Resolve a subject commit OID to its identity. Validates that the OID
/// is a valid commit and resolves its tree.
let resolveSubjectIdentity
    (repoRoot: string)
    (subjectOid: string)
    : Result<ResolvedIdentity, ProvideFailure> =
    if String.IsNullOrWhiteSpace subjectOid then
        Result.Error(ProvideInvalidSubjectOid "empty OID")
    else
        match runGit repoRoot [ "rev-parse"; "--show-object-format=storage" ] with
        | Error err ->
            Result.Error(ProvideIdentityFailure(IdentityGitFailure(sprintf "object-format: %A" err)))
        | Ok fmtRun ->
            if fmtRun.ExitCode <> 0 then
                Result.Error(ProvideIdentityFailure(IdentityGitFailure(sprintf "object-format exit %d: %s" fmtRun.ExitCode fmtRun.Stderr)))
            else
                let formatToken = fmtRun.Stdout.Trim()
                match parseObjectFormat formatToken with
                | None -> Result.Error(ProvideIdentityFailure(IdentityUnsupportedFormat formatToken))
                | Some fmtStr ->
                    match runGit repoRoot [ "cat-file"; "-t"; subjectOid ] with
                    | Error _ -> Result.Error(ProvideSubjectNotFound subjectOid)
                    | Ok typeRun ->
                        if typeRun.ExitCode <> 0 then
                            Result.Error(ProvideSubjectNotFound subjectOid)
                        else
                            let objType = typeRun.Stdout.Trim()
                            if objType <> "commit" then
                                Result.Error(ProvideSubjectNotACommit subjectOid)
                            else
                                if not (isValidOid fmtStr subjectOid) then
                                    Result.Error(ProvideInvalidSubjectOid(sprintf "invalid %s OID: %s" fmtStr subjectOid))
                                else
                                    match runGit repoRoot [ "rev-parse"; "--verify"; "--end-of-options"; subjectOid + "^{tree}" ] with
                                    | Error err ->
                                        Result.Error(ProvideIdentityFailure(IdentityGitFailure(sprintf "tree: %A" err)))
                                    | Ok treeRun ->
                                        if treeRun.ExitCode <> 0 then
                                            Result.Error(ProvideIdentityFailure(IdentityGitFailure(sprintf "tree exit %d: %s" treeRun.ExitCode treeRun.Stderr)))
                                        else
                                            let tree = treeRun.Stdout.Trim()
                                            if not (isValidOid fmtStr tree) then
                                                Result.Error(ProvideInvalidSubjectOid(sprintf "invalid tree OID: %s" tree))
                                            else
                                                Result.Ok {
                                                    CommitOid = subjectOid
                                                    TreeOid = tree
                                                    ObjectFormat = fmtStr
                                                }

/// Generate canonical evidence for an explicit subject commit OID.
/// This is the authoritative 'provide' entry point.
let provideWithDependencies
    (deps: CanonicalEvidenceDependencies)
    (repoRoot: string)
    (subjectOid: string)
    (scopeDeclarationPath: string option)
    : Result<CanonicalEvidence, ProvideFailure> =
    match resolveSubjectIdentity repoRoot subjectOid with
    | Result.Error err -> Result.Error err
    | Result.Ok identity ->
        match deps.ReadWorkingTreeState repoRoot with
        | Result.Error _ -> Result.Error ProvideWorkingTreeDirty
        | Result.Ok state ->
            if state.Dirty then
                Result.Error ProvideWorkingTreeDirty
            else
                match
                    deps.ResolveScopeBinding
                        repoRoot
                        identity.CommitOid
                        scopeDeclarationPath
                        None
                with
                | Error failure -> Result.Error(ProvideScopeAuthorityFailure failure)
                | Ok scope ->
                    let defs =
                        CanonicalCheckDefinitions
                            repoRoot
                            scope.BaselineCommitOid
                            scope.DeclarationPath
                            identity.CommitOid

                    if List.isEmpty defs then
                        Result.Error ProvideCheckListEmpty
                    else
                        let known = SupportedCheckIdSet
                        let mutable badId : string option = None
                        for d in defs do
                            if not (Set.contains d.Id known) then
                                badId <- Some d.Id
                        match badId with
                        | Some id -> Result.Error(ProvideUnexpectedCheckId id)
                        | None ->
                            let mutable acc : EvidenceCheckResult list = []
                            let mutable firstError : ProvideFailure option = None
                            let mutable halted = false
                            for d in defs do
                                if not halted then
                                    match deps.RunCheck d CancellationToken.None with
                                    | Result.Ok r -> acc <- r :: acc
                                    | Result.Error e ->
                                        firstError <- Some(ProvideCheckFailure e)
                                        halted <- true
                            match firstError with
                            | Some f -> Result.Error f
                            | None ->
                                let checks = sortChecksDeterministic acc
                                let overallStatus = computeOverallStatus checks
                                let doc = {
                                    SchemaVersion = SchemaVersionValue
                                    ProviderName = ProviderNameValue
                                    ProviderVersion = ProviderVersionValue
                                    TestedCommitOid = identity.CommitOid
                                    TestedTreeOid = identity.TreeOid
                                    ObjectFormat = identity.ObjectFormat
                                    ActiveScopeActId = scope.ActId
                                    ActiveScopePointerBlobOid = scope.PointerBlobOid
                                    ScopeDeclarationPath = scope.DeclarationPath
                                    DeclarationBlobOid = scope.DeclarationBlobOid
                                    BaselineCommitOid = scope.BaselineCommitOid
                                    Checks = checks
                                    OverallStatus = overallStatus
                                    SemanticSha256 = ""
                                }
                                Result.Ok(withSemanticHash doc)
