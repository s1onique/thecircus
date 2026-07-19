module Circus.DevHost.Cli

open System
open System.IO

open Domain

/// The full surface of commands supported by `circus-dev`.
type Command =
    | Version
    | Check
    | Bootstrap of force:bool * dryRun:bool
    | Doctor of json:bool * allowDirty:bool
    | Env of Shell option
    | InstallShellHook of Shell option
    | Verify of VerifyKind
    | Help

and VerifyKind =
    | VerifySource
    | VerifyDocker
    | VerifyGate
    | VerifyAll

/// Built-in help text used by `circus-dev help`.
let helpText () : string =
    "circus-dev — Circus Linux development-host CLI\n" +
    "\n" +
    "Usage:\n" +
    "  circus-dev version\n" +
    "  circus-dev check\n" +
    "  circus-dev bootstrap [--force] [--dry-run]\n" +
    "  circus-dev doctor [--json] [--allow-dirty]\n" +
    "  circus-dev env [--shell bash|zsh]\n" +
    "  circus-dev install-shell-hook [--shell bash|zsh]\n" +
    "  circus-dev verify source|docker|gate|all\n" +
    "  circus-dev help\n"

/// Parse the argv list and classify it. Pure. Never throws.
let parse (argv: string list) : Result<Command, string> =
    let error msg = Error msg
    let shellOf name =
        match name with
        | "bash" -> Some Bash
        | "zsh" -> Some Zsh
        | _ -> None
    let verifyOf name =
        match name with
        | "source" -> Some VerifySource
        | "docker" -> Some VerifyDocker
        | "gate" -> Some VerifyGate
        | "all" -> Some VerifyAll
        | _ -> None
    match argv with
    | [] -> Ok Help
    | [ "version" ] -> Ok Version
    | [ "check" ] -> Ok Check
    | [ "bootstrap" ] -> Ok Bootstrap (false, false)
    | [ "bootstrap"; "--force" ] -> Ok Bootstrap (true, false)
    | [ "bootstrap"; "--dry-run" ] -> Ok Bootstrap (false, true)
    | [ "bootstrap"; "--force"; "--dry-run" ] -> Ok Bootstrap (true, true)
    | [ "bootstrap"; "--dry-run"; "--force" ] -> Ok Bootstrap (true, true)
    | [ "doctor" ] -> Ok Doctor (false, false)
    | [ "doctor"; "--json" ] -> Ok Doctor (true, false)
    | [ "doctor"; "--allow-dirty" ] -> Ok Doctor (false, true)
    | [ "doctor"; "--json"; "--allow-dirty" ] -> Ok Doctor (true, true)
    | [ "doctor"; "--allow-dirty"; "--json" ] -> Ok Doctor (true, true)
    | [ "env" ] -> Ok Env None
    | [ "env"; "--shell"; name ] ->
        match shellOf name with
        | Some s -> Ok (Env (Some s))
        | None -> error ("unsupported shell: " + name)
    | [ "install-shell-hook" ] -> Ok InstallShellHook None
    | [ "install-shell-hook"; "--shell"; name ] ->
        match shellOf name with
        | Some s -> Ok (InstallShellHook (Some s))
        | None -> error ("unsupported shell: " + name)
    | [ "verify"; target ] ->
        match verifyOf target with
        | Some k -> Ok (Verify k)
        | None -> error ("unknown verify target: " + target)
    | [ "help" ] | [ "-h" ] | [ "--help" ] -> Ok Help
    | args -> error ("unknown command: " + String.concat " " args)

/// Classify the parsed command for tests. Currently always Success
/// because parsing has already split the input.
let classify (cmd: Command) : ExitClass = Success
