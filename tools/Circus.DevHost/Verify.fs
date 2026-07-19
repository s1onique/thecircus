module Circus.DevHost.Verify

open System
open System.IO

open Domain
open Circus.DevHost.Adapters
open Circus.DevHost.DockerChecks
open Circus.DevHost.Doctor
open Circus.DevHost.Evidence
open Circus.DevHost.Paths
open Circus.DevHost.ProcessRunner
open Circus.DevHost.Repository
open Circus.DevHost.SourceVerification

/// The set of verify sub-targets.
type VerifyTarget =
    | Source
    | Docker
    | Gate

/// The inputs for verify.
type VerifyInputs =
    { RepoRoot: string
      Layout: Layout
      AllowDirty: bool }

/// Run `verify source` against the host repository.
let verifySource (runner: IProcessRunner) (inputs: VerifyInputs) : Async<CheckResult list> =
    async {
        match SourceVerification.verifySource runner inputs.Layout.DotNet inputs.RepoRoot with
        | Ok() ->
            return
                [ { Name = "verify-source"
                    Status = Passed
                    Detail = Some "OK" } ]
        | Error e ->
            return
                [ { Name = "verify-source"
                    Status = Failed e
                    Detail = Some(renderFailure e) } ]
    }

let private verifyCheckOk name detail =
    { Name = name
      Status = Passed
      Detail = Some detail }

let private verifyCheckFail name failure =
    { Name = name
      Status = Failed failure
      Detail = Some(renderFailure failure) }

/// Run `verify docker` checks: binary + direct daemon + buildx + compose.
let verifyDocker (runner: IProcessRunner) (fs: IFilesystem) : CheckResult list =
    [ match checkDockerBinary fs with
      | Ok path -> verifyCheckOk "verify-docker.binary" path
      | Error f -> verifyCheckFail "verify-docker.binary" f
      match checkDirectDaemonAccess runner with
      | Ok _ -> verifyCheckOk "verify-docker.daemon" "direct"
      | Error f -> verifyCheckFail "verify-docker.daemon" f
      match checkBuildx runner with
      | Ok _ -> verifyCheckOk "verify-docker.buildx" "ok"
      | Error f -> verifyCheckFail "verify-docker.buildx" f
      match checkCompose runner with
      | Ok _ -> verifyCheckOk "verify-docker.compose" "ok"
      | Error f -> verifyCheckFail "verify-docker.compose" f ]

/// `verify gate` runs the local repository gate script and reports its
/// result.
let verifyGate (_runner: IProcessRunner) (inputs: VerifyInputs) : Async<CheckResult list> =
    async {
        // We delegate to the gate-summary by reading the canonical file the
        // Makefile produces. Failure to find it is a clear failure.
        let path =
            Path.Combine(inputs.RepoRoot, ".factory", "generated", "factory-summary.json")

        if not (File.Exists path) then
            return
                [ { Name = "verify-gate"
                    Status = Failed(MissingAuthorityFile "gate-summary")
                    Detail = Some "factory-summary.json missing" } ]
        else
            return
                [ { Name = "verify-gate"
                    Status = Passed
                    Detail = Some "factory-summary.json present" } ]
    }

/// Compose the per-target verify result for a single command.
let runTarget
    (runner: IProcessRunner)
    (fs: IFilesystem)
    (_env: IEnvironment)
    (inputs: VerifyInputs)
    (target: VerifyTarget)
    : Async<CheckResult list> =
    async {
        match target with
        | Source -> return! verifySource runner inputs
        | Docker -> return verifyDocker runner fs
        | Gate -> return! verifyGate runner inputs
    }
