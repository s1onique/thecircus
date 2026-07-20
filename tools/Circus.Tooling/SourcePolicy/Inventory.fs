module Circus.Tooling.SourcePolicy.Inventory

/// Git inventory capture used by the source-policy verifier.

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

let discoverRoot (startDir: string) : GitRoot =
    let argv = [ "git"; "rev-parse"; "--show-toplevel" ]
    let result =
        runProcessText argv (Some startDir) CancellationToken.None
    match result.Outcome with
    | Exited (0, _) ->
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

/// Discriminated failure surface.  Operational failures of the ``git``
/// invocation are kept distinct from NUL parse failures so the
/// policy and consumers can attribute them correctly.  Only
/// ``NulDecodeFailure`` should be rendered through
/// ``NulInventory.renderDiagnostic``; the ``Git*Failure`` cases
/// carry operational diagnostics instead.
type InventoryFailure =
    | GitSpawnFailure of detail: string
    | GitNonzeroExit of exitCode: int * stderr: string
    | GitCancelled of detail: string
    | GitCleanupFailure of detail: string
    | GitOutputFailure of detail: string
    | NulDecodeFailure of NulInventory.DecodeDiagnostic

let renderInventoryFailure (f: InventoryFailure) : string =
    match f with
    | GitSpawnFailure d -> sprintf "git spawn failure: %s" d
    | GitNonzeroExit (code, stderr) -> sprintf "git nonzero exit (code=%d): %s" code stderr
    | GitCancelled d -> sprintf "git cancelled: %s" d
    | GitCleanupFailure d -> sprintf "git cleanup failure: %s" d
    | GitOutputFailure d -> sprintf "git output failure: %s" d
    | NulDecodeFailure d -> NulInventory.renderDiagnostic d

/// Outcome of ``git ls-files`` capture.
type private InventoryParse =
    | InventoryOk of paths: string list
    | InventoryDecodeError of NulInventory.DecodeDiagnostic
    | InventoryGitFailure of InventoryFailure

/// Convert a ``ProcessOutcome`` produced by the ``git`` invocation
/// into the appropriate ``InventoryFailure`` case.
let private fromOutcome (outcome: ProcessOutcome) (stderr: string) : InventoryFailure =
    match outcome with
    | SpawnFailure (d, _) -> GitSpawnFailure d
    | NonzeroExit (code, _) -> GitNonzeroExit (code, stderr)
    | Cancelled d -> GitCancelled d
    | CleanupFailure d -> GitCleanupFailure d
    | OutputFailure (d, _) -> GitOutputFailure d
    | Exited (n, _) -> GitNonzeroExit (n, stderr)

let private runGitBytes (repoRoot: string) (args: string list) : InventoryParse =
    let argv = "git" :: args
    let result = runProcessBytes argv (Some repoRoot) CancellationToken.None
    let cmdId = "git " + String.concat " " args
    match result.Outcome with
    | Exited (0, _) ->
        match NulInventory.parse cmdId result.Output with
        | NulInventory.Ok paths -> InventoryOk paths
        | NulInventory.Error d -> InventoryDecodeError d
    | outcome -> InventoryGitFailure (fromOutcome outcome result.Stderr)

type InventoryResult =
    | InventoryEntries of InventoryEntry list
    | InventoryFailed of InventoryFailure

let enumerate (repoRoot: string) : InventoryResult =
    match runGitBytes repoRoot [ "ls-files"; "--cached"; "--others"; "--exclude-standard"; "-z" ] with
    | InventoryDecodeError d -> InventoryFailed (NulDecodeFailure d)
    | InventoryGitFailure f -> InventoryFailed f
    | InventoryOk raw ->
        let entries =
            raw
            |> List.map (fun rel -> { RelativePath = toPosix rel; IsTracked = true })
        InventoryEntries entries

let splitTrackedUntracked (repoRoot: string) (entries: InventoryEntry list) : InventoryResult =
    match runGitBytes repoRoot [ "ls-files"; "--cached"; "-z" ] with
    | InventoryDecodeError d -> InventoryFailed (NulDecodeFailure d)
    | InventoryGitFailure f -> InventoryFailed f
    | InventoryOk tracked ->
        let trackedSet = tracked |> List.map toPosix |> Set.ofList
        InventoryEntries(entries |> List.map (fun e -> { e with IsTracked = trackedSet.Contains e.RelativePath }))

type TrackedInventory =
    | TrackedFiles of string list
    | TrackedInventoryFailed of InventoryFailure

let gitTrackedFiles (root: string) : TrackedInventory =
    match runGitBytes root [ "ls-files"; "-z" ] with
    | InventoryOk paths -> TrackedFiles paths
    | InventoryDecodeError d -> TrackedInventoryFailed (NulDecodeFailure d)
    | InventoryGitFailure f -> TrackedInventoryFailed f

let gitTrackedFilesResult (root: string) : Result<string list, InventoryFailure> =
    match gitTrackedFiles root with
    | TrackedFiles paths -> Result.Ok paths
    | TrackedInventoryFailed f -> Result.Error f
