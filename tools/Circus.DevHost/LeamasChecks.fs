module Circus.DevHost.LeamasChecks

open System
open System.IO

open Domain
open Circus.DevHost.Adapters
open Circus.DevHost.Paths
open Circus.DevHost.ProcessRunner

/// Find `leamas` through PATH, then documented system and user-local
/// fallback locations.
let locateLeamas () : string option =
    let path = Environment.GetEnvironmentVariable "PATH"
    let pathValue = if String.IsNullOrEmpty path then None else Some path
    let home = Environment.GetEnvironmentVariable "HOME"

    let userFallback =
        if String.IsNullOrEmpty home then
            []
        else
            [ Path.Combine(home, ".local", "bin", "leamas") ]

    let fallbacks =
        [ "/usr/bin/leamas"; "/usr/local/bin/leamas"; "/opt/leamas/bin/leamas" ]
        @ userFallback

    locateInPath File.Exists pathValue fallbacks "leamas"

/// Run `leamas version` and return the parsed version string.
let checkLeamasBinary (runner: IProcessRunner) (path: string) : Result<string, DevHostFailure> =
    let spec =
        mkSpec path [ "version" ] (Directory.GetCurrentDirectory()) Map.empty (TimeSpan.FromSeconds(30.0)) None

    match runSync runner spec with
    | Ok result -> Ok(result.StandardOutput.Trim())
    | Error failure -> Error failure

/// Verify the `leamas factory digest` subcommand is available.
let checkFactoryDigest (runner: IProcessRunner) (path: string) : Result<string, DevHostFailure> =
    let spec =
        mkSpec
            path
            [ "factory"; "digest"; "--help" ]
            (Directory.GetCurrentDirectory())
            Map.empty
            (TimeSpan.FromSeconds(30.0))
            None

    match runSync runner spec with
    | Ok result -> Ok(result.StandardOutput.Trim())
    | Error(ProcessExitFailure(_, _, stderr)) when
        stderr.Contains("Usage: leamas factory digest", StringComparison.Ordinal)
        ->
        Ok "available"
    | Error failure -> Error failure
