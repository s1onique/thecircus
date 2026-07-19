module Circus.Tooling.Program

open System

open Circus.Tooling.SourcePolicy.Cli

[<EntryPoint>]
let main (argv: string[]) : int =
    let args = argv |> Array.toList
    let subArgs =
        match args with
        | "source-policy" :: rest -> rest
        | _ -> args

    let emitText (_format: string) (text: string) : unit = stdout.WriteLine text

    match parse subArgs with
    | Error msg ->
        stderr.WriteLine("error: " + msg)
        eprintfn "%s" (helpText ())
        ExitCode.operationalError
    | Ok cmd ->
        match cmd with
        | HelpCmd ->
            stdout.WriteLine(helpText ())
            ExitCode.pass
        | VersionCmd ->
            stdout.WriteLine "circus-tooling source-policy 1.0.0"
            ExitCode.pass
        | VerifyCmd fmt ->
            let f = fmt
            match resolveRepoRoot () with
            | Error detail -> emitText f detail; ExitCode.operationalError
            | Ok repoRoot -> runVerify f repoRoot
        | InventoryCmd fmt ->
            let f = fmt
            match resolveRepoRoot () with
            | Error detail -> emitText f detail; ExitCode.operationalError
            | Ok repoRoot -> runInventory f repoRoot
        | ExplainCmd (_path, _ignored) ->
            stdout.WriteLine "explain: not implemented"
            ExitCode.pass
