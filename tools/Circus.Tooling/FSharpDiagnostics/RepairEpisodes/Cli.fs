module Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Cli

open System.IO
open Circus.Tooling.FSharpDiagnostics.Domain
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
    // Report evidence load errors if any
    if not (List.isEmpty result.EvidenceLoadErrors) then
        append(sprintf "  evidence_load_errors: %d" (List.length result.EvidenceLoadErrors))
    ignore (sb.ToString())
    sb.ToString()

/// Render evidence load errors in human-readable form for CLI output.
let private renderEvidenceLoadErrors (errors: Domain.VerificationEvidenceLoadError list) : string =
    let sb = System.Text.StringBuilder()
    sb.AppendLine("error: verification evidence loading failed") |> ignore
    for err in errors do
        match err with
        | Domain.VerificationEvidenceLoadError.EvidenceFileMissing path ->
            sb.AppendLine(sprintf "  evidence_file_missing: %s" path) |> ignore
        | Domain.VerificationEvidenceLoadError.EvidenceFileUnreadable (path, msg) ->
            sb.AppendLine(sprintf "  evidence_file_unreadable: %s (%s)" path msg) |> ignore
        | Domain.VerificationEvidenceLoadError.DuplicateEvidenceId (path, evid, l1, l2) ->
            sb.AppendLine(sprintf "  duplicate_evidence_id: %s %s (lines %d, %d)" path evid l1 l2) |> ignore
        | Domain.VerificationEvidenceLoadError.ConflictingEvidenceRecord (path, evid, l1, l2) ->
            sb.AppendLine(sprintf "  conflicting_evidence: %s %s (lines %d, %d)" path evid l1 l2) |> ignore
        | Domain.VerificationEvidenceLoadError.UnsupportedEvidenceSchemaVersion (path, ver) ->
            sb.AppendLine(sprintf "  unsupported_evidence_schema: %s %s" path ver) |> ignore
        | Domain.VerificationEvidenceLoadError.ParseError parseErr ->
            match parseErr with
            | Domain.VerificationEvidenceParseError.MalformedJson (src, line, msg) ->
                sb.AppendLine(sprintf "  malformed_json: %s:%d %s" src line msg) |> ignore
            | Domain.VerificationEvidenceParseError.MissingField (src, line, field) ->
                sb.AppendLine(sprintf "  missing_field: %s:%d %s" src line field) |> ignore
            | Domain.VerificationEvidenceParseError.InvalidEvidenceId (src, line, evid) ->
                sb.AppendLine(sprintf "  invalid_evidence_id: %s:%d %s" src line evid) |> ignore
            | Domain.VerificationEvidenceParseError.UnsupportedSchemaVersion (src, line, ver) ->
                sb.AppendLine(sprintf "  unsupported_schema: %s:%d %s" src line ver) |> ignore
            | Domain.VerificationEvidenceParseError.UnknownVerificationKind (src, line, v) ->
                sb.AppendLine(sprintf "  unknown_verification_kind: %s:%d %s" src line v) |> ignore
            | Domain.VerificationEvidenceParseError.UnknownVerificationStatus (src, line, v) ->
                sb.AppendLine(sprintf "  unknown_verification_status: %s:%d %s" src line v) |> ignore
            | Domain.VerificationEvidenceParseError.InvalidExitCode (src, line, ec) ->
                sb.AppendLine(sprintf "  invalid_exit_code: %s:%d %s" src line ec) |> ignore
            | Domain.VerificationEvidenceParseError.InvalidCommitOid (src, line, field, oid) ->
                sb.AppendLine(sprintf "  invalid_commit_oid: %s:%d %s %s" src line field oid) |> ignore
            | Domain.VerificationEvidenceParseError.InvalidTreeOid (src, line, field, oid) ->
                sb.AppendLine(sprintf "  invalid_tree_oid: %s:%d %s %s" src line field oid) |> ignore
            | Domain.VerificationEvidenceParseError.InvalidSha256 (src, line, field, hash) ->
                sb.AppendLine(sprintf "  invalid_sha256: %s:%d %s %s" src line field hash) |> ignore
            | Domain.VerificationEvidenceParseError.PlaceholderEvidenceId (src, line, evid) ->
                sb.AppendLine(sprintf "  placeholder_evidence_id: %s:%d %s" src line evid) |> ignore
            | Domain.VerificationEvidenceParseError.ExpectedObject (src, line) ->
                sb.AppendLine(sprintf "  expected_object: %s:%d" src line) |> ignore
            | Domain.VerificationEvidenceParseError.JsonException (src, line, msg) ->
                sb.AppendLine(sprintf "  json_exception: %s:%d %s" src line msg) |> ignore
            | Domain.VerificationEvidenceParseError.WrongFieldType (src, line, field, expected) ->
                sb.AppendLine(sprintf "  wrong_field_type: %s:%d %s expected %s" src line field expected) |> ignore
    sb.ToString()

let runInventory (repoRoot: string) : int =
    let result = Engine.runEpisodeEngine repoRoot Engine.defaultEngineOptions
    
    // Check for evidence load errors first (fail-closed)
    if not (List.isEmpty result.EvidenceLoadErrors) then
        stderr.WriteLine(renderEvidenceLoadErrors result.EvidenceLoadErrors)
        ExitCode.policyFailure
    else
        stdout.WriteLine(renderInventoryHuman result)
        if result.Summary.InvalidDeclarations > 0
           || result.Summary.MissingCaptures > 0
           || result.Summary.MissingGitObjects > 0
           || result.Summary.DuplicateEpisodeKeys > 0
           || result.Summary.DuplicateEpisodeIds > 0
           || not result.Outcome then
            ExitCode.policyFailure
        else
            ExitCode.pass

let runRegenerate (repoRoot: string) : int =
    let result = Engine.runEpisodeEngine repoRoot Engine.defaultEngineOptions
    
    // Check for evidence load errors first (fail-closed)
    if not (List.isEmpty result.EvidenceLoadErrors) then
        stderr.WriteLine(renderEvidenceLoadErrors result.EvidenceLoadErrors)
        ExitCode.policyFailure
    elif not result.Outcome then
        stderr.WriteLine "error: atomic publication failed"
        ExitCode.operationalError
    else
        stdout.WriteLine
            (sprintf
                "fsharp-diagnostics repair-episodes regenerate: episodes=%d transitions=%d change_sets=%d"
                result.RepairEpisodes.Length
                result.Transitions.Length
                result.ChangeSets.Length)
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
    
    // Check for evidence load errors first (fail-closed)
    if not (List.isEmpty result.EvidenceLoadErrors) then
        stderr.WriteLine(renderEvidenceLoadErrors result.EvidenceLoadErrors)
        ExitCode.policyFailure
    else
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
