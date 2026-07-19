module Circus.DevHost.LeamasChecks

open System
open System.IO

open Domain
open Circus.DevHost.Adapters
open Circus.DevHost.ProcessRunner

/// Find `leamas` on the system.
let locateLeamas (): string option =
    let candidates = [
        "/usr/bin/leamas"
        "/usr/local/bin/leamas"
        "/opt/leamas/bin/leamas"
    ]
    candidates |> List.tryFind File.Exists

/// Run `leamas version` and return the parsed version string.
let checkLeamasBinary
    (runner: IProcessRunner)
    (path: string)
    : Result<string, DevHostFailure> =
    let spec = mkSpec path [ "version" ] (Directory.GetCurrentDirectory()) Map.empty (TimeSpan.FromSeconds(30.0)) None
    match runSync runner spec with
    | Error e -> Error e
    | Ok r ->
        if r.ExitCode = 0 then Ok (r.StandardOutput.Trim())
        else Error(ProcessExitFailure(path, r.ExitCode, r.StandardError))

/// Verify the `leamas factory digest` subcommand is available.
let checkFactoryDigest
    (runner: IProcessRunner)
    (path: string)
    : Result<string, DevHostFailure> =
    let spec = mkSpec path [ "factory"; "digest"; "--help" ] (Directory.GetCurrentDirectory()) Map.empty (TimeSpan.FromSeconds(30.0)) None
    match runSync runner spec with
    | Error e -> Error e
    | Ok r ->
        if r.ExitCode = 0 then Ok (r.StandardOutput.Trim())
        else Error(ProcessExitFailure(path, r.ExitCode, r.StandardError))
