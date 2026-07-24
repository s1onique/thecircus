module Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Git

// =============================================================================
// Bounded Git adapter -- ACT-CIRCUS-FSHARP-DIAGNOSTIC-BOUNDED-GIT-ADAPTER01
// =============================================================================
//
// This module is the single, bounded, typed adapter for executing the Git
// binary from the RepairEpisodes subsystem. Every Git command flows
// through ``BoundedProcess.run``; no ``Process.Start`` call, no shell, no
// concatenated command line, and no repo-controlled data interpolation
// happens anywhere in the production path.
//
// Contract highlights:
//
//   * Single execution authority: ``BoundedProcess.run`` is the only path
//     that launches a process from this module.
//   * No shell: the Git executable is launched directly with an explicit
//     argument vector (the underlying ``ProcessStartInfo.ArgumentList``).
//   * Repository-directory authority: every command runs with
//     ``WorkingDirectory = repoPath``; the adapter never relies on the
//     process-wide current directory.
//   * Canonical execution profile: 60s timeout, 32 MiB stdout limit,
//     32 MiB stderr limit, kill-the-process-tree semantics inherited
//     from ``BoundedProcess``.
//   * Distinct failure taxonomy:
//       GitLaunchFailure
//       GitTimeoutFailure
//       GitCancellationFailure
//       GitStdoutOverflowFailure
//       GitStderrOverflowFailure
//       GitIoFailure
//       GitExitFailure (retains argv, exit_code, stdout, stderr)
//       GitProtocolFailure
//     plus a merge-parent ambiguity failure carrying candidate parents.
//   * Object-format authority: storage format is detected once per
//     ``repoPath`` via ``git rev-parse --show-object-format=storage`` and
//     cached. SHA-1 (40 hex chars) and SHA-256 (64 hex chars) are
//     accepted; anything else fails closed with ``GitObjectFormatFailure``.
//   * Non-abbreviated output: ``git diff-tree`` is invoked with
//     ``--no-abbrev --no-renames --raw -z``; ``git rev-parse`` uses
//     ``--verify --end-of-options`` so caller-supplied references
//     cannot be reinterpreted as flags.
//   * Merge parents are NEVER chosen implicitly. When the after-commit
//     has more than one parent, the adapter raises
//     ``GitMergeAmbiguityFailure`` listing the candidate parent OIDs.
//     Callers may pass an explicit parent via
//     ``resolveGitIdentityWithParent``.
//   * Deterministic decoding: only contractually irrelevant line
//     endings are normalised (\r\n / bare \r -> \n); UTF-8 path bytes
//     are preserved, malformed records are rejected, missing required
//     fields fail closed, extra ambiguous records fail closed.
//
// A completed child run is surfaced as ``Ok`` even when its exit code
// is non-zero: the exit code, stdout, and stderr are part of the
// success payload, and callers (for example ``verifyOne``) inspect
// ``ExitCode`` to decide whether to raise a domain exception such as
// ``GitIdentityFailure``. Only catastrophic bounded-process failures
// (launch, timeout, cancellation, output overflow, I/O, protocol)
// become ``Error``. This keeps the failure taxonomy distinct: timeout,
// cancellation, overflow, and launch failures are NEVER collapsed into
// a generic non-zero exit with empty streams.
//
// This module deliberately preserves the legacy ``GitRunOptions`` /
// ``GitRunResult`` / ``GitIdentityFailure`` / ``GitObjectFormatFailure`` /
// ``GitChangeParseFailure`` surface so that ``Engine.fs`` continues to
// work unchanged. The new typed ``GitRunError`` DU and the eight
// dedicated exceptions live alongside those legacy types; the legacy
// public ``runGit`` translates each typed error into the corresponding
// dedicated exception so callers can match on either surface.

open System
open System.Globalization
open System.IO
open System.Text
open System.Threading
open Circus.Tooling.FSharpDiagnostics.Hashing
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.BoundedProcess
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain

// -----------------------------------------------------------------------------
// Canonical execution profile
// -----------------------------------------------------------------------------

[<Literal>]
let CanonicalTimeoutMs : int = 60000

[<Literal>]
let CanonicalStdoutLimitBytes : int = 33554432  // 32 MiB

[<Literal>]
let CanonicalStderrLimitBytes : int = 33554432  // 32 MiB

/// Resource bounds for Git invocations. Public surface mirrors the
/// pre-existing ``GitRunOptions`` shape exactly so ``Engine.fs`` keeps
/// compiling and running unchanged.
type GitRunOptions = {
    Timeout: TimeSpan
    MaxStdoutBytes: int64
    MaxStderrBytes: int64
}

let defaultGitRunOptions : GitRunOptions =
    { Timeout = TimeSpan.FromMilliseconds (float CanonicalTimeoutMs)
      MaxStdoutBytes = int64 CanonicalStdoutLimitBytes
      MaxStderrBytes = int64 CanonicalStderrLimitBytes }

// -----------------------------------------------------------------------------
// Failure taxonomy
// -----------------------------------------------------------------------------

exception GitLaunchFailure of detail: string
exception GitTimeoutFailure of detail: string
exception GitCancellationFailure of detail: string
exception GitStdoutOverflowFailure of detail: string
exception GitStderrOverflowFailure of detail: string
exception GitIoFailure of detail: string
exception GitExitFailure of argv: string list * exitCode: int * stdout: string * stderr: string
exception GitProtocolFailure of detail: string
exception GitMergeAmbiguityFailure of parentCandidates: string list

// Backward-compatible exceptions consumed by Engine.fs.
exception GitObjectFormatFailure of detail: string
exception GitIdentityFailure of detail: string
exception GitChangeParseFailure of detail: string

/// Typed representation of the eight distinct adapter failure modes.
type GitRunError =
    | LaunchFailure of detail: string
    | TimeoutFailure of detail: string
    | CancellationFailure of detail: string
    | StdoutOverflowFailure of limitBytes: int * detail: string
    | StderrOverflowFailure of limitBytes: int * detail: string
    | IoFailure of detail: string
    | ExitFailure of argv: string list * exitCode: int * stdout: string * stderr: string
    | ProtocolFailure of detail: string

type GitRunSuccess = {
    ExitCode: int
    Stdout: string
    Stderr: string
    Argv: string list
}

/// Legacy result record used by ``runGit`` (the exception-raising
/// surface) and by ``Engine.fs``. ``Argv`` is retained so the bounded
/// adapter can hand the exact argument vector to ``GitExitFailure``
/// without consulting the caller.
type GitRunResult = {
    ExitCode: int
    Stdout: string
    Stderr: string
    Argv: string list
}

// -----------------------------------------------------------------------------
// Test seam: the git executable is mutable so the adapter can be
// driven against a missing executable, a long-sleeping fixture, or any
// other controlled binary without going through a real Git binary.
// Production code never touches this seam; it remains at its default
// value of "git".
// -----------------------------------------------------------------------------

type private GitExecutableCell =
    { mutable Value: string }

let private gitExecutableCell : GitExecutableCell =
    { Value = "git" }

let setGitExecutable (path: string) : unit =
    if isNull path then
        invalidArg "path" "git executable path must not be null"
    elif String.IsNullOrWhiteSpace path then
        invalidArg "path" "git executable path must not be whitespace"
    else
        gitExecutableCell.Value <- path

let resetGitExecutable () : unit =
    gitExecutableCell.Value <- "git"

let private currentGitExecutable () : string =
    gitExecutableCell.Value

// -----------------------------------------------------------------------------
// Helpers
// -----------------------------------------------------------------------------

let private safeInt (v: int64) : int =
    if v < 0L then 0
    elif v > int64 Int32.MaxValue then Int32.MaxValue
    else int v

/// Decode UTF-8 bytes into text while preserving all path bytes. We
/// normalise only contractually irrelevant line endings (\r\n and bare
/// \r -> \n) so platform-independent text comparisons remain stable
/// across hosts. Path bytes that are not valid UTF-8 are replaced by
/// the U+FFFD substitution character, matching ``Encoding.UTF8``'s
/// default decoder fallback behaviour.
let private decodeGitBytes (bytes: byte array) : string =
    let raw = Encoding.UTF8.GetString(bytes)
    raw.Replace("\r\n", "\n").Replace("\r", "\n")

let private toBoundedRequest
    (repoPath: string)
    (options: GitRunOptions)
    (args: string list)
    : BoundedProcessRequest =
    { Executable = currentGitExecutable ()
      WorkingDirectory = repoPath
      Arguments = args
      Environment = []
      Limits = {
        Timeout = options.Timeout
        StdoutLimitBytes = safeInt options.MaxStdoutBytes
        StderrLimitBytes = safeInt options.MaxStderrBytes
      } }

let private runBoundedSync
    (request: BoundedProcessRequest)
    : Result<BoundedProcessSuccess, BoundedProcessFailure> =
    run request CancellationToken.None
    |> Async.AwaitTask
    |> Async.RunSynchronously

let private translateBoundedError
    (failure: BoundedProcessFailure)
    (argv: string list)
    : GitRunError =
    // ``NonZeroExit`` is filtered out by ``runGitTyped`` before reaching
    // here, so the only branch that can match it would be unreachable
    // in practice. Keep the explicit case anyway so the compiler can
    // warn when ``BoundedProcessFailure`` adds a new failure kind.
    match failure with
    | NonZeroExit (exitCode, stdout, stderr) ->
        GitRunError.ExitFailure
            (argv, exitCode, decodeGitBytes stdout, decodeGitBytes stderr)
    | InvalidRequest detail ->
        GitRunError.LaunchFailure (sprintf "git: invalid request: %s" detail)
    | LaunchFailed (_, detail) ->
        GitRunError.LaunchFailure (sprintf "git: launch failed: %s" detail)
    | TimedOut timeout ->
        GitRunError.TimeoutFailure (sprintf "git: timed out after %O" timeout)
    | Cancelled ->
        GitRunError.CancellationFailure "git: caller cancellation observed"
    | StdoutLimitExceeded limit ->
        GitRunError.StdoutOverflowFailure
            (limit, sprintf "git: stdout exceeded %d bytes" limit)
    | StderrLimitExceeded limit ->
        GitRunError.StderrOverflowFailure
            (limit, sprintf "git: stderr exceeded %d bytes" limit)
    | StdoutReaderFailed detail ->
        GitRunError.IoFailure (sprintf "git: stdout reader failed: %s" detail)
    | StderrReaderFailed detail ->
        GitRunError.IoFailure (sprintf "git: stderr reader failed: %s" detail)
    | WaitFailed detail ->
        GitRunError.IoFailure (sprintf "git: process wait failed: %s" detail)
    | KillFailed detail ->
        GitRunError.IoFailure (sprintf "git: process kill failed: %s" detail)
    | IncompleteOutput (stdoutComplete, stderrComplete) ->
        GitRunError.IoFailure
            (sprintf
                "git: incomplete output (stdoutComplete=%b, stderrComplete=%b)"
                stdoutComplete stderrComplete)
    | TerminationCleanupFailed ctx ->
        match ctx.Cause with
        | TimeoutFire ->
            GitRunError.TimeoutFailure
                "git: timed out with incomplete termination cleanup"
        | CallerCancel ->
            GitRunError.CancellationFailure
                "git: caller cancellation with incomplete termination cleanup"
        | StdoutTerminal ->
            match ctx.TerminalFailure with
            | Some StdoutOverflow ->
                GitRunError.StdoutOverflowFailure
                    (CanonicalStdoutLimitBytes,
                     "git: stdout overflow with incomplete termination cleanup")
            | Some (StdoutReadFailure detail) ->
                GitRunError.IoFailure
                    (sprintf "git: stdout read failed: %s" detail)
            | _ ->
                GitRunError.IoFailure
                    "git: incomplete stdout termination"
        | StderrTerminal ->
            match ctx.TerminalFailure with
            | Some StderrOverflow ->
                GitRunError.StderrOverflowFailure
                    (CanonicalStderrLimitBytes,
                     "git: stderr overflow with incomplete termination cleanup")
            | Some (StderrReadFailure detail) ->
                GitRunError.IoFailure
                    (sprintf "git: stderr read failed: %s" detail)
            | _ ->
                GitRunError.IoFailure
                    "git: incomplete stderr termination"
        | ExitWaitFailed ->
            match ctx.WaitDetail with
            | Some d -> GitRunError.IoFailure (sprintf "git: exit wait failed: %s" d)
            | None -> GitRunError.IoFailure "git: exit wait failed"
        | _ ->
            GitRunError.IoFailure
                (sprintf "git: incomplete termination (cause=%A)" ctx.Cause)

let private translateTypedToException (error: GitRunError) : exn =
    match error with
    | GitRunError.LaunchFailure detail ->
        GitLaunchFailure detail
    | GitRunError.TimeoutFailure detail ->
        GitTimeoutFailure detail
    | GitRunError.CancellationFailure detail ->
        GitCancellationFailure detail
    | GitRunError.StdoutOverflowFailure (_, detail) ->
        GitStdoutOverflowFailure detail
    | GitRunError.StderrOverflowFailure (_, detail) ->
        GitStderrOverflowFailure detail
    | GitRunError.IoFailure detail ->
        GitIoFailure detail
    | GitRunError.ExitFailure (argv, exitCode, stdout, stderr) ->
        GitExitFailure(argv, exitCode, stdout, stderr)
    | GitRunError.ProtocolFailure detail ->
        GitProtocolFailure detail

// -----------------------------------------------------------------------------
// Single execution authority: every Git command flows through here.
// -----------------------------------------------------------------------------

/// Run Git with the canonical bounded profile and return a typed
/// result. This is the primary, exception-free adapter surface used
/// by the adapter tests and by any future caller that wants to handle
/// failures as values.
///
/// A completed child run is ALWAYS surfaced as ``Ok`` even when its
/// exit code is non-zero: the exit code, stdout, and stderr are part
/// of the success payload, and callers (for example ``verifyOne``)
/// inspect ``ExitCode`` to decide whether to raise a domain
/// exception such as ``GitIdentityFailure``. Only catastrophic
/// bounded-process failures (launch, timeout, cancellation, output
/// overflow, I/O, protocol) become ``Error``.
let runGitTyped
    (repoPath: string)
    (options: GitRunOptions)
    (args: string list)
    : Result<GitRunSuccess, GitRunError> =
    if String.IsNullOrWhiteSpace repoPath then
        Error (GitRunError.LaunchFailure "git: empty repository path")
    elif not (Directory.Exists repoPath) then
        Error
            (GitRunError.LaunchFailure
                (sprintf "git: repository path does not exist: %s" repoPath))
    else
        let request = toBoundedRequest repoPath options args
        match runBoundedSync request with
        | Ok success ->
            Ok {
                ExitCode = success.ExitCode
                Stdout = decodeGitBytes success.Stdout
                Stderr = decodeGitBytes success.Stderr
                Argv = args
            }
        | Error failure ->
            // ``NonZeroExit`` is data, not a bounded-process failure:
            // the child ran to completion and returned an exit code we
            // must inspect. Surface it as a typed success so callers
            // decide whether to raise. Every other failure is a true
            // bounded-process failure and is mapped to ``GitRunError``.
            match failure with
            | NonZeroExit (exitCode, stdout, stderr) ->
                Ok {
                    ExitCode = exitCode
                    Stdout = decodeGitBytes stdout
                    Stderr = decodeGitBytes stderr
                    Argv = args
                }
            | _ ->
                Error (translateBoundedError failure args)

/// Legacy exception-raising adapter. Kept for ``Engine.fs`` and the
/// existing ``GitIdentityTests``; internally it forwards to
/// ``runGitTyped`` and translates every typed error into the matching
/// dedicated exception.
let runGit
    (repoPath: string)
    (options: GitRunOptions)
    (args: string list)
    : GitRunResult =
    match runGitTyped repoPath options args with
    | Ok success ->
        {
            ExitCode = success.ExitCode
            Stdout = success.Stdout
            Stderr = success.Stderr
            Argv = success.Argv
        }
    | Error error -> raise (translateTypedToException error)

// -----------------------------------------------------------------------------
// Object format detection
// -----------------------------------------------------------------------------

let private objectFormatCache =
    System.Collections.Concurrent.ConcurrentDictionary<string, GitObjectFormat>()

let detectObjectFormat (repoPath: string) : GitObjectFormat =
    match objectFormatCache.TryGetValue repoPath with
    | true, fmt -> fmt
    | false, _ ->
        let result =
            runGit
                repoPath
                defaultGitRunOptions
                [ "rev-parse"; "--show-object-format=storage" ]
        if result.ExitCode <> 0 then
            raise
                (GitObjectFormatFailure
                    (sprintf
                        "git rev-parse --show-object-format failed: %s"
                        result.Stderr))
        let token = result.Stdout.Trim()
        match tryParseGitObjectFormat token with
        | Some fmt ->
            objectFormatCache.TryAdd(repoPath, fmt) |> ignore
            fmt
        | None ->
            raise
                (GitObjectFormatFailure
                    (sprintf "git: unrecognised object format token: %s" token))

let clearObjectFormatCache () : unit =
    objectFormatCache.Clear()

// -----------------------------------------------------------------------------
// OID validation
// -----------------------------------------------------------------------------

/// Validate a Git OID for the given object format. Returns true when
/// the OID is full-width hexadecimal of the correct width.
let isValidOid (fmt: GitObjectFormat) (oid: string) : bool =
    if isNull oid then false
    elif oid.Length <> gitObjectFormatWidth fmt then false
    else
        let mutable ok = true
        for c in oid do
            if not (Char.IsAsciiHexDigit c) then
                ok <- false
        ok

// -----------------------------------------------------------------------------
// Identity resolution
// -----------------------------------------------------------------------------

let private verifyOne
    (repoRoot: string)
    (options: GitRunOptions)
    (rev: string)
    (suffix: string)
    : string =
    let result =
        runGit
            repoRoot
            options
            [ "rev-parse"; "--verify"; "--end-of-options"; rev + suffix ]
    if result.ExitCode <> 0 then
        raise
            (GitIdentityFailure
                (sprintf
                    "git rev-parse %s%s failed: %s"
                    rev suffix result.Stderr))
    let trimmed = result.Stdout.Trim()
    if trimmed.Length = 0 then
        raise
            (GitIdentityFailure
                (sprintf
                    "git rev-parse %s%s returned empty output"
                    rev suffix))
    trimmed

let private resolveCommitAndTree
    (repoRoot: string)
    (options: GitRunOptions)
    (commitRef: string)
    : string * string =
    let commitOid = verifyOne repoRoot options commitRef "^{commit}"
    let treeOid = verifyOne repoRoot options commitRef "^{tree}"
    commitOid, treeOid

let private isAncestor
    (repoRoot: string)
    (options: GitRunOptions)
    (ancestor: string)
    (descendant: string)
    : bool =
    let result =
        runGit
            repoRoot
            options
            [ "merge-base"; "--is-ancestor"; ancestor; descendant ]
    if result.ExitCode = 0 then true
    elif result.ExitCode = 1 then false
    else
        raise
            (GitIdentityFailure
                (sprintf
                    "git merge-base --is-ancestor failed: %s"
                    result.Stderr))

/// Return the list of parent OIDs for ``commitRef`` (empty list if it
/// is a root commit). This is the basis for merge-parent ambiguity
/// detection; it never silently collapses to the first parent.
let private parentsOf
    (repoRoot: string)
    (options: GitRunOptions)
    (commitRef: string)
    : string list =
    let result =
        runGit
            repoRoot
            options
            [ "log"; "--pretty=%P"; "-n"; "1"; commitRef ]
    if result.ExitCode <> 0 then
        raise
            (GitIdentityFailure
                (sprintf
                    "git log --pretty=%%P %s failed: %s"
                    commitRef result.Stderr))
    let trimmed = result.Stdout.Trim()
    if trimmed.Length = 0 then []
    else
        trimmed.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries)
        |> Array.toList

let private revListAncestryPath
    (repoRoot: string)
    (options: GitRunOptions)
    (before: string)
    (after: string)
    : string list =
    let result =
        runGit
            repoRoot
            options
            [ "rev-list"; "--reverse"; "--ancestry-path"; before + ".." + after ]
    if result.ExitCode <> 0 then
        raise
            (GitIdentityFailure
                (sprintf "git rev-list failed: %s" result.Stderr))
    result.Stdout.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries)
    |> Array.toList
    |> List.map (fun s -> s.Trim())

let private ensureValidInputOid
    (fmt: GitObjectFormat)
    (label: string)
    (oid: string)
    : unit =
    if not (isValidOid fmt oid) then
        raise
            (GitIdentityFailure
                (sprintf
                    "git: invalid %s commit OID for %s format: %s"
                    label
                    (gitObjectFormatToken fmt)
                    oid))

/// Resolve a complete repair-episode Git identity. Validates OIDs
/// against the detected object format, refuses abbreviated or
/// wrong-width OIDs, refuses non-ancestor ranges, and refuses to
/// silently pick a parent when the after-commit has more than one
/// parent (raising ``GitMergeAmbiguityFailure`` instead).
let resolveGitIdentity
    (repoRoot: string)
    (options: GitRunOptions)
    (beforeCommitInput: string)
    (afterCommitInput: string)
    : GitIdentityResolution =
    let fmt = detectObjectFormat repoRoot
    ensureValidInputOid fmt "before" beforeCommitInput
    ensureValidInputOid fmt "after" afterCommitInput
    let beforeCommit, beforeTree =
        resolveCommitAndTree repoRoot options beforeCommitInput
    let afterCommit, afterTree =
        resolveCommitAndTree repoRoot options afterCommitInput
    if not (isAncestor repoRoot options beforeCommit afterCommit) then
        raise
            (GitIdentityFailure
                (sprintf
                    "git: %s is not an ancestor of %s"
                    beforeCommit afterCommit))
    let parents = parentsOf repoRoot options afterCommit
    if List.length parents > 1 then
        raise (GitMergeAmbiguityFailure parents)
    let commitRange =
        revListAncestryPath repoRoot options beforeCommit afterCommit
    {
        BeforeCommitOid = beforeCommit
        BeforeTreeOid = beforeTree
        AfterCommitOid = afterCommit
        AfterTreeOid = afterTree
        CommitRange = commitRange
        ObjectFormat = fmt
    }

/// Same contract as ``resolveGitIdentity`` but accepts an explicit
/// parent OID. The supplied parent must be one of ``after``'s parents;
/// the ancestry path is then computed from the explicit parent to
/// ``after`` so the caller selects the intended change set. The
/// change set itself (the tree-to-tree diff) is independent of the
/// parent selection because we compare ``beforeTree`` and ``afterTree``
/// directly.
let resolveGitIdentityWithParent
    (repoRoot: string)
    (options: GitRunOptions)
    (beforeCommitInput: string)
    (afterCommitInput: string)
    (explicitParent: string)
    : GitIdentityResolution =
    let fmt = detectObjectFormat repoRoot
    ensureValidInputOid fmt "before" beforeCommitInput
    ensureValidInputOid fmt "after" afterCommitInput
    ensureValidInputOid fmt "explicit parent" explicitParent
    let beforeCommit, beforeTree =
        resolveCommitAndTree repoRoot options beforeCommitInput
    let afterCommit, afterTree =
        resolveCommitAndTree repoRoot options afterCommitInput
    if not (isAncestor repoRoot options beforeCommit afterCommit) then
        raise
            (GitIdentityFailure
                (sprintf
                    "git: %s is not an ancestor of %s"
                    beforeCommit afterCommit))
    let parents = parentsOf repoRoot options afterCommit
    if not (List.contains explicitParent parents) then
        raise
            (GitIdentityFailure
                (sprintf
                    "git: explicit parent %s is not a parent of %s"
                    explicitParent afterCommit))
    let commitRange =
        revListAncestryPath repoRoot options explicitParent afterCommit
    {
        BeforeCommitOid = beforeCommit
        BeforeTreeOid = beforeTree
        AfterCommitOid = afterCommit
        AfterTreeOid = afterTree
        CommitRange = commitRange
        ObjectFormat = fmt
    }

// -----------------------------------------------------------------------------
// Change set extraction
//
// `git diff-tree` invocation is exactly the form mandated by ACT §7.
// `--no-abbrev` keeps every OID at the repository's storage width so
// downstream change-set identity hashing sees full-width blob OIDs.
// `--no-renames` keeps the rename detection surface inert; renames
// therefore surface as explicit delete + add pairs. `-z` makes the
// parser NUL-delimited and unambiguous.
// -----------------------------------------------------------------------------

let private zeroOid40 =
    "0000000000000000000000000000000000000000"

let private zeroOid64 =
    "0000000000000000000000000000000000000000000000000000000000000000"

let private isZeroOid (token: string) : bool =
    token = zeroOid40 || token = zeroOid64

/// Parse the NUL-delimited output of
/// ``git diff-tree --raw -z --no-renames --no-abbrev``.
///
/// Each record is six NUL-separated tokens:
///   ``:<before_mode> <after_mode> <before_blob> <after_blob> <status>\0<path>\0``
///
/// A "type change" (link/submodule) emits status 'T' which we surface
/// explicitly as ``TypeChanged``.
let private parseDiffTreeRaw
    (raw: string)
    (objectFormat: GitObjectFormat)
    : GitChangeEntry list =
    let parts =
        raw.Split([| '\u0000' |], StringSplitOptions.None)
        |> Array.toList
    let expectedWidth = gitObjectFormatWidth objectFormat
    let expectWidth (token: string) =
        if token.Length <> expectedWidth then
            raise
                (GitChangeParseFailure
                    (sprintf
                        "git diff-tree: OID %s has width %d, expected %d for %s"
                        token
                        token.Length
                        expectedWidth
                        (gitObjectFormatToken objectFormat)))
    let mutable entries : GitChangeEntry list = []
    let mutable i = 0
    while i < parts.Length do
        let header = parts.[i]
        if String.IsNullOrEmpty header then
            i <- i + 1
        else
            if i + 1 >= parts.Length then
                raise
                    (GitChangeParseFailure
                        "git diff-tree: unexpected end of input before path")
            let path = parts.[i + 1]
            if not (header.StartsWith ":") then
                raise
                    (GitChangeParseFailure
                        (sprintf
                            "git diff-tree: expected ':' header, got %s"
                            header))
            let body = header.Substring(1)
            let tokens =
                body.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries)
            if tokens.Length < 5 then
                raise
                    (GitChangeParseFailure
                        (sprintf
                            "git diff-tree: header has %d tokens, expected ≥ 5: %s"
                            tokens.Length
                            body))
            let beforeMode = tokens.[0]
            let afterMode = tokens.[1]
            let beforeBlob = tokens.[2]
            let afterBlob = tokens.[3]
            let status = tokens.[4]
            if not (isZeroOid beforeBlob) then
                expectWidth beforeBlob
            if not (isZeroOid afterBlob) then
                expectWidth afterBlob
            let beforeBlobOpt =
                if isZeroOid beforeBlob then None else Some beforeBlob
            let afterBlobOpt =
                if isZeroOid afterBlob then None else Some afterBlob
            let changeKind =
                match status with
                | "A" -> Added
                | "M" -> Modified
                | "D" -> Deleted
                | "T" -> TypeChanged
                | _ ->
                    raise
                        (GitChangeParseFailure
                            (sprintf
                                "git diff-tree: unrecognised status token %s for path %s"
                                status
                                path))
            let canonicalPath =
                if path.Contains ".." then
                    raise
                        (GitChangeParseFailure
                            (sprintf
                                "git diff-tree: path escapes repository: %s"
                                path))
                elif Path.IsPathRooted path then
                    raise
                        (GitChangeParseFailure
                            (sprintf
                                "git diff-tree: absolute path not allowed: %s"
                                path))
                else
                    path.Replace('\\', '/')
            let entry : GitChangeEntry =
                {
                    BeforeMode = beforeMode
                    AfterMode = afterMode
                    BeforeBlobOid = beforeBlobOpt
                    AfterBlobOid = afterBlobOpt
                    ChangeKind = changeKind
                    CanonicalPath = canonicalPath
                }
            entries <- entry :: entries
            i <- i + 2
    entries
    |> List.sortBy (fun e -> e.CanonicalPath)

let computeChangeSet
    (repoRoot: string)
    (options: GitRunOptions)
    (objectFormat: GitObjectFormat)
    (beforeTreeOid: string)
    (afterTreeOid: string)
    : GitChangeEntry list =
    let result =
        runGit
            repoRoot
            options
            [ "-c"; "core.quotepath=false"
              "diff-tree"
              "--no-commit-id"
              "-r"
              "--raw"
              "-z"
              "--no-renames"
              "--no-ext-diff"
              "--no-textconv"
              "--no-abbrev"
              beforeTreeOid
              afterTreeOid ]
    if result.ExitCode <> 0 then
        raise
            (GitChangeParseFailure
                (sprintf "git diff-tree failed: %s" result.Stderr))
    parseDiffTreeRaw result.Stdout objectFormat

// -----------------------------------------------------------------------------
// Change-set identity
// -----------------------------------------------------------------------------

let private lengthPrefixedString (sb: StringBuilder) (value: string) : unit =
    sb.Append(value.Length.ToString("x8", CultureInfo.InvariantCulture)) |> ignore
    sb.Append(':') |> ignore
    sb.Append(value) |> ignore

let private lengthPrefixedInt (sb: StringBuilder) (value: int) : unit =
    sb.Append(value.ToString("x8", CultureInfo.InvariantCulture)) |> ignore

let private lengthPrefixedOptBlob
    (sb: StringBuilder)
    (oid: string option)
    : unit =
    match oid with
    | None -> sb.Append("00:") |> ignore
    | Some o ->
        sb.Append(o.Length.ToString("x8", CultureInfo.InvariantCulture)) |> ignore
        sb.Append(':') |> ignore
        sb.Append(o) |> ignore

let computeChangeSetIdentity
    (beforeTreeOid: string)
    (afterTreeOid: string)
    (entries: GitChangeEntry list)
    : string =
    let sb = StringBuilder()
    lengthPrefixedString sb ChangeSetIdentityVersion
    lengthPrefixedString sb beforeTreeOid
    lengthPrefixedString sb afterTreeOid
    lengthPrefixedInt sb (List.length entries)
    for e in entries do
        lengthPrefixedString sb e.CanonicalPath
        lengthPrefixedString sb e.BeforeMode
        lengthPrefixedString sb e.AfterMode
        lengthPrefixedOptBlob sb e.BeforeBlobOid
        lengthPrefixedOptBlob sb e.AfterBlobOid
        lengthPrefixedString sb (gitChangeKindToken e.ChangeKind)
    sha256OfUtf8 (sb.ToString())

let buildChangeSet
    (repoRoot: string)
    (options: GitRunOptions)
    (objectFormat: GitObjectFormat)
    (beforeTreeOid: string)
    (afterTreeOid: string)
    : GitChangeSet =
    let entries =
        computeChangeSet repoRoot options objectFormat beforeTreeOid afterTreeOid
    let id = computeChangeSetIdentity beforeTreeOid afterTreeOid entries
    {
        SchemaVersion = GitChangeSetSchemaVersion
        ChangeSetId = id
        ChangeSetVersion = ChangeSetIdentityVersion
        BeforeTreeOid = beforeTreeOid
        AfterTreeOid = afterTreeOid
        ObjectFormat = objectFormat
        Entries = entries
    }

// -----------------------------------------------------------------------------
// Source-change lookup helpers
// -----------------------------------------------------------------------------

let hasChangeOfKind
    (entries: GitChangeEntry list)
    (kind: GitChangeKind)
    (canonicalPath: string)
    : bool =
    entries
    |> List.exists (fun e ->
        e.ChangeKind = kind && e.CanonicalPath = canonicalPath)

let findChange
    (entries: GitChangeEntry list)
    (canonicalPath: string)
    : GitChangeEntry option =
    entries |> List.tryFind (fun e -> e.CanonicalPath = canonicalPath)

let hasAnyChange
    (entries: GitChangeEntry list)
    (canonicalPath: string)
    : bool =
    entries |> List.exists (fun e -> e.CanonicalPath = canonicalPath)

let declaredRelevantTouched
    (entries: GitChangeEntry list)
    (declared: string list)
    : string list =
    let entryPaths =
        entries |> List.map (fun e -> e.CanonicalPath) |> Set.ofList
    declared |> List.filter (fun p -> Set.contains p entryPaths)
