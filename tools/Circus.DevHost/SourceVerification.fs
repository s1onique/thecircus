module Circus.DevHost.SourceVerification

open System
open System.IO

open Domain
open Circus.DevHost.Adapters
open Circus.DevHost.ProcessRunner

/// Run a `dotnet` command in the repository.
let private runDotnetCommand
    (runner: IProcessRunner)
    (dotnetRoot: string)
    (args: string list)
    (cwd: string)
    (timeout: TimeSpan)
    : Result<string, DevHostFailure> =
    let dotnet = Path.Combine(dotnetRoot, "dotnet")
    if not (File.Exists dotnet) then Error(MissingTool DotNetSdk)
    else
        let env = Map.ofList [ "DOTNET_ROOT", dotnetRoot ]
        let spec = mkSpec dotnet args cwd env timeout None
        match runSync runner spec with
        | Error e -> Error e
        | Ok r -> Ok (r.StandardOutput + r.StandardError)

/// Run `dotnet restore --locked-mode Circus.sln`.
let verifyRestore
    (runner: IProcessRunner)
    (dotnetRoot: string)
    (repoRoot: string)
    : Result<unit, DevHostFailure> =
    match runDotnetCommand runner dotnetRoot
        [ "restore"; "Circus.sln"; "--locked-mode" ]
        repoRoot (TimeSpan.FromMinutes(10.0)) with
    | Ok _ -> Ok ()
    | Error e -> Error e

/// Run `dotnet build Circus.sln -c Release --no-restore`.
let verifyBuild
    (runner: IProcessRunner)
    (dotnetRoot: string)
    (repoRoot: string)
    : Result<unit, DevHostFailure> =
    match runDotnetCommand runner dotnetRoot
        [ "build"; "Circus.sln"; "-c"; "Release"; "--no-restore" ]
        repoRoot (TimeSpan.FromMinutes(15.0)) with
    | Ok _ -> Ok ()
    | Error e -> Error e

/// Top-level source verification used by `circus-dev verify source`.
let verifySource
    (runner: IProcessRunner)
    (dotnetRoot: string)
    (repoRoot: string)
    : Result<unit, DevHostFailure> =
    if not (File.Exists(Path.Combine(repoRoot, "Circus.sln"))) then
        Error(MalformedAuthorityFile "Circus.sln")
    else
        match verifyRestore runner dotnetRoot repoRoot with
        | Error e -> Error e
        | Ok () -> verifyBuild runner dotnetRoot repoRoot

/// Discovery for required Makefile targets.
type MakeTargetExpectation = { Name: string; Required: bool }

let requiredMakeTargets : MakeTargetExpectation list = [
    { Name = "build-backend"; Required = true }
    { Name = "test-backend"; Required = true }
    { Name = "build-frontend"; Required = true }
]

/// Parse the `Makefile` and decide whether each named target exists.
let makefileHasTargets (text: string) (targets: string list) : (string * bool) list =
    targets
    |> List.map (fun t ->
        let pattern = "^" + System.Text.RegularExpressions.Regex.Escape(t) + ":"
        let found =
            text.Split([| '\n' |])
            |> Array.exists (fun line -> System.Text.RegularExpressions.Regex.IsMatch(line.TrimStart(), pattern))
        t, found)
