module Circus.Tooling.ProtectedScope.Cli

// =============================================================================
// Protected-scope authority – CLI dispatcher
//
// ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01-CORRECTION01
//
// The ``protected-scope check`` verb takes an ACT-scope declaration
// JSON file and a baseline commit OID, runs ``git diff --name-only
// <baseline>..HEAD`` to obtain the changed paths, and categorises
// every change against the declaration's ``globally_protected`` and
// ``act_owned`` lists.
//
// Exit codes:
//
//   0 - all changes are in act_owned (or there are no changes)
//   1 - changes detected in globally_protected OR undeclared
//   2 - operational failure (file missing, git failure, etc.)
//
// Verb:
//
//   circus-tooling protected-scope check \
//     --repo-root <path> \
//     --declaration <path> \
//     --baseline-commit <oid>
//
// The verb is wired to the existing top-level CLI dispatcher in
// ``Program.fs``.
// =============================================================================

open System
open System.IO

open Circus.Tooling.ProtectedScope.Domain
open Circus.Tooling.ProtectedScope.Check
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Git

module ExitCode =
    let pass = 0
    let policyFailure = 1
    let operationalError = 2

type Command =
    | CheckCmd of repoRoot: string * declaration: string * baseline: string option
    | HelpCmd

let helpText () : string =
    "protected-scope — ACT-scope authority reconciliation check\n"
    + "\n"
    + "Usage:\n"
    + "  circus-tooling protected-scope check \\\n"
    + "    --repo-root <path> \\\n"
    + "    --declaration <path> \\\n"
    + "    --baseline-commit <oid>\n"
    + "  circus-tooling protected-scope help\n"

let private consumeFlag (flag: string) (args: string list) : Result<string * string list, string> =
    match args with
    | v :: rest -> Ok (v, rest)
    | _ -> Error (sprintf "missing value for %s" flag)

let private parse (argv: string list) : Result<Command, string> =
    match argv with
    | [] | [ "help" ] | [ "-h" ] | [ "--help" ] -> Ok HelpCmd
    | "check" :: rest ->
        let mutable repoRoot : string option = None
        let mutable declaration : string option = None
        let mutable baseline : string option = None
        let mutable remaining = rest
        let mutable bad = false
        while not bad && not (List.isEmpty remaining) do
            match remaining with
            | "--repo-root" :: t ->
                match consumeFlag "--repo-root" t with
                | Ok (v, r) -> repoRoot <- Some v; remaining <- r
                | Error e -> bad <- true; stderr.WriteLine("error: " + e)
            | "--declaration" :: t ->
                match consumeFlag "--declaration" t with
                | Ok (v, r) -> declaration <- Some v; remaining <- r
                | Error e -> bad <- true; stderr.WriteLine("error: " + e)
            | "--baseline-commit" :: t ->
                match consumeFlag "--baseline-commit" t with
                | Ok (v, r) -> baseline <- Some v; remaining <- r
                | Error e -> bad <- true; stderr.WriteLine("error: " + e)
            | unknown :: _ ->
                bad <- true
                stderr.WriteLine(sprintf "error: unrecognised argument: %s" unknown)
            | [] ->
                bad <- true
        if bad then Error "argument parse failed"
        else
            match repoRoot, declaration with
            | Some r, Some d -> Ok(CheckCmd (r, d, baseline))
            | _ -> Error "check requires --repo-root and --declaration (--baseline-commit is optional and defaults to the declaration's baseline)"
    | _ ->
        Error "usage: protected-scope {check|help}"

// -----------------------------------------------------------------------------
// Git integration
// -----------------------------------------------------------------------------

let private listChangedPaths (repoRoot: string) (baseline: string) : Result<string list, string> =
    match runGitTyped repoRoot defaultGitRunOptions [ "diff"; "--name-only"; baseline + "..HEAD" ] with
    | Error err -> Error(sprintf "git diff failed: %A" err)
    | Ok run ->
        if run.ExitCode <> 0 then
            Error(sprintf "git diff exit %d: %s" run.ExitCode run.Stderr)
        else
            let paths =
                run.Stdout.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.toList
            Ok paths

// -----------------------------------------------------------------------------
// Outcome rendering
// -----------------------------------------------------------------------------

let private renderOutcome (outcome: ScopeCheckOutcome) : int =
    if isPathAuthorized outcome then
        stdout.WriteLine(
            sprintf
                "protected-scope: PASS act_id=%s baseline=%s globally_protected_changes=%d act_owned_changes=%d undeclared_changes=%d"
                outcome.ActId
                (if outcome.BaselineCommitOid.Length >= 12 then outcome.BaselineCommitOid.Substring(0, 12) else outcome.BaselineCommitOid)
                (List.length outcome.GloballyProtectedChanges)
                (List.length outcome.ActOwnedChanges)
                (List.length outcome.UndeclaredChanges)
        )
        ExitCode.pass
    else
        stderr.WriteLine(
            sprintf
                "protected-scope: FAIL act_id=%s baseline=%s globally_protected_changes=%d act_owned_changes=%d undeclared_changes=%d"
                outcome.ActId
                (if outcome.BaselineCommitOid.Length >= 12 then outcome.BaselineCommitOid.Substring(0, 12) else outcome.BaselineCommitOid)
                (List.length outcome.GloballyProtectedChanges)
                (List.length outcome.ActOwnedChanges)
                (List.length outcome.UndeclaredChanges)
        )
        for p in outcome.GloballyProtectedChanges do
            stderr.WriteLine("  GLOBALLY_PROTECTED: " + p)
        for p in outcome.UndeclaredChanges do
            stderr.WriteLine("  UNDECLARED: " + p)
        ExitCode.policyFailure

// -----------------------------------------------------------------------------
// Runners
// -----------------------------------------------------------------------------

let runCheck (repoRoot: string) (declarationPath: string) (baseline: string option) : int =
    let declAbs =
        if Path.IsPathRooted declarationPath then declarationPath
        else Path.GetFullPath(Path.Combine(repoRoot, declarationPath))
    match readDeclaration declAbs with
    | Error e ->
        stderr.WriteLine(sprintf "protected-scope: FAIL (%s)" (parseErrorToString e))
        ExitCode.operationalError
    | Ok declaration ->
        let effectiveBaseline =
            match baseline with
            | Some b -> b
            | None -> declaration.BaselineCommitOid
        match listChangedPaths repoRoot effectiveBaseline with
        | Error e ->
            stderr.WriteLine(sprintf "protected-scope: FAIL (%s)" e)
            ExitCode.operationalError
        | Ok changedPaths ->
            let outcome = categorize declaration changedPaths
            let outcome =
                { outcome with DeclarationPath = declAbs }
            renderOutcome outcome

let run (argv: string list) : int =
    match parse argv with
    | Ok HelpCmd ->
        stdout.WriteLine(helpText ())
        ExitCode.pass
    | Ok(CheckCmd (repoRoot, declaration, baseline)) ->
        runCheck repoRoot declaration baseline
    | Error msg ->
        stderr.WriteLine(sprintf "error: %s" msg)
        stderr.WriteLine(helpText ())
        ExitCode.operationalError
