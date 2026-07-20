module Circus.Tooling.SourcePolicy.Inventory

/// Git inventory capture used by the source-policy verifier.
///
/// ``git ls-files -z`` is the only authoritative way to enumerate the
/// tracked tree robustly: filenames can legally contain spaces,
/// tabs, embedded newlines, quotes, backslashes, leading dashes,
/// and non-ASCII Unicode characters.  Reading its stdout through a
/// text decoder corrupts that contract (invalid UTF-8 bytes are
/// replaced with ``U+FFFD``, an initial BOM is consumed).
///
/// This module therefore consumes Git's output through
/// ``ProcessRunner.runProcessBytes`` which reads directly from
/// ``Process.StandardOutput.BaseStream`` and feeds the captured
/// bytes verbatim into ``NulInventory.parse``.  Frame-splitting
/// happens **before** any character decoding, and the parser fails
/// closed when the byte stream cannot be decoded as strict UTF-8.

#nowarn "3261"

open System
open System.Diagnostics
open System.IO
open System.Threading

open Paths
open ProcessRunner
open NulInventory

type GitRoot =
    | Root of string
    | NotARepository

/// Discover the Git repository root by invoking ``git rev-parse
/// --show-toplevel``.  The text-mode runner is appropriate here
/// because the output is a single line of ASCII.
let discoverRoot (startDir: string) : GitRoot =
    let argv = [ "git"; "rev-parse"; "--show-toplevel" ]
    let result =
        runProcessText argv (Some startDir) CancellationToken.None
    match result.Outcome with
    | Exited 0 ->
        let trimmed = result.Output.Trim()
        if String.IsNullOrEmpty trimmed then NotARepository
        else Root(toPosix trimmed)
    | Exited _
    | NonzeroExit _
    | SpawnFailure _
    | CleanupFailure _
    | OutputFailure _
    | Cancelled _ -> NotARepository

type InventoryEntry = { RelativePath: string; IsTracked: bool }

/// Run ``git <args>`` and capture stdout as raw bytes.  Returns
/// ``Ok`` with the byte buffer on success and ``Error`` with the
/// numeric exit code on failure.  Cancellation is mapped to a
/// distinct ``Error`` shape so callers cannot confuse it with a
/// non-zero exit.
type private GitBytesResult =
    | GitOk of bytes: byte[]
    | GitFailed of exitCode: int
    | GitOperational of detail: string

let private runGitBytes (repoRoot: string) (args: string list) : GitBytesResult =
    let argv = "git" :: args
    let result = runProcessBytes argv (Some repoRoot) CancellationToken.None
    match result.Outcome with
    | Exited 0 -> GitOk result.Output
    | Exited n -> GitFailed n
    | NonzeroExit code -> GitFailed code
    | SpawnFailure d -> GitOperational (sprintf "spawn failure: %s" d)
    | CleanupFailure d -> GitOperational (sprintf "cleanup failure: %s" d)
    | OutputFailure d -> GitOperational (sprintf "output failure: %s" d)
    | Cancelled d -> GitOperational (sprintf "cancelled: %s" d)

let private toPathResult (r: ParseResult) : Result<string list, string> =
    match r with
    | NulInventory.Ok paths -> Result.Ok paths
    | NulInventory.Error d -> Result.Error (NulInventory.renderDiagnostic d)

let enumerate (repoRoot: string) : Result<InventoryEntry list, string> =
    match runGitBytes repoRoot [ "ls-files"; "--cached"; "--others"; "--exclude-standard"; "-z" ] with
    | GitOperational detail -> Result.Error (sprintf "git ls-files operational error: %s" detail)
    | GitFailed code -> Result.Error (sprintf "git ls-files failed (exit %d)" code)
    | GitOk bytes ->
        match toPathResult (NulInventory.parse "git ls-files --cached --others --exclude-standard -z" bytes) with
        | Result.Error detail -> Result.Error detail
        | Result.Ok raw ->
            let entries =
                raw
                |> List.map (fun rel -> { RelativePath = toPosix rel; IsTracked = true })
            Result.Ok entries

let splitTrackedUntracked (repoRoot: string) (entries: InventoryEntry list) : Result<InventoryEntry list, string> =
    match runGitBytes repoRoot [ "ls-files"; "--cached"; "-z" ] with
    | GitOperational detail -> Result.Error (sprintf "git ls-files --cached operational error: %s" detail)
    | GitFailed code -> Result.Error (sprintf "git ls-files --cached failed (exit %d)" code)
    | GitOk bytes ->
        match toPathResult (NulInventory.parse "git ls-files --cached -z" bytes) with
        | Result.Error detail -> Result.Error detail
        | Result.Ok tracked ->
            let trackedSet = tracked |> List.map toPosix |> Set.ofList
            Result.Ok(entries |> List.map (fun e -> { e with IsTracked = trackedSet.Contains e.RelativePath }))

/// Outcome of enumerating the tracked file inventory.  ``Ok`` carries
/// the relative paths; ``Error`` carries the git exit code (which
/// the policy runner must treat as an operational failure, not a
/// policy pass — see CP-29).
type TrackedInventory =
    | TrackedFiles of string list
    | TrackedInventoryFailed of exitCode: int

/// Tracked-file inventory used by container-policy (CP-29).  Reads
/// ``git ls-files -z`` through the byte-oriented runner, so the
/// ``TrackedFiles`` payload is byte-faithful (no path corruption on
/// unusual but valid filenames).  Git failure surfaces as
/// ``TrackedInventoryFailed`` so callers cannot confuse it with the
/// empty-inventory case.
let gitTrackedFiles (root: string) : TrackedInventory =
    match runGitBytes root [ "ls-files"; "-z" ] with
    | GitOk bytes ->
        match toPathResult (NulInventory.parse "git ls-files -z" bytes) with
        | Result.Ok paths -> TrackedFiles paths
        | Result.Error _ -> TrackedInventoryFailed -1
    | GitFailed code -> TrackedInventoryFailed code
    | GitOperational _ -> TrackedInventoryFailed -1

/// Same shape as ``gitTrackedFiles`` but returns the parse error
/// directly when the byte stream cannot be decoded, so callers can
/// surface the diagnostic.
let gitTrackedFilesResult (root: string) : Result<string list, int> =
    match runGitBytes root [ "ls-files"; "-z" ] with
    | GitOk bytes ->
        match toPathResult (NulInventory.parse "git ls-files -z" bytes) with
        | Result.Ok paths -> Result.Ok paths
        | Result.Error _ -> Result.Error -1
    | GitFailed code -> Result.Error code
    | GitOperational _ -> Result.Error -1