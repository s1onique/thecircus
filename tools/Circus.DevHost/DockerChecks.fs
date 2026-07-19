module Circus.DevHost.DockerChecks

open System
open System.IO

open Domain
open Circus.DevHost.Adapters
open Circus.DevHost.Paths
open Circus.DevHost.ProcessRunner

let private pathValue () =
    Environment.GetEnvironmentVariable "PATH"
    |> fun value -> if String.IsNullOrEmpty value then None else Some value

let private dockerFallbacks = [ "/usr/bin/docker"; "/usr/local/bin/docker" ]

let private dockerExecutable () =
    locateInPath File.Exists (pathValue ()) dockerFallbacks "docker"
    |> Option.defaultValue "docker"

let private runExecutable
    (runner: IProcessRunner)
    (executable: string)
    (arguments: string list)
    (timeout: TimeSpan)
    : Result<string, DevHostFailure> =
    mkSpec executable arguments (Directory.GetCurrentDirectory()) Map.empty timeout None
    |> runSync runner
    |> Result.map (fun result -> result.StandardOutput)

let private runDocker
    (runner: IProcessRunner)
    (arguments: string list)
    (timeout: TimeSpan)
    : Result<string, DevHostFailure> =
    runExecutable runner (dockerExecutable ()) arguments timeout

let checkDockerBinary (fs: IFilesystem) : Result<string, DevHostFailure> =
    match locateInPath fs.IsFile (pathValue ()) dockerFallbacks "docker" with
    | Some path -> Ok path
    | None -> Error(MissingTool Docker)

let checkDirectDaemonAccess (runner: IProcessRunner) : Result<string, DevHostFailure> =
    match runDocker runner [ "info" ] (TimeSpan.FromSeconds 30.0) with
    | Ok output -> Ok output
    | Error _ -> Error DockerPermissionDenied

let checkBuildx (runner: IProcessRunner) : Result<string, DevHostFailure> =
    runDocker runner [ "buildx"; "version" ] (TimeSpan.FromSeconds 30.0)

let checkCompose (runner: IProcessRunner) : Result<string, DevHostFailure> =
    match runDocker runner [ "compose"; "version" ] (TimeSpan.FromSeconds 30.0) with
    | Ok output -> Ok output
    | Error _ ->
        let standaloneFallbacks =
            [ "/usr/bin/docker-compose"; "/usr/local/bin/docker-compose" ]

        let executable =
            locateInPath File.Exists (pathValue ()) standaloneFallbacks "docker-compose"
            |> Option.defaultValue "docker-compose"

        runExecutable runner executable [ "version" ] (TimeSpan.FromSeconds 30.0)
