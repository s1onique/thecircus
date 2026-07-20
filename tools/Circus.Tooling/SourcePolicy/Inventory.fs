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

/// Discover the Git repository root.
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

type private GitBytesResult =
    | GitOk of bytes: byte[]
    | GitFailed of exitCode: int
    | GitOperational of detail: string

let private runGitBytes (repoRoot: string) (args: string list) : GitBytesResult =
    let argv = "git" :: args
    let result = runProcessBytes argv (Some repoRoot) CancellationToken.None
    match result.Outcome with
    | Exited (0, _) -> GitOk result.Output
    | Exited (_, note) -> GitFailed 1
    | NonzeroExit (code, _) -> GitFailed code
    | SpawnFailure (d, _) -> GitOperational (sprintf "spawn failure: %s" d)
    | CleanupFailure d -> GitOperational (sprintf "cleanup failure: %s" d)
    | OutputFailure (d, _) -> GitOperational (sprintf "output failure: %s" d)
    | Cancelled _ -> GitOperational "cancelled"

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

/// Outcome of enumerating the tracked file inventory.
type TrackedInventory =
    | TrackedFiles of string list
    | TrackedInventoryFailed of exitCode: int

let gitTrackedFiles (root: string) : TrackedInventory =
    match runGitBytes root [ "ls-files"; "-z" ] with
    | GitOk bytes ->
        match toPathResult (NulInventory.parse "git ls-files -z" bytes) with
        | Result.Ok paths -> TrackedFiles paths
        | Result.Error _ -> TrackedInventoryFailed -1
    | GitFailed code -> TrackedInventoryFailed code
    | GitOperational _ -> TrackedInventoryFailed -1

let gitTrackedFilesResult (root: string) : Result<string list, int> =
    match runGitBytes root [ "ls-files"; "-z" ] with
    | GitOk bytes ->
        match toPathResult (NulInventory.parse "git ls-files -z" bytes) with
        | Result.Ok paths -> Result.Ok paths
        | Result.Error _ -> Result.Error -1
    | GitFailed code -> Result.Error code
    | GitOperational _ -> Result.Error -1
