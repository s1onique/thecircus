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

/// Discover the Git repository root by invoking ``git rev-parse
/// --show-toplevel``.  Cancellation-aware.
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

/// Outcome of parsing ``git ls-files -z`` output.
type private InventoryParse =
    | InventoryOk of paths: string list
    | InventoryError of diagnostic: NulInventory.DecodeDiagnostic

/// Build a structured diagnostic for a non-zero-exit / non-parse
/// path through ``git ls-files``.  Carries the actual command
/// identity and the exit code so the policy can attribute the
/// failure correctly.
let private failureDiagnostic (commandId: string) (exitCode: int) : NulInventory.DecodeDiagnostic =
    { NulInventory.CommandId = commandId
      NulInventory.RecordIndex = -1
      NulInventory.ByteOffset = 0
      NulInventory.Category = NulInventory.UnterminatedFinalRecord exitCode
      NulInventory.SafeBytesHex = "" }

/// Run ``git <args>`` and capture stdout as raw bytes through the
/// strict NUL parser.
let private runGitBytes (repoRoot: string) (args: string list) : InventoryParse =
    let argv = "git" :: args
    let result = runProcessBytes argv (Some repoRoot) CancellationToken.None
    let cmdId = "git " + String.concat " " args
    match result.Outcome with
    | Exited (0, _) ->
        match NulInventory.parse cmdId result.Output with
        | NulInventory.Ok paths -> InventoryOk paths
        | NulInventory.Error d -> InventoryError d
    | Exited (n, _) -> InventoryError (failureDiagnostic cmdId n)
    | NonzeroExit (code, _) -> InventoryError (failureDiagnostic cmdId code)
    | SpawnFailure (_, _) -> InventoryError (failureDiagnostic cmdId -1)
    | CleanupFailure _ -> InventoryError (failureDiagnostic cmdId -1)
    | OutputFailure _ -> InventoryError (failureDiagnostic cmdId -1)
    | Cancelled _ -> InventoryError (failureDiagnostic cmdId -1)

/// Structured enumeration result.
type InventoryResult =
    | InventoryEntries of InventoryEntry list
    | InventoryDiagnostic of NulInventory.DecodeDiagnostic

/// Enumerate the working tree (tracked + untracked).  Diagnostic
/// flows through unchanged.
let enumerate (repoRoot: string) : InventoryResult =
    match runGitBytes repoRoot [ "ls-files"; "--cached"; "--others"; "--exclude-standard"; "-z" ] with
    | InventoryError d -> InventoryDiagnostic d
    | InventoryOk raw ->
        let entries =
            raw
            |> List.map (fun rel -> { RelativePath = toPosix rel; IsTracked = true })
        InventoryEntries entries

/// Enumerate the untracked/ignored split.
let splitTrackedUntracked (repoRoot: string) (entries: InventoryEntry list) : InventoryResult =
    match runGitBytes repoRoot [ "ls-files"; "--cached"; "-z" ] with
    | InventoryError d -> InventoryDiagnostic d
    | InventoryOk tracked ->
        let trackedSet = tracked |> List.map toPosix |> Set.ofList
        InventoryEntries(entries |> List.map (fun e -> { e with IsTracked = trackedSet.Contains e.RelativePath }))

/// Tracked-file inventory used by container-policy (CP-29).
type TrackedInventory =
    | TrackedFiles of string list
    | TrackedInventoryFailed of diagnostic: NulInventory.DecodeDiagnostic

let gitTrackedFiles (root: string) : TrackedInventory =
    match runGitBytes root [ "ls-files"; "-z" ] with
    | InventoryOk paths -> TrackedFiles paths
    | InventoryError d -> TrackedInventoryFailed d

/// Convenience result type.
let gitTrackedFilesResult (root: string) : Result<string list, NulInventory.DecodeDiagnostic> =
    match gitTrackedFiles root with
    | TrackedFiles paths -> Result.Ok paths
    | TrackedInventoryFailed d -> Result.Error d
