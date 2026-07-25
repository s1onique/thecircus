module Circus.Tooling.EvidenceValidator.Cli

// =============================================================================
// Evidence validator – CLI dispatcher
//
// ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01-CORRECTION01
//
// The ``evidence-validate`` verb validates a per-ACT evidence file
// against two contracts:
//
//   1. Non-recursive identity: the file's ``tested_subject_commit_oid``
//      MUST NOT equal the OID of the commit that contains the file.
//      The containing commit is resolved by running
//      ``git log -1 --format=%H -- <path>`` through the bounded
//      Git adapter so the validator never spawns unrestricted
//      subprocesses.
//
//   2. Self-consistent payload hash: the
//      ``evidence_payload_sha256`` field MUST equal the SHA-256 of
//      the canonical JSON form where the documented placeholder is
//      substituted for the hash itself.
//
// Exit codes:
//   0 - pass
//   1 - policy failure (validation reported issues)
//   2 - operational failure (git adapter failed, file unreadable, etc.)
//
// Verb:
//
//   circus-tooling evidence-validate --repo-root <path> --path <path>
//
// The verb is wired to the existing top-level CLI dispatcher in
// ``Program.fs``.
// =============================================================================

open System
open System.IO

open Circus.Tooling.EvidenceValidator.Domain
open Circus.Tooling.EvidenceValidator.Validation
open Circus.Tooling.FSharpDiagnostics.Hashing
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Git

module ExitCode =
    let pass = 0
    let policyFailure = 1
    let operationalError = 2

type Command =
    | ValidateCmd of repoRoot: string * path: string
    | HashCmd of path: string
    | HelpCmd

let helpText () : string =
    "evidence-validate — per-ACT evidence file validator\n"
    + "\n"
    + "Usage:\n"
    + "  circus-tooling evidence-validate validate \\\n"
    + "    --repo-root <path> --path <evidence-file>\n"
    + "  circus-tooling evidence-validate hash --path <evidence-file>\n"
    + "  circus-tooling evidence-validate help\n"
    + "\n"
    + "validate: validates that the evidence file at --path does not\n"
    + "claim its own containing commit AND that its\n"
    + "evidence_payload_sha256 field matches the SHA-256 of the\n"
    + "canonical JSON form (with the placeholder substituted for the\n"
    + "hash itself).\n"
    + "hash: prints the canonical evidence_payload_sha256 value for\n"
    + "the file at --path (placeholder substituted into the body).\n"
    + "Use this to update the file's evidence_payload_sha256 field.\n"

let private consumeFlag (flag: string) (args: string list) : Result<string * string list, string> =
    match args with
    | v :: rest -> Ok (v, rest)
    | _ -> Error (sprintf "missing value for %s" flag)

let private parse (argv: string list) : Result<Command, string> =
    match argv with
    | [] | [ "help" ] | [ "-h" ] | [ "--help" ] -> Ok HelpCmd
    | [ "validate" ] ->
        Error "validate requires --repo-root and --path"
    | "validate" :: rest ->
        let mutable repoRoot : string option = None
        let mutable pathArg : string option = None
        let mutable remaining = rest
        let mutable bad = false
        while not bad && not (List.isEmpty remaining) do
            match remaining with
            | "--repo-root" :: t ->
                match consumeFlag "--repo-root" t with
                | Ok (v, r) -> repoRoot <- Some v; remaining <- r
                | Error e -> bad <- true; stderr.WriteLine("error: " + e)
            | "--path" :: t ->
                match consumeFlag "--path" t with
                | Ok (v, r) -> pathArg <- Some v; remaining <- r
                | Error e -> bad <- true; stderr.WriteLine("error: " + e)
            | unknown :: _ ->
                bad <- true
                stderr.WriteLine(sprintf "error: unrecognised argument: %s" unknown)
            | [] ->
                bad <- true
        if bad then Error "argument parse failed"
        else
            match repoRoot, pathArg with
            | Some r, Some p -> Ok(ValidateCmd (r, p))
            | _ -> Error "validate requires --repo-root and --path"
    | "hash" :: rest ->
        let mutable pathArg : string option = None
        let mutable remaining = rest
        let mutable bad = false
        while not bad && not (List.isEmpty remaining) do
            match remaining with
            | "--path" :: t ->
                match consumeFlag "--path" t with
                | Ok (v, r) -> pathArg <- Some v; remaining <- r
                | Error e -> bad <- true; stderr.WriteLine("error: " + e)
            | unknown :: _ ->
                bad <- true
                stderr.WriteLine(sprintf "error: unrecognised argument: %s" unknown)
            | [] ->
                bad <- true
        if bad then Error "argument parse failed"
        else
            match pathArg with
            | Some p -> Ok(HashCmd p)
            | _ -> Error "hash requires --path"
    | _ ->
        Error "usage: evidence-validate {validate|hash|help}"

// -----------------------------------------------------------------------------
// Containing commit resolution
// -----------------------------------------------------------------------------

let private resolveContainingCommit (repoRoot: string) (path: string) : Result<string, string> =
    match runGitTyped repoRoot defaultGitRunOptions [ "log"; "-1"; "--format=%H"; "--"; path ] with
    | Error err -> Error(sprintf "git log failed: %A" err)
    | Ok run ->
        if run.ExitCode <> 0 then
            Error(sprintf "git log exit %d: %s" run.ExitCode run.Stderr)
        else
            let oid = run.Stdout.Trim()
            if String.IsNullOrEmpty oid then
                Error "git log returned empty OID (path not in any commit)"
            else
                Ok oid

// -----------------------------------------------------------------------------
// Result rendering
// -----------------------------------------------------------------------------

let private issueToString (i: Issue) (path: string) : string =
    match i with
    | FileMissing p -> sprintf "%s: file missing: %s" path p
    | NotJsonObject p -> sprintf "%s: not a JSON object: %s" path p
    | MissingField (p, field) -> sprintf "%s: missing required field: %s" p field
    | FieldNotString (p, field) -> sprintf "%s: field %s is not a string" p field
    | PayloadHashFieldMissing p -> sprintf "%s: missing evidence_payload_sha256 field" p
    | PayloadHashFieldNotString p -> sprintf "%s: evidence_payload_sha256 is not a string" p
    | PlaceholderFieldMissing p -> sprintf "%s: missing evidence_payload_sha256_input_placeholder field" p
    | PlaceholderFieldNotString p -> sprintf "%s: evidence_payload_sha256_input_placeholder is not a string" p
    | PlaceholderWrongWidth (p, actual) ->
        sprintf "%s: placeholder width %d is not 64" p actual
    | PayloadHashMismatch (p, expected, actual) ->
        sprintf "%s: payload hash mismatch: expected=%s actual=%s" p expected actual
    | SelfReferentialIdentity (p, field, claimed, c) ->
        sprintf "%s: SELF-REFERENTIAL IDENTITY: field %s claims %s which equals the file's containing commit %s" p field claimed c
    | ManifestLineCountWrong (p, expected, actual) ->
        sprintf "%s: manifest line count wrong: expected=%d actual=%d" p expected actual
    | ManifestHashMismatch (p, filename, expected, actual) ->
        sprintf "%s: manifest hash mismatch for %s: expected=%s actual=%s" p filename expected actual

// -----------------------------------------------------------------------------
// Runners
// -----------------------------------------------------------------------------

let runValidate (repoRoot: string) (path: string) : int =
    let absPath =
        if Path.IsPathRooted path then path
        else Path.GetFullPath(Path.Combine(repoRoot, path))
    let containingOid =
        match resolveContainingCommit repoRoot path with
        | Ok oid -> Some oid
        | Error _ -> None
    let result = validatePath absPath containingOid
    let status = if List.isEmpty result.Issues then "PASS" else "FAIL"
    let subject =
        match result.Snapshot with
        | Some s -> defaultArg s.SubjectCommitOid "<missing>"
        | None -> "<unreadable>"
    let computed =
        defaultArg result.ComputedPayloadHash "<none>"
    stdout.WriteLine(
        sprintf
            "evidence-validate: %s path=%s subject=%s containing=%s computed_payload_sha256=%s"
            status
            result.Path
            (if subject.Length >= 12 then subject.Substring(0, 12) else subject)
            (match containingOid with
             | Some c when c.Length >= 12 -> c.Substring(0, 12)
             | Some c -> c
             | None -> "<unresolved>")
            (if computed.Length >= 12 then computed.Substring(0, 12) else computed)
    )
    if not (List.isEmpty result.Issues) then
        for issue in result.Issues do
            stderr.WriteLine("  " + issueToString issue result.Path)
        ExitCode.policyFailure
    else
        ExitCode.pass

let runHash (path: string) : int =
    if not (File.Exists path) then
        stderr.WriteLine(sprintf "error: file not found: %s" path)
        ExitCode.operationalError
    else
        let raw = File.ReadAllText path
        let absPath =
            if Path.IsPathRooted path then path
            else Path.GetFullPath path
        let containing = None
        let result = validatePath absPath containing
        match result.ComputedPayloadHash with
        | Some h ->
            stdout.WriteLine h
            ExitCode.pass
        | None ->
            stderr.WriteLine("error: cannot compute hash (missing fields or placeholder); see validate output")
            ExitCode.operationalError

let run (argv: string list) : int =
    match parse argv with
    | Ok HelpCmd ->
        stdout.WriteLine(helpText ())
        ExitCode.pass
    | Ok(ValidateCmd (repoRoot, path)) ->
        runValidate repoRoot path
    | Ok(HashCmd path) ->
        runHash path
    | Error msg ->
        stderr.WriteLine(sprintf "error: %s" msg)
        stderr.WriteLine(helpText ())
        ExitCode.operationalError
