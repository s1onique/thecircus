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
    | []
    | [ "help" ]
    | [ "-h" ]
    | [ "--help" ] -> Ok HelpCmd
    | [ "inventory" ] -> Ok InventoryCmd
    | [ "regenerate" ] -> Ok RegenerateCmd
    | [ "verify" ] -> Ok VerifyCmd
    | [ "show"; id ] -> Ok(ShowCmd id)
    | [ "show" ] -> Result.Error "show requires an episode-id argument"
    | _ ->
        Result.Error
            "usage: circus-tooling fsharp-diagnostics repair-episodes {inventory|regenerate|verify|show <episode-id>|help}"

let private renderInventoryHuman (result: EpisodeEngineResult) : string =
    let s = result.Summary
    let sb = System.Text.StringBuilder()
    let append (line: string) = sb.AppendLine(line) |> ignore
    append "fsharp-diagnostics repair-episodes inventory"
    append (sprintf "  declarations_total: %d" s.DeclarationsTotal)
    append (sprintf "  valid_declarations: %d" s.ValidDeclarations)
    append (sprintf "  invalid_declarations: %d" s.InvalidDeclarations)
    append (sprintf "  missing_captures: %d" s.MissingCaptures)
    append (sprintf "  missing_git_objects: %d" s.MissingGitObjects)
    append (sprintf "  duplicate_episode_keys: %d" s.DuplicateEpisodeKeys)
    append (sprintf "  duplicate_episode_ids: %d" s.DuplicateEpisodeIds)
    append (sprintf "  episodes_total: %d" s.EpisodesTotal)
    append (sprintf "  episodes_qualified: %d" s.EpisodesQualified)
    append (sprintf "  change_sets_total: %d" s.ChangeSetsTotal)
    append (sprintf "  transitions_total: %d" s.TransitionsTotal)
    ignore (sb.ToString())
    sb.ToString()

/// Render evidence load errors in human-readable form for CLI output.
let private renderEvidenceLoadErrors (errors: VerificationEvidenceLoadError list) : string =
    let sb = System.Text.StringBuilder()
    sb.AppendLine("error: verification evidence loading failed") |> ignore

    for err in errors do
        match err with
        | VerificationEvidenceLoadError.EvidenceFileMissing path ->
            sb.AppendLine(sprintf "  evidence_file_missing: %s" path) |> ignore
        | VerificationEvidenceLoadError.EvidenceFileUnreadable(path, msg) ->
            sb.AppendLine(sprintf "  evidence_file_unreadable: %s (%s)" path msg) |> ignore
        | VerificationEvidenceLoadError.DuplicateEvidenceId(path, evid, l1, l2) ->
            sb.AppendLine(sprintf "  duplicate_evidence_id: %s %s (lines %d, %d)" path evid l1 l2)
            |> ignore
        | VerificationEvidenceLoadError.ConflictingEvidenceRecord(path, evid, l1, l2) ->
            sb.AppendLine(sprintf "  conflicting_evidence: %s %s (lines %d, %d)" path evid l1 l2)
            |> ignore
        | VerificationEvidenceLoadError.UnsupportedEvidenceSchemaVersion(path, ver) ->
            sb.AppendLine(sprintf "  unsupported_evidence_schema: %s %s" path ver) |> ignore
        | VerificationEvidenceLoadError.ParseError parseErr ->
            match parseErr with
            | VerificationEvidenceParseError.MalformedJson(src, line, msg) ->
                sb.AppendLine(sprintf "  malformed_json: %s:%d %s" src line msg) |> ignore
            | VerificationEvidenceParseError.MissingField(src, line, field) ->
                sb.AppendLine(sprintf "  missing_field: %s:%d %s" src line field) |> ignore
            | VerificationEvidenceParseError.InvalidEvidenceId(src, line, evid) ->
                sb.AppendLine(sprintf "  invalid_evidence_id: %s:%d %s" src line evid) |> ignore
            | VerificationEvidenceParseError.UnsupportedSchemaVersion(src, line, ver) ->
                sb.AppendLine(sprintf "  unsupported_schema: %s:%d %s" src line ver) |> ignore
            | VerificationEvidenceParseError.UnknownVerificationKind(src, line, v) ->
                sb.AppendLine(sprintf "  unknown_verification_kind: %s:%d %s" src line v)
                |> ignore
            | VerificationEvidenceParseError.UnknownVerificationStatus(src, line, v) ->
                sb.AppendLine(sprintf "  unknown_verification_status: %s:%d %s" src line v)
                |> ignore
            | VerificationEvidenceParseError.InvalidExitCode(src, line, ec) ->
                sb.AppendLine(sprintf "  invalid_exit_code: %s:%d %s" src line ec) |> ignore
            | VerificationEvidenceParseError.InvalidCommitOid(src, line, field, oid) ->
                sb.AppendLine(sprintf "  invalid_commit_oid: %s:%d %s %s" src line field oid)
                |> ignore
            | VerificationEvidenceParseError.InvalidTreeOid(src, line, field, oid) ->
                sb.AppendLine(sprintf "  invalid_tree_oid: %s:%d %s %s" src line field oid)
                |> ignore
            | VerificationEvidenceParseError.InvalidSha256(src, line, field, hash) ->
                sb.AppendLine(sprintf "  invalid_sha256: %s:%d %s %s" src line field hash)
                |> ignore
            | VerificationEvidenceParseError.PlaceholderEvidenceId(src, line, evid) ->
                sb.AppendLine(sprintf "  placeholder_evidence_id: %s:%d %s" src line evid)
                |> ignore
            | VerificationEvidenceParseError.DuplicateRawProperty(src, line, propertyName, occurrenceCount) ->
                sb.AppendLine(sprintf "  duplicate_raw_property: %s:%d %s (occurrences %d)" src line propertyName occurrenceCount)
                |> ignore
            | VerificationEvidenceParseError.ExpectedObject(src, line) ->
                sb.AppendLine(sprintf "  expected_object: %s:%d" src line) |> ignore
            | VerificationEvidenceParseError.JsonException(src, line, msg) ->
                sb.AppendLine(sprintf "  json_exception: %s:%d %s" src line msg) |> ignore
            | VerificationEvidenceParseError.WrongFieldType(src, line, field, expected, actual) ->
                sb.AppendLine(sprintf "  wrong_field_type: %s:%d %s expected %s, got %s" src line field expected actual)
                |> ignore
            | VerificationEvidenceParseError.ConflictingSemanticFields(src, line, primary, alias, val1, val2) ->
                sb.AppendLine(
                    sprintf
                        "  conflicting_semantic_fields: %s:%d %s vs %s (values: %s vs %s)"
                        src
                        line
                        primary
                        alias
                        val1
                        val2
                )
                |> ignore
            | VerificationEvidenceParseError.DuplicateSemanticField(src, line, primary, alias) ->
                sb.AppendLine(sprintf "  duplicate_semantic_field: %s:%d %s and %s" src line primary alias)
                |> ignore

    sb.ToString()

/// Render engine failure in human-readable form for CLI output.
let private renderEngineFailure (failure: EpisodeEngineFailure) : string =
    let sb = System.Text.StringBuilder()

    match failure with
    | EpisodeEngineFailure.VerificationEvidenceLoadFailed errors ->
        sb.AppendLine("error: verification evidence loading failed") |> ignore
        sb.Append(renderEvidenceLoadErrors errors) |> ignore
    | EpisodeEngineFailure.DeclarationLoadFailed issues ->
        sb.AppendLine("error: declaration loading failed") |> ignore

        for issue in issues do
            sb.AppendLine(sprintf "  declaration_issue: %A" issue) |> ignore
    | EpisodeEngineFailure.PublicationFailed(canonical, msg) ->
        sb.AppendLine(sprintf "error: publication failed (canonical=%b): %s" canonical msg)
        |> ignore
    | EpisodeEngineFailure.InternalFailure(op, msg) ->
        sb.AppendLine(sprintf "error: internal engine failure in %s: %s" op msg)
        |> ignore
    | EpisodeEngineFailure.DuplicateInputIdentities dups ->
        sb.AppendLine("error: upstream duplicate input identities detected") |> ignore
        for d in dups do
            sb.AppendLine(
                sprintf
                    "  duplicate_input_identity: kind=%s identity=%s occurrence_indices=[%s]"
                    (episodeInputIdentityKindToken d.Kind)
                    d.Identity
                    (d.OccurrenceIndices |> List.map string |> String.concat "; ")
            )
            |> ignore

    sb.ToString()

/// Render verification evidence load issues in human-readable form for CLI output.
/// This handles the VerificationIssue discriminated union case specifically.
let renderVerificationEvidenceLoadIssues (errors: VerificationEvidenceLoadError list) : string =
    renderEvidenceLoadErrors errors

let runInventory (repoRoot: string) : int =
    match runEpisodeEngine repoRoot defaultEngineOptions with
    | EpisodeEngineExecution.Failed failure ->
        stderr.WriteLine(renderEngineFailure failure)
        ExitCode.policyFailure
    | EpisodeEngineExecution.Completed result ->
        stdout.WriteLine(renderInventoryHuman result)

        if
            result.Summary.InvalidDeclarations > 0
            || result.Summary.MissingCaptures > 0
            || result.Summary.MissingGitObjects > 0
            || result.Summary.DuplicateEpisodeKeys > 0
            || result.Summary.DuplicateEpisodeIds > 0
            || not result.Outcome
        then
            ExitCode.policyFailure
        else
            ExitCode.pass

let runRegenerate (repoRoot: string) : int =
    match runEpisodeEngine repoRoot defaultEngineOptions with
    | EpisodeEngineExecution.Failed failure ->
        stderr.WriteLine(renderEngineFailure failure)
        ExitCode.policyFailure
    | EpisodeEngineExecution.Completed result ->
        if not result.Outcome then
            stderr.WriteLine "error: atomic publication failed"
            ExitCode.operationalError
        else
            stdout.WriteLine(
                sprintf
                    "fsharp-diagnostics repair-episodes regenerate: episodes=%d transitions=%d change_sets=%d"
                    result.RepairEpisodes.Length
                    result.Transitions.Length
                    result.ChangeSets.Length
            )

            ExitCode.pass

let runVerify (repoRoot: string) : int =
    let vr = verifyPipeline repoRoot defaultEngineOptions
    let issueCount = List.length vr.Issues

    if issueCount > 0 then
        // Render exact issues to stderr for visibility
        for issue in vr.Issues do
            match issue with
            | VerificationIssue.VerificationEvidenceLoadFailed errors ->
                stderr.WriteLine(renderVerificationEvidenceLoadIssues errors)
            | VerificationIssue.EpisodeEngineFailed failure -> stderr.WriteLine(renderEngineFailure failure)
            | VerificationIssue.FileMissing path -> stderr.WriteLine(sprintf "error: canonical file missing: %s" path)
            | VerificationIssue.DeclarationInvalid count ->
                stderr.WriteLine(sprintf "error: %d invalid declarations" count)
            | VerificationIssue.HashMismatch path -> stderr.WriteLine(sprintf "error: hash mismatch in %s" path)
            | VerificationIssue.ManifestMissing path -> stderr.WriteLine(sprintf "error: manifest missing: %s" path)
            | VerificationIssue.SummaryMismatch -> stderr.WriteLine(sprintf "error: summary mismatch")
            | VerificationIssue.EpisodeIdMismatch -> stderr.WriteLine(sprintf "error: episode ID mismatch")
            | VerificationIssue.ChangeSetIdMismatch -> stderr.WriteLine(sprintf "error: change set ID mismatch")
            | VerificationIssue.TransitionCountMismatch -> stderr.WriteLine(sprintf "error: transition count mismatch")
            | VerificationIssue.TransitionEpisodeIdMismatch ->
                stderr.WriteLine(sprintf "error: transition episode ID mismatch")

        stdout.WriteLine(
            sprintf
                "fsharp-diagnostics repair-episodes verify: episodes_validated=%d transitions_validated=%d issues=%d"
                vr.RepairEpisodesValidated
                vr.TransitionsValidated
                issueCount
        )

        ExitCode.policyFailure
    else
        stdout.WriteLine(
            sprintf
                "fsharp-diagnostics repair-episodes verify: episodes_validated=%d transitions_validated=%d issues=0"
                vr.RepairEpisodesValidated
                vr.TransitionsValidated
        )

        ExitCode.pass

let runShow (repoRoot: string) (episodeId: string) : int =
    match runEpisodeEngine repoRoot defaultEngineOptions with
    | EpisodeEngineExecution.Failed failure ->
        stderr.WriteLine(renderEngineFailure failure)
        ExitCode.policyFailure
    | EpisodeEngineExecution.Completed result ->
        match result.RepairEpisodes |> List.tryFind (fun e -> e.EpisodeId = episodeId) with
        | Some episode ->
            let sb = System.Text.StringBuilder()
            sb.AppendLine("fsharp-diagnostics repair-episodes show") |> ignore
            sb.AppendLine(sprintf "  episode_id: %s" episode.EpisodeId) |> ignore
            sb.AppendLine(sprintf "  episode_key: %s" episode.EpisodeKey) |> ignore

            sb.AppendLine(sprintf "  before_capture_id: %s" episode.BeforeCaptureId)
            |> ignore

            sb.AppendLine(sprintf "  after_capture_id: %s" episode.AfterCaptureId) |> ignore
            sb.AppendLine(sprintf "  before_tree_oid: %s" episode.BeforeTreeOid) |> ignore
            sb.AppendLine(sprintf "  after_tree_oid: %s" episode.AfterTreeOid) |> ignore
            sb.AppendLine(sprintf "  change_set_id: %s" episode.ChangeSetId) |> ignore

            sb.AppendLine(sprintf "  verification_level: %s" (verificationLevelToken episode.VerificationLevel))
            |> ignore

            sb.AppendLine(sprintf "  qualification: %s" (episodeQualificationStatusToken episode.Qualification.Status))
            |> ignore

            stdout.WriteLine(sb.ToString())
            ExitCode.pass
        | None ->
            stderr.WriteLine(sprintf "error: episode %s not found" episodeId)
            ExitCode.operationalError

let run (argv: string list) : int =
    match parse argv with
    | Ok HelpCmd ->
        stdout.WriteLine(helpText ())
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
        stderr.WriteLine(helpText ())
        ExitCode.operationalError
