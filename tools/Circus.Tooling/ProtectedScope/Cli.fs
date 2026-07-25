module Circus.Tooling.ProtectedScope.Cli

// ``protected-scope check`` is a consumer of ScopeAuthority.  It never reads a
// declaration directly and never trusts a CLI declaration in place of the
// tracked pointer.  All comparisons are against committed objects at the
// evaluated commit.

open System

open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Git
open Circus.Tooling.ScopeAuthority.Domain
open Circus.Tooling.ScopeAuthority.Authority
open Circus.Tooling.ProtectedScope.Domain
open Circus.Tooling.ProtectedScope.Check

module ExitCode =
    let pass = 0
    let policyFailure = 1
    let operationalError = 2

type Command =
    | CheckCmd of
        repoRoot: string *
        declaration: string option *
        baseline: string option *
        evaluatedCommit: string option
    | HelpCmd

let helpText () =
    "protected-scope -- strict active ACT ownership check\n"
    + "\n"
    + "Usage:\n"
    + "  circus-tooling protected-scope check \\\n"
    + "    --repo-root <path> [--declaration <repo-relative-path>] \\\n"
    + "    [--baseline-commit <full-oid>] [--evaluated-commit <full-oid>]\n"

let private consume flag args =
    match args with
    | value :: rest -> Ok(value, rest)
    | [] -> Error(sprintf "missing value for %s" flag)

let private parse argv =
    match argv with
    | []
    | [ "help" ]
    | [ "-h" ]
    | [ "--help" ] -> Ok HelpCmd
    | "check" :: tail ->
        let mutable repoRoot = None
        let mutable declaration = None
        let mutable baseline = None
        let mutable evaluated = None
        let mutable rest = tail
        let mutable failure = None

        let assign flag setter values =
            match consume flag values with
            | Ok(value, remaining) ->
                setter value
                rest <- remaining
            | Error detail -> failure <- Some detail

        while failure.IsNone && not (List.isEmpty rest) do
            match rest with
            | "--repo-root" :: values -> assign "--repo-root" (fun value -> repoRoot <- Some value) values
            | "--declaration" :: values -> assign "--declaration" (fun value -> declaration <- Some value) values
            | "--baseline-commit" :: values -> assign "--baseline-commit" (fun value -> baseline <- Some value) values
            | "--evaluated-commit" :: values -> assign "--evaluated-commit" (fun value -> evaluated <- Some value) values
            | unknown :: _ -> failure <- Some(sprintf "unrecognised argument: %s" unknown)
            | [] -> ()

        match failure, repoRoot with
        | Some detail, _ -> Error detail
        | None, None -> Error "check requires --repo-root"
        | None, Some root -> Ok(CheckCmd(root, declaration, baseline, evaluated))
    | _ -> Error "usage: protected-scope {check|help}"

let private changedPaths repoRoot baseline evaluated =
    match
        runGitTyped
            repoRoot
            defaultGitRunOptions
            [ "diff"
              "--name-only"
              "--no-renames"
              "-z"
              baseline + ".." + evaluated ]
    with
    | Error error -> Error(sprintf "bounded Git diff failed: %A" error)
    | Ok result when result.ExitCode <> 0 ->
        Error(sprintf "Git diff exit=%d stderr=%s" result.ExitCode result.Stderr)
    | Ok result ->
        let paths =
            result.Stdout.Split([| '\u0000' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.toList

        match
            paths
            |> List.tryPick (fun path ->
                match validateRepositoryPath false path with
                | Ok() -> None
                | Error detail -> Some(sprintf "Git diff emitted invalid path %s: %s" path detail))
        with
        | Some detail -> Error detail
        | None -> Ok paths

let private prefix (value: string) =
    if value.Length > 12 then value.Substring(0, 12) else value

let private render outcome =
    if isPathAuthorized outcome then
        stdout.WriteLine(
            sprintf
                "protected-scope: PASS act_id=%s commit=%s baseline=%s pointer_blob=%s declaration_blob=%s globally_protected_changes=%d act_owned_changes=%d undeclared_changes=%d"
                outcome.ActId
                (prefix outcome.EvaluatedCommitOid)
                (prefix outcome.BaselineCommitOid)
                outcome.PointerBlobOid
                outcome.DeclarationBlobOid
                outcome.GloballyProtectedChanges.Length
                outcome.ActOwnedChanges.Length
                outcome.UndeclaredChanges.Length
        )

        ExitCode.pass
    else
        stderr.WriteLine(
            sprintf
                "protected-scope: FAIL act_id=%s commit=%s globally_protected_changes=%d act_owned_changes=%d undeclared_changes=%d"
                outcome.ActId
                (prefix outcome.EvaluatedCommitOid)
                outcome.GloballyProtectedChanges.Length
                outcome.ActOwnedChanges.Length
                outcome.UndeclaredChanges.Length
        )

        for path in outcome.GloballyProtectedChanges do
            stderr.WriteLine("  GLOBALLY_PROTECTED: " + path)

        for path in outcome.UndeclaredChanges do
            stderr.WriteLine("  UNDECLARED: " + path)

        ExitCode.policyFailure

let runCheck repoRoot declaration baseline evaluatedCommit =
    let evaluatedResult =
        match evaluatedCommit with
        | Some oid -> Ok oid
        | None -> resolveCommitReference repoRoot "HEAD"

    match evaluatedResult with
    | Error error ->
        stderr.WriteLine("protected-scope: FAIL (" + errorToString error + ")")
        ExitCode.operationalError
    | Ok evaluated ->
        match resolve repoRoot evaluated declaration baseline with
        | Error error ->
            stderr.WriteLine("protected-scope: FAIL (" + errorToString error + ")")
            ExitCode.operationalError
        | Ok binding ->
            match changedPaths repoRoot binding.BaselineCommitOid binding.EvaluatedCommitOid with
            | Error detail ->
                stderr.WriteLine("protected-scope: FAIL (" + detail + ")")
                ExitCode.operationalError
            | Ok paths -> categorize binding paths |> render

let run argv =
    match parse argv with
    | Ok HelpCmd ->
        stdout.WriteLine(helpText ())
        ExitCode.pass
    | Ok(CheckCmd(repoRoot, declaration, baseline, evaluated)) ->
        runCheck repoRoot declaration baseline evaluated
    | Error detail ->
        stderr.WriteLine("error: " + detail)
        stderr.WriteLine(helpText ())
        ExitCode.operationalError
