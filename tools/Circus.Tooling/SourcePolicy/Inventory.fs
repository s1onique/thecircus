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

/// Outcome of ``git ls-files`` capture.  Operational failures of the
/// ``git`` invocation are kept distinct from NUL parse failures so
/// the policy can attribute them correctly.
type private InventoryParse =
    | InventoryOk of paths: string list
    | InventoryDecodeError of NulInventory.DecodeDiagnostic
    | InventoryGitFailure of ProcessOutcome

let private runGitBytes (repoRoot: string) (args: string list) : InventoryParse =
    let argv = "git" :: args
    let result = runProcessBytes argv (Some repoRoot) CancellationToken.None
    let cmdId = "git " + String.concat " " args
    match result.Outcome with
    | Exited (0, _) ->
        match NulInventory.parse cmdId result.Output with
        | NulInventory.Ok paths -> InventoryOk paths
        | NulInventory.Error d -> InventoryDecodeError d
    | outcome -> InventoryGitFailure outcome

type InventoryResult =
    | InventoryEntries of InventoryEntry list
    | InventoryDiagnostic of NulInventory.DecodeDiagnostic

/// Convert a ``git`` operational failure to a structured diagnostic
/// that preserves the original ``ProcessOutcome`` in the safe-bytes
/// field.  This is the ONLY path that constructs a decode diagnostic
/// from a non-decode failure, and the safe-bytes field is sanitised
/// via ``renderDiagnostic`` before any consumer sees it.
let private gitFailureDiagnostic (cmdId: string) (outcome: ProcessOutcome) : NulInventory.DecodeDiagnostic =
    { NulInventory.CommandId = cmdId
      NulInventory.RecordIndex = -1
      NulInventory.ByteOffset = 0
      NulInventory.Category = NulInventory.UnterminatedFinalRecord -1
      NulInventory.SafeBytesHex =
        "git operational failure (not a NUL decode error): "
        + string (sprintf "%A" outcome) }

let enumerate (repoRoot: string) : InventoryResult =
    match runGitBytes repoRoot [ "ls-files"; "--cached"; "--others"; "--exclude-standard"; "-z" ] with
    | InventoryDecodeError d -> InventoryDiagnostic d
    | InventoryGitFailure outcome -> InventoryDiagnostic (gitFailureDiagnostic "git ls-files" outcome)
    | InventoryOk raw ->
        let entries =
            raw
            |> List.map (fun rel -> { RelativePath = toPosix rel; IsTracked = true })
        InventoryEntries entries

let splitTrackedUntracked (repoRoot: string) (entries: InventoryEntry list) : InventoryResult =
    match runGitBytes repoRoot [ "ls-files"; "--cached"; "-z" ] with
    | InventoryDecodeError d -> InventoryDiagnostic d
    | InventoryGitFailure outcome -> InventoryDiagnostic (gitFailureDiagnostic "git ls-files" outcome)
    | InventoryOk tracked ->
        let trackedSet = tracked |> List.map toPosix |> Set.ofList
        InventoryEntries(entries |> List.map (fun e -> { e with IsTracked = trackedSet.Contains e.RelativePath }))

type TrackedInventory =
    | TrackedFiles of string list
    | TrackedInventoryFailed of diagnostic: NulInventory.DecodeDiagnostic

let gitTrackedFiles (root: string) : TrackedInventory =
    match runGitBytes root [ "ls-files"; "-z" ] with
    | InventoryOk paths -> TrackedFiles paths
    | InventoryDecodeError d -> TrackedInventoryFailed d
    | InventoryGitFailure outcome -> TrackedInventoryFailed (gitFailureDiagnostic "git ls-files" outcome)

let gitTrackedFilesResult (root: string) : Result<string list, NulInventory.DecodeDiagnostic> =
    match gitTrackedFiles root with
    | TrackedFiles paths -> Result.Ok paths
    | TrackedInventoryFailed d -> Result.Error d
