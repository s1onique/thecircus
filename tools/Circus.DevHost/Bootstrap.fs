module Circus.DevHost.Bootstrap

open System
open System.IO
open System.Threading

open Domain
open Circus.DevHost.Adapters
open Circus.DevHost.DotNetInstaller
open Circus.DevHost.Downloads
open Circus.DevHost.NodeInstaller
open Circus.DevHost.FrontendInstaller
open Circus.DevHost.PolicyEnvironment
open Circus.DevHost.ProcessRunner
open Circus.DevHost.Repository
open Circus.DevHost.ToolchainManifest
open Circus.DevHost.ToolInstaller

/// Inputs for the bootstrap executor.
type BootstrapInputs =
    { RepoRoot: string
      Layout: Paths.Layout
      Manifest: ToolchainData
      Force: bool
      DryRun: bool }

type PlanStep =
    | PlanDotnet
    | PlanNode
    | PlanElm
    | PlanPolicyVenv
    | PlanActionlint
    | PlanShellCheck

/// Describe the planned actions of a bootstrap run. Dry-run simply prints
/// this list to stdout.
let planSteps () : PlanStep list =
    [ PlanDotnet
      PlanNode
      PlanElm
      PlanPolicyVenv
      PlanActionlint
      PlanShellCheck ]

/// Render a plan step to a stable string.
let describe (s: PlanStep) : string =
    match s with
    | PlanDotnet -> "install .NET SDK"
    | PlanNode -> "install Node.js"
    | PlanElm -> "restore locked Elm dependencies"
    | PlanPolicyVenv -> "create policy Python venv"
    | PlanActionlint -> "install actionlint"
    | PlanShellCheck -> "install ShellCheck"

/// Resolve the .NET SDK version from `global.json`. Used by the executor.
let readDotnetAuthority (repoRoot: string) : Result<ToolVersion, DevHostFailure> = readDotNetVersion repoRoot

/// Resolve the Node.js version from `Dockerfile.frontend`.
let readNodeAuthority (repoRoot: string) : Result<ToolVersion, DevHostFailure> = readNodeVersion repoRoot

/// Run the planned dotnet install. `dryRun=true` skips the actual install.
let executeDotnet
    (http: IHttp)
    (runner: IProcessRunner)
    (inputs: BootstrapInputs)
    (cancellation: CancellationToken)
    : Async<Result<string, DevHostFailure>> =
    async {
        if inputs.DryRun then
            return Ok "(dry-run) install .NET SDK"
        else
            match readDotnetAuthority inputs.RepoRoot with
            | Error e -> return Error e
            | Ok v ->
                return! installDotnet http runner inputs.Layout.DotNet v inputs.Force inputs.Layout.Cache cancellation
    }

/// Run the planned Node install.
let executeNode
    (http: IHttp)
    (runner: IProcessRunner)
    (inputs: BootstrapInputs)
    (cancellation: CancellationToken)
    : Async<Result<string, DevHostFailure>> =
    async {
        if inputs.DryRun then
            return Ok "(dry-run) install Node.js"
        else
            match readNodeAuthority inputs.RepoRoot with
            | Error e -> return Error e
            | Ok v ->
                return!
                    installNode
                        http
                        runner
                        inputs.Layout.Cache
                        (Paths.nodeDirectory inputs.Layout (ToolVersion.value v))
                        v
                        inputs.Force
                        cancellation
    }

/// Run the planned frontend restore. We always run `npm ci`.
let executeElm
    (runner: IProcessRunner)
    (inputs: BootstrapInputs)
    (cancellation: CancellationToken)
    : Async<Result<string, DevHostFailure>> =
    async {
        if inputs.DryRun then
            return Ok "(dry-run) restore Elm"
        else
            match readNodeAuthority inputs.RepoRoot with
            | Error e -> return Error e
            | Ok v ->
                let webDir = Path.Combine(inputs.RepoRoot, "web")
                let nodeDir = Paths.nodeDirectory inputs.Layout (ToolVersion.value v)
                let! restoreOutcome = restoreFrontend runner webDir nodeDir (fun () -> async.Return())

                return restoreOutcome |> Result.map (fun () -> "restored Elm")
    }

/// Run the planned policy venv install.
let executePolicyVenv (runner: IProcessRunner) (inputs: BootstrapInputs) : Result<string, DevHostFailure> =
    if inputs.DryRun then
        Ok "(dry-run) create policy venv"
    else
        let pipVersion = inputs.Manifest.PythonPolicy.Pip
        let pyYamlVersion = ToolVersion.unsafeParse inputs.Manifest.PythonPolicy.PyYaml
        reconcilePolicyVenv runner inputs.Layout.PolicyVenv pipVersion pyYamlVersion inputs.Force

/// Run the planned actionlint install using the manifest authority.
let executeActionlint
    (http: IHttp)
    (runner: IProcessRunner)
    (fs: IFilesystem)
    (inputs: BootstrapInputs)
    (cancellation: CancellationToken)
    : Async<Result<string, DevHostFailure>> =
    async {
        if inputs.DryRun then
            return Ok "(dry-run) install actionlint"
        else
            let url = System.Uri inputs.Manifest.Actionlint.LinuxX64Url
            let sha = inputs.Manifest.Actionlint.Sha256
            let version = ToolVersion.unsafeParse inputs.Manifest.Actionlint.Version

            return!
                installActionlint
                    http
                    runner
                    fs
                    inputs.Layout.Cache
                    inputs.Layout.Tmp
                    inputs.Layout.Bin
                    (url.OriginalString)
                    sha
                    version
                    cancellation
    }

/// Run the planned ShellCheck install.
let executeShellCheck
    (http: IHttp)
    (runner: IProcessRunner)
    (fs: IFilesystem)
    (inputs: BootstrapInputs)
    (cancellation: CancellationToken)
    : Async<Result<string, DevHostFailure>> =
    async {
        if inputs.DryRun then
            return Ok "(dry-run) install ShellCheck"
        else
            let url = System.Uri inputs.Manifest.ShellCheck.LinuxX64Url
            let sha = inputs.Manifest.ShellCheck.Sha256
            let version = ToolVersion.unsafeParse inputs.Manifest.ShellCheck.Version

            return!
                installShellCheck
                    http
                    runner
                    fs
                    inputs.Layout.Cache
                    inputs.Layout.Tmp
                    inputs.Layout.Bin
                    (url.OriginalString)
                    sha
                    version
                    cancellation
    }

/// Drive the full bootstrap sequence. Returns the list of step outcomes.
let run
    (http: IHttp)
    (runner: IProcessRunner)
    (fs: IFilesystem)
    (_env: IEnvironment)
    (inputs: BootstrapInputs)
    (cancellation: CancellationToken)
    : Async<DevHostFailure list> =
    async {
        let mutable failures: DevHostFailure list = []

        match! executeDotnet http runner inputs cancellation with
        | Ok _ -> ()
        | Error e -> failures <- e :: failures

        match! executeNode http runner inputs cancellation with
        | Ok _ -> ()
        | Error e -> failures <- e :: failures

        match! executeElm runner inputs cancellation with
        | Ok _ -> ()
        | Error e -> failures <- e :: failures

        match executePolicyVenv runner inputs with
        | Ok _ -> ()
        | Error e -> failures <- e :: failures

        match! executeActionlint http runner fs inputs cancellation with
        | Ok _ -> ()
        | Error e -> failures <- e :: failures

        match! executeShellCheck http runner fs inputs cancellation with
        | Ok _ -> ()
        | Error e -> failures <- e :: failures

        return List.rev failures
    }
