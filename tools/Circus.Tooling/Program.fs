module Circus.Tooling.Program

open System

open Circus.Tooling.SourcePolicy.Cli

[<EntryPoint>]
let main (argv: string[]) : int =
    let args = argv |> Array.toList

    match parseTopLevel args with
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
            stdout.WriteLine "circus-tooling 1.0.0"
            ExitCode.pass
        | VerifyCmd _ ->
            match resolveRepoRoot () with
            | Error detail -> stderr.WriteLine detail; ExitCode.operationalError
            | Ok repoRoot -> runSourcePolicyVerify repoRoot
        | ContainerPolicyCmd _ ->
            match resolveRepoRoot () with
            | Error detail -> stderr.WriteLine detail; ExitCode.operationalError
            | Ok repoRoot -> runContainerPolicy repoRoot
        | GateSummaryRegenerateCmd ->
            match resolveRepoRoot () with
            | Error detail -> stderr.WriteLine detail; ExitCode.operationalError
            | Ok repoRoot -> runGateSummaryRegenerate repoRoot
        | GateSummaryVerifyCmd ->
            match resolveRepoRoot () with
            | Error detail -> stderr.WriteLine detail; ExitCode.operationalError
            | Ok repoRoot -> runGateSummaryVerify repoRoot
        | GateRunCmd ->
            match resolveRepoRoot () with
            | Error detail -> stderr.WriteLine detail; ExitCode.operationalError
            | Ok repoRoot -> runGate repoRoot
        | NoForcePushCmd subArgs ->
            Circus.Tooling.NoForcePush.Cli.run subArgs
