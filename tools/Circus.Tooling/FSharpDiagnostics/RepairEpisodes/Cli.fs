module Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Cli

open System.IO
open Circus.Tooling.FSharpDiagnostics.Paths
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Engine
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Serialization

/// Exit codes for the repair-episode subsystem.
module ExitCode =
    let pass = 0
    let policyFailure = 1
    let operationalError = 2

type Command =
    | InventoryCmd
    | RegenerateCmd
    | VerifyCmd
    | ShowCmd of episodeId: string
    | HelpCmd

let helpText () : string =
    "fsharp-diagnostics repair-episodes — deterministic repair-episode linker\n"
    + "\n"
    + "Usage:\n"
    + "  circus-tooling fsharp-diagnostics repair-episodes inventory\n"
    + "  circus-tooling fsharp-diagnostics repair-episodes regenerate\n"
    + "  circus-tooling fsharp-diagnostics repair-episodes verify\n"
    + "  circus-tooling fsharp-diagnostics repair-episodes show <episode-id>\n"
    + "  circus-tooling fsharp-diagnostics repair-episodes help\n"

let parse (argv: string list) : Result<Command, string> =
    match argv with
    | [] | [ "help" ] | [ "-h" ] | [ "--help" ] -> Ok HelpCmd
    | [ "inventory" ] -> Ok InventoryCmd
    | [ "regenerate" ] -> Ok RegenerateCmd
    | [ "verify" ] -> Ok VerifyCmd
    | [ "show"; id ] -> Ok(ShowCmd id)
    | [ "show" ] -> Result.Error "show requires an episode-id argument"
    | _ ->
        Result.Error
            "usage: circus-tooling fsharp-diagnostics repair-episodes {inventory|regenerate|verify|show <episode-id>|help}"

let private renderInventoryHuman (result: Engine.EpisodeEngineResult) : string =
    let s = result.Summary
    let sb = System.Text.StringBuilder()
    let append (line: string) = sb.AppendLine(line) |> ignore
    append "fsharp-diagnostics repair-episodes inventory"
    append(sprintf "  declarations_total: %d" s.DeclarationsTotal)
    append(sprintf "  valid_declarations: %d" s.ValidDeclarations)
    append(sprintf "  invalid_declarations: %d" s.InvalidDeclarations)
    append(sprintf "  missing_captures: %d" s.MissingCaptures)
    append(sprintf "  missing_git_objects: %d" s.MissingGitObjects)
    append(sprintf "  duplicate_episode_keys: %d" s.DuplicateEpisodeKeys)
    append(sprintf "  duplicate_episode_ids: %d" s.DuplicateEpisodeIds)
    append(sprintf "  episodes_total: %d" s.EpisodesTotal)
    append(sprintf "  episodes_qualified: %d" s.EpisodesQualified)
    append(sprintf "  change_sets_total: %d" s.ChangeSetsTotal)
    append(sprintf "  transitions_total: %d" s.TransitionsTotal)
    ignore (sb.ToString())
    sb.ToString()

let runInventory (repoRoot: string) : int =
    let result = Engine.runEpisodeEngine repoRoot Engine.defaultEngineOptions
    stdout.WriteLine(renderInventoryHuman result)
    if result.Summary.InvalidDeclarations > 0
       || result.Summary.MissingCaptures > 0
       || result.Summary.MissingGitObjects > 0
       || result.Summary.DuplicateEpisodeKeys > 0
       || result.Summary.DuplicateEpisodeIds > 0
       || not result.Outcome.Success then
        ExitCode.policyFailure
    else
        ExitCode.pass

let runRegenerate (repoRoot: string) : int =
    let result = Engine.runEpisodeEngine repoRoot Engine.defaultEngineOptions
    if not result.Outcome.Success then
        stderr.WriteLine "error: atomic publication failed"
        ExitCode.operationalError
    else
        stdout.WriteLine
            (sprintf
                "fsharp-diagnostics repair-episodes regenerate: episodes=%d transitions=%d change_sets=%d canonical_byte_identical_after_failure=%b"
                result.RepairEpisodes.Length
                result.Transitions.Length
                result.ChangeSets.Length
                result.Outcome.CanonicalByteIdenticalAfterFailure)
        ExitCode.pass

let runVerify (repoRoot: string) : int =
    let vr = Engine.verifyPipeline repoRoot Engine.defaultEngineOptions
    let issueCount = List.length vr.Issues
    stdout.WriteLine
        (sprintf
            "fsharp-diagnostics repair-episodes verify: episodes_validated=%d transitions_validated=%d issues=%d"
            vr.RepairEpisodesValidated
            vr.TransitionsValidated
            issueCount)
    if issueCount = 0 then ExitCode.pass else ExitCode.policyFailure

let runShow (repoRoot: string) (episodeId: string) : int =
    let result = Engine.runEpisodeEngine repoRoot Engine.defaultEngineOptions
    match result.RepairEpisodes |> List.tryFind (fun e -> e.EpisodeId = episodeId) with
    | Some e ->
        let sb = System.Text.StringBuilder()
        sb.AppendLine("fsharp-diagnostics repair-episodes show") |> ignore
        sb.AppendLine(sprintf "  episode_id: %s" e.EpisodeId) |> ignore
        sb.AppendLine(sprintf "  episode_key: %s" e.EpisodeKey) |> ignore
        sb.AppendLine(sprintf "  before_capture_id: %s" e.BeforeCaptureId) |> ignore
        sb.AppendLine(sprintf "  after_capture_id: %s" e.AfterCaptureId) |> ignore
        sb.AppendLine(sprintf "  before_tree_oid: %s" e.BeforeTreeOid) |> ignore
        sb.AppendLine(sprintf "  after_tree_oid: %s" e.AfterTreeOid) |> ignore
        sb.AppendLine(sprintf "  change_set_id: %s" e.ChangeSetId) |> ignore
        sb.AppendLine(sprintf "  verification_level: %s" (verificationLevelToken e.VerificationLevel)) |> ignore
        sb.AppendLine(sprintf "  qualification: %s" (episodeQualificationStatusToken e.Qualification.Status)) |> ignore
        stdout.WriteLine(sb.ToString())
        ExitCode.pass
    | None ->
        stderr.WriteLine(sprintf "error: episode %s not found" episodeId)
        ExitCode.operationalError

let run (argv: string list) : int =
    match parse argv with
    | Ok HelpCmd ->
        stdout.WriteLine(helpText())
        ExitCode.pass
    | Ok InventoryCmd ->
        match Circus.Tooling.SourcePolicy.Cli.resolveRepoRoot () with
        | Ok root -> runInventory root
        | Result.Error msg ->
            stderr.WriteLine(sprintf "error: %s" msg)
            ExitCode.operationalError
    | Ok RegenerateCmd ->
        match Circus.Tooling.SourcePolicy.Cli.resolveRepoRoot () with
        | Ok root -> runRegenerate root
        | Result.Error msg ->
            stderr.WriteLine(sprintf "error: %s" msg)
            ExitCode.operationalError
    | Ok VerifyCmd ->
        match Circus.Tooling.SourcePolicy.Cli.resolveRepoRoot () with
        | Ok root -> runVerify root
        | Result.Error msg ->
            stderr.WriteLine(sprintf "error: %s" msg)
            ExitCode.operationalError
    | Ok(ShowCmd id) ->
        match Circus.Tooling.SourcePolicy.Cli.resolveRepoRoot () with
        | Ok root -> runShow root id
        | Result.Error msg ->
            stderr.WriteLine(sprintf "error: %s" msg)
            ExitCode.operationalError
    | Result.Error msg ->
        stderr.WriteLine(sprintf "error: %s" msg)
        stderr.WriteLine(helpText())
        ExitCode.operationalError
