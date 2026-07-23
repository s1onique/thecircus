module Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Git

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Text
open Circus.Tooling.FSharpDiagnostics.Hashing
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain

// =============================================================================
// Bounded Git process seam
// =============================================================================
//
// This module is the only production code that invokes the Git binary.
// All Git invocations go through ``runGit`` which:
//   * sets WorkingDirectory to the supplied repository root,
//   * uses ProcessStartInfo.ArgumentList (no shell),
//   * redirects stdout and stderr,
//   * enforces a bounded timeout (default 60s),
//   * enforces bounded stdout/stderr sizes (default 32 MiB each),
//   * preserves actual exit codes,
//   * distinguishes timeout, launch failure, output overflow, and Git
//     nonzero exit,
//   * fails closed when output cannot be fully read,
//   * terminates the process tree on timeout where supported,
//   * never substitutes an empty output for an incomplete read.
//
// ``runGit`` never silently falls back to the caller's CWD.

exception GitLaunchFailure of string
exception GitTimeoutFailure of string
exception GitOverflowFailure of string
exception GitIoFailure of string

type GitRunOptions = {
    Timeout: TimeSpan
    MaxStdoutBytes: int64
    MaxStderrBytes: int64
}

let defaultGitRunOptions : GitRunOptions =
    { Timeout = TimeSpan.FromSeconds 60.0
      MaxStdoutBytes = 32L * 1024L * 1024L
      MaxStderrBytes = 32L * 1024L * 1024L }

type GitRunResult = {
    ExitCode: int
    Stdout: string
    Stderr: string
}

/// Run a Git command.  The repository root is always set; arguments are
/// passed via ``ArgumentList`` so a shell is never invoked.  The function
/// is total over the (timeout, max-bytes) bound: any out-of-bound condition
/// is signalled by a dedicated exception.
let runGit
    (repoRoot: string)
    (options: GitRunOptions)
    (args: string list)
    : GitRunResult =
    if System.String.IsNullOrWhiteSpace repoRoot then
        raise (GitLaunchFailure "git: empty repository root")
    if not (Directory.Exists repoRoot) then
        raise (GitLaunchFailure(sprintf "git: repository root does not exist: %s" repoRoot))

    let psi = ProcessStartInfo()
    psi.FileName <- "git"
    psi.WorkingDirectory <- repoRoot
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    psi.RedirectStandardInput <- false
    psi.CreateNoWindow <- true
    psi.StandardOutputEncoding <- Encoding.UTF8
    psi.StandardErrorEncoding <- Encoding.UTF8
    // The -c core.quotepath=false argument is added by callers that need
    // unquoted output (e.g. diff-tree --raw).  All other global defaults
    // remain explicit here.
    for a in args do
        psi.ArgumentList.Add a

    let proc = Process.Start(psi)
    if isNull proc then
        raise (GitLaunchFailure "git: Process.Start returned null")

    let stdoutDone = System.Threading.Tasks.Task.Run(fun () ->
        use ms = new MemoryStream()
        let mutable total : int64 = 0L
        let buf = Array.zeroCreate<byte> 8192
        let mutable go = true
        while go do
            let n = proc.StandardOutput.BaseStream.Read(buf, 0, buf.Length)
            if n <= 0 then go <- false
            else
                total <- total + int64 n
                if total > options.MaxStdoutBytes then
                    try proc.Kill(true) with _ -> ()
                    raise (GitOverflowFailure(sprintf "git: stdout overflow (>%d bytes)" options.MaxStdoutBytes))
                ms.Write(buf, 0, n)
        Encoding.UTF8.GetString(ms.ToArray()))

    let stderrDone = System.Threading.Tasks.Task.Run(fun () ->
        use ms = new MemoryStream()
        let mutable total : int64 = 0L
        let buf = Array.zeroCreate<byte> 8192
        let mutable go = true
        while go do
            let n = proc.StandardError.BaseStream.Read(buf, 0, buf.Length)
            if n <= 0 then go <- false
            else
                total <- total + int64 n
                if total > options.MaxStderrBytes then
                    try proc.Kill(true) with _ -> ()
                    raise (GitOverflowFailure(sprintf "git: stderr overflow (>%d bytes)" options.MaxStderrBytes))
                ms.Write(buf, 0, n)
        Encoding.UTF8.GetString(ms.ToArray()))

    let exited = proc.WaitForExit(int options.Timeout.TotalMilliseconds)
    if not exited then
        try proc.Kill(true) with _ -> ()
        try stdoutDone.Wait(2000) |> ignore with _ -> ()
        try stderrDone.Wait(2000) |> ignore with _ -> ()
        raise (GitTimeoutFailure(sprintf "git: timed out after %O" options.Timeout))

    let stdout =
        try stdoutDone.GetAwaiter().GetResult()
        with
        | ex ->
            raise (GitIoFailure(sprintf "git: stdout read failed: %s" ex.Message))
    let stderr =
        try stderrDone.GetAwaiter().GetResult()
        with
        | ex ->
            raise (GitIoFailure(sprintf "git: stderr read failed: %s" ex.Message))

    { ExitCode = proc.ExitCode
      Stdout = stdout
      Stderr = stderr }

// =============================================================================
// Object format detection
// =============================================================================

exception GitObjectFormatFailure of string

/// Detect the repository object format exactly once per run.  Only the
/// ``storage`` form is accepted (sha1 or sha256).  The detected format is
/// cached so subsequent calls within the same process return identical
/// results without re-invoking Git.
let private objectFormatCache = System.Collections.Concurrent.ConcurrentDictionary<string, GitObjectFormat>()

let detectObjectFormat (repoRoot: string) : GitObjectFormat =
    match objectFormatCache.TryGetValue repoRoot with
    | true, fmt -> fmt
    | false, _ ->
        let result = runGit repoRoot defaultGitRunOptions [ "rev-parse"; "--show-object-format=storage" ]
        if result.ExitCode <> 0 then
            raise (GitObjectFormatFailure(sprintf "git rev-parse --show-object-format failed: %s" result.Stderr))
        let token = result.Stdout.Trim()
        match tryParseGitObjectFormat token with
        | Some fmt ->
            objectFormatCache.TryAdd(repoRoot, fmt) |> ignore
            fmt
        | None ->
            raise (GitObjectFormatFailure(sprintf "git: unrecognised object format token: %s" token))

let clearObjectFormatCache () : unit =
    objectFormatCache.Clear()

// =============================================================================
// OID validation
// =============================================================================

/// Validate a Git OID for the given object format.  Returns true when the
/// OID is full-width hexadecimal of the correct width.
let isValidOid (fmt: GitObjectFormat) (oid: string) : bool =
    if isNull oid then false
    elif oid.Length <> gitObjectFormatWidth fmt then false
    else
        let mutable ok = true
        for c in oid do
            if not (Char.IsAsciiHexDigit c) then
                ok <- false
        ok

// =============================================================================
// Identity resolution
// =============================================================================

let private verifyOne (repoRoot: string) (options: GitRunOptions) (rev: string) (suffix: string) : string =
    let result =
        runGit
            repoRoot
            options
            [ "rev-parse"; "--verify"; "--end-of-options"; rev + suffix ]
    if result.ExitCode <> 0 then
        raise (GitIdentityFailure(sprintf "git rev-parse %s%s failed: %s" rev suffix result.Stderr))
    let trimmed = result.Stdout.Trim()
    if trimmed.Length = 0 then
        raise (GitIdentityFailure(sprintf "git rev-parse %s%s returned empty output" rev suffix))
    trimmed

/// Resolve a commit and its tree.  Uses ``^{commit}`` to reject non-commit
/// objects and ``^{tree}`` to derive the tree OID.
let resolveCommitAndTree
    (repoRoot: string)
    (options: GitRunOptions)
    (commitRef: string)
    : string * string =
    let commitOid = verifyOne repoRoot options commitRef "^{commit}"
    let treeOid = verifyOne repoRoot options commitRef "^{tree}"
    commitOid, treeOid

let isAncestor (repoRoot: string) (options: GitRunOptions) (ancestor: string) (descendant: string) : bool =
    let result =
        runGit
            repoRoot
            options
            [ "merge-base"; "--is-ancestor"; ancestor; descendant ]
    if result.ExitCode = 0 then true
    elif result.ExitCode = 1 then false
    else
        raise (GitIdentityFailure(sprintf "git merge-base --is-ancestor failed: %s" result.Stderr))

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
        raise (GitIdentityFailure(sprintf "git rev-list failed: %s" result.Stderr))
    result.Stdout.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries)
    |> Array.toList
    |> List.map (fun s -> s.Trim())

/// Resolve a complete repair-episode Git identity.  Validates OIDs against
/// the detected object format, refuses abbreviated or wrong-width OIDs,
/// refuses non-ancestor ranges, and records the ordered commit range.
let resolveGitIdentity
    (repoRoot: string)
    (options: GitRunOptions)
    (beforeCommitInput: string)
    (afterCommitInput: string)
    : GitIdentityResolution =
    let fmt = detectObjectFormat repoRoot
    if not (isValidOid fmt beforeCommitInput) then
        raise (GitIdentityFailure(sprintf "git: invalid before commit OID for %s format: %s" (gitObjectFormatToken fmt) beforeCommitInput))
    if not (isValidOid fmt afterCommitInput) then
        raise (GitIdentityFailure(sprintf "git: invalid after commit OID for %s format: %s" (gitObjectFormatToken fmt) afterCommitInput))
    let beforeCommit, beforeTree = resolveCommitAndTree repoRoot options beforeCommitInput
    let afterCommit, afterTree = resolveCommitAndTree repoRoot options afterCommitInput
    if not (isAncestor repoRoot options beforeCommit afterCommit) then
        raise (GitIdentityFailure(sprintf "git: %s is not an ancestor of %s" beforeCommit afterCommit))
    let commitRange = revListAncestryPath repoRoot options beforeCommit afterCommit
    { BeforeCommitOid = beforeCommit
      BeforeTreeOid = beforeTree
      AfterCommitOid = afterCommit
      AfterTreeOid = afterTree
      CommitRange = commitRange
      ObjectFormat = fmt }

// =============================================================================
// Change set extraction
// =============================================================================
//
// `git diff-tree` invocation is exactly the form mandated by ACT §7.  No
// rename detection is requested.  Paths are NUL-delimited.

/// Parse the NUL-delimited output of ``git diff-tree --raw -z --no-renames``.
///
/// Each record is six NUL-separated tokens:
///   :<before_mode> <after_mode> <before_blob> <after_blob> <status>\0<path>\0
///
/// A "type change" (link/submodule) emits status 'T' which we surface
/// explicitly as ``TypeChanged`` so callers can fail closed or treat it
/// separately.
let private parseDiffTreeRaw (raw: string) (objectFormat: GitObjectFormat) : GitChangeEntry list =
    // Split on NUL and drop the trailing empty token if present.
    let parts =
        raw.Split([| '\u0000' |], StringSplitOptions.None)
        |> Array.toList
    let mutable entries : GitChangeEntry list = []
    let mutable i = 0
    let expectWidth (token: string) =
        if token.Length <> gitObjectFormatWidth objectFormat then
            raise (GitChangeParseFailure(sprintf "git diff-tree: OID %s has width %d, expected %d for %s"
                                            token token.Length (gitObjectFormatWidth objectFormat) (gitObjectFormatToken objectFormat)))
    while i < parts.Length do
        let header = parts.[i]
        if System.String.IsNullOrEmpty header then
            i <- i + 1
        else
            if i + 1 >= parts.Length then
                raise (GitChangeParseFailure "git diff-tree: unexpected end of input before path")
            let path = parts.[i + 1]
            if not (header.StartsWith ":") then
                raise (GitChangeParseFailure(sprintf "git diff-tree: expected ':' header, got %s" header))
            // :100644 100644 <blob1> <blob2> <status>
            let body = header.Substring(1)
            let tokens = body.Split([| ' ' |], StringSplitOptions.RemoveEmptyEntries)
            if tokens.Length < 5 then
                raise (GitChangeParseFailure(sprintf "git diff-tree: header has %d tokens, expected ≥ 5: %s" tokens.Length body))
            let beforeMode = tokens.[0]
            let afterMode = tokens.[1]
            let beforeBlob = tokens.[2]
            let afterBlob = tokens.[3]
            let status = tokens.[4]
            // Reject zero OIDs (mode-only changes) only if explicitly invalid; we
            // surface them as mode-only changes by leaving both blob OIDs absent
            // and using Modified with same canonical path.
            if beforeBlob <> "0000000000000000000000000000000000000000" && beforeBlob <> "0000000000000000000000000000000000000000000000000000000000000000" then
                expectWidth beforeBlob
            if afterBlob <> "0000000000000000000000000000000000000000" && afterBlob <> "0000000000000000000000000000000000000000000000000000000000000000" then
                expectWidth afterBlob
            let beforeBlobOpt =
                if beforeBlob = "0000000000000000000000000000000000000000" || beforeBlob = "0000000000000000000000000000000000000000000000000000000000000000" then
                    None
                else
                    Some beforeBlob
            let afterBlobOpt =
                if afterBlob = "0000000000000000000000000000000000000000" || afterBlob = "0000000000000000000000000000000000000000000000000000000000000000" then
                    None
                else
                    Some afterBlob
            let changeKind =
                match status with
                | "A" -> Added
                | "M" -> Modified
                | "D" -> Deleted
                | "T" -> TypeChanged
                | _ ->
                    raise (GitChangeParseFailure(sprintf "git diff-tree: unrecognised status token %s for path %s" status path))
            let canonicalPath =
                if path.Contains ".." then
                    raise (GitChangeParseFailure(sprintf "git diff-tree: path escapes repository: %s" path))
                elif System.IO.Path.IsPathRooted path then
                    raise (GitChangeParseFailure(sprintf "git diff-tree: absolute path not allowed: %s" path))
                else
                    path.Replace('\\', '/')
            let entry : GitChangeEntry =
                { BeforeMode = beforeMode
                  AfterMode = afterMode
                  BeforeBlobOid = beforeBlobOpt
                  AfterBlobOid = afterBlobOpt
                  ChangeKind = changeKind
                  CanonicalPath = canonicalPath }
            entries <- entry :: entries
            i <- i + 2
    // Sort entries ordinally by canonical path.
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
              beforeTreeOid
              afterTreeOid ]
    if result.ExitCode <> 0 then
        raise (GitChangeParseFailure(sprintf "git diff-tree failed: %s" result.Stderr))
    parseDiffTreeRaw result.Stdout objectFormat

// =============================================================================
// Change-set identity
// =============================================================================
//
// The identity is a lowercase SHA-256 over a canonical, length-prefixed
// encoding containing the change_set_version, the two tree OIDs, and the
// ordered (mode, blob, kind, path) tuples.  We never use delimiter-only
// concatenation.

let private lengthPrefixedString (sb: StringBuilder) (value: string) : unit =
    sb.Append(value.Length.ToString("x8", CultureInfo.InvariantCulture)) |> ignore
    sb.Append(':') |> ignore
    sb.Append(value) |> ignore

let private lengthPrefixedInt (sb: StringBuilder) (value: int) : unit =
    sb.Append(value.ToString("x8", CultureInfo.InvariantCulture)) |> ignore

let private lengthPrefixedOptBlob (sb: StringBuilder) (oid: string option) : unit =
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
    let entries = computeChangeSet repoRoot options objectFormat beforeTreeOid afterTreeOid
    let id = computeChangeSetIdentity beforeTreeOid afterTreeOid entries
    { SchemaVersion = GitChangeSetSchemaVersion
      ChangeSetId = id
      ChangeSetVersion = ChangeSetIdentityVersion
      BeforeTreeOid = beforeTreeOid
      AfterTreeOid = afterTreeOid
      ObjectFormat = objectFormat
      Entries = entries }

// =============================================================================
// Source-change lookup helpers
// =============================================================================

/// True when the change set contains an entry with the given canonical path
/// matching the given kind.
let hasChangeOfKind
    (entries: GitChangeEntry list)
    (kind: GitChangeKind)
    (canonicalPath: string)
    : bool =
    entries
    |> List.exists (fun e -> e.ChangeKind = kind && e.CanonicalPath = canonicalPath)

/// Lookup a single change for the given path; returns None if no entry
/// exists for the path.
let findChange
    (entries: GitChangeEntry list)
    (canonicalPath: string)
    : GitChangeEntry option =
    entries
    |> List.tryFind (fun e -> e.CanonicalPath = canonicalPath)

/// True when any entry touches the given path (any kind).
let hasAnyChange
    (entries: GitChangeEntry list)
    (canonicalPath: string)
    : bool =
    entries |> List.exists (fun e -> e.CanonicalPath = canonicalPath)

/// Lookup all declared relevant paths that were touched by any change.
let declaredRelevantTouched
    (entries: GitChangeEntry list)
    (declared: string list)
    : string list =
    let entryPaths = entries |> List.map (fun e -> e.CanonicalPath) |> Set.ofList
    declared |> List.filter (fun p -> Set.contains p entryPaths)
