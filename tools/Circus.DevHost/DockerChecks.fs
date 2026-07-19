module Circus.DevHost.DockerChecks

open System
open System.IO

open Domain
open Circus.DevHost.Adapters
open Circus.DevHost.ProcessRunner

/// Run a docker command silently and report its `Ok`/`Error`.
let private runDocker
    (runner: IProcessRunner)
    (args: string list)
    (timeout: TimeSpan)
    : Result<string, DevHostFailure> =
    let dockerPath = "docker"
    let spec = mkSpec dockerPath args (Directory.GetCurrentDirectory()) Map.empty timeout None
    match runSync runner spec with
    | Error e -> Error e
    | Ok r ->
        if r.ExitCode = 0 then Ok r.StandardOutput
        else Error(ProcessExitFailure(dockerPath, r.ExitCode, r.StandardError))

/// Verify the docker binary exists in PATH.
let checkDockerBinary (fs: IFilesystem) : Result<string, DevHostFailure> =
    if not (fs.IsFile "/usr/bin/docker") then Error(MissingTool Docker)
    else Ok "/usr/bin/docker"

/// Verify docker daemon is *directly* accessible.
let checkDirectDaemonAccess (runner: IProcessRunner) : Result<string, DevHostFailure> =
    match runDocker runner [ "info" ] (TimeSpan.FromSeconds(30.0)) with
    | Ok out -> Ok out
    | Error _ -> Error(DockerPermissionDenied)

/// Verify docker buildx is installed and usable.
let checkBuildx (runner: IProcessRunner) : Result<string, DevHostFailure> =
    runDocker runner [ "buildx"; "version" ] (TimeSpan.FromSeconds(30.0))

/// Verify docker compose is installed and usable.
let checkCompose (runner: IProcessRunner) : Result<string, DevHostFailure> =
    match runDocker runner [ "compose"; "version" ] (TimeSpan.FromSeconds(30.0)) with
    | Ok out -> Ok out
    | Error _ ->
        runDocker runner [ "docker-compose"; "version" ] (TimeSpan.FromSeconds(30.0))
