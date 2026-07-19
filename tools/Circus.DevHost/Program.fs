module Circus.DevHost.Program

open System
open System.IO
open System.Reflection
open System.Threading
open System.Threading.Tasks

open Domain
open Circus.DevHost.Adapters
open Circus.DevHost.Bootstrap
open Circus.DevHost.Cli
open Circus.DevHost.Doctor
open Circus.DevHost.Downloads
open Circus.DevHost.ProcessRunner
open Circus.DevHost.Repository
open Circus.DevHost.ShellEnvironment
open Circus.DevHost.ShellProfile
open Circus.DevHost.ToolchainManifest
open Circus.DevHost.Verify

/// The default tool root used when `CIRCUS_TOOL_ROOT` is unset.
let defaultToolRoot () : string =
    let v = Environment.GetEnvironmentVariable "CIRCUS_TOOL_ROOT"
    if not (System.String.IsNullOrEmpty v) then v
    else
        let home = Environment.GetEnvironmentVariable "HOME"
        if System.String.IsNullOrEmpty home then "/tmp/circus-dev"
        else Path.Combine(home, ".local", "share", "circus-dev")

/// The repository root, computed by walking up from the executable.
let resolveRepoRoot () : string =
    let rec findRoot (directory: string) : string option =
        if File.Exists(Path.Combine(directory, "Circus.sln")) then
            Some directory
        else
            let parent = Path.GetDirectoryName directory

            if String.IsNullOrEmpty parent || parent = directory then
                None
            else
                findRoot parent

    let location = Assembly.GetExecutingAssembly().Location

    let startingDirectory =
        if String.IsNullOrEmpty location then
            Environment.CurrentDirectory
        else
            let directory = Path.GetDirectoryName location

            if String.IsNullOrEmpty directory then
                Environment.CurrentDirectory
            else
                directory

    findRoot startingDirectory |> Option.defaultValue Environment.CurrentDirectory

/// The execution flow: parse, dispatch, render, exit.
/// Returns the chosen exit code.
let execute
    (argv: string list)
    (runner: IProcessRunner)
    (fs: IFilesystem)
    (env: IEnvironment)
    (console: IConsole)
    (http: IHttp)
    : int =

    let repoRoot = resolveRepoRoot ()
    let layout = Paths.Layout.ofRoot (defaultToolRoot ())

    let cancellation = CancellationToken.None

    match Cli.parse argv with
    | Error msg ->
        console.Stderr ("error: " + msg)
        ExitCodes.Code.contractError
    | Ok command ->
        match command with
        | Version ->
            let asm = Assembly.GetExecutingAssembly()
            let attribute = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()

            let v =
                match attribute with
                | null -> "1.0.0"
                | value -> value.InformationalVersion

            let commit = "0"
            console.Stdout(sprintf "circus-dev %s" v)
            console.Stdout("commit: " + commit)
            console.Stdout("built: " + (DateTimeOffset.UtcNow.ToString("o")))
            console.Stdout("runtime: " + (Environment.Version.ToString()))
            ExitCodes.Code.success

        | Help ->
            console.Stdout(helpText ())
            ExitCodes.Code.success

        | Check ->
            // Read-only: ensure core authority files are present and the
            // toolchain manifest satisfies its structural invariants.
            let manifestPath = Path.Combine(repoRoot, "eng", "devhost-toolchain.json")

            let missing =
                [ if not (fs.IsFile (Path.Combine(repoRoot, "Circus.sln"))) then
                      "Circus.sln"
                  if not (fs.IsFile (Path.Combine(repoRoot, "global.json"))) then
                      "global.json"
                  if not (fs.IsFile (Path.Combine(repoRoot, "Dockerfile.frontend"))) then
                      "Dockerfile.frontend"
                  if not (fs.IsFile (Path.Combine(repoRoot, "web", "elm.json"))) then
                      "web/elm.json"
                  if not (fs.IsFile (Path.Combine(repoRoot, "web", "package.json"))) then
                      "web/package.json"
                  if not (fs.IsFile manifestPath) then
                      "eng/devhost-toolchain.json" ]

            let manifestIssue =
                if not (fs.IsFile manifestPath) then
                    None
                else
                    let parsed =
                        try
                            fs.ReadAllText manifestPath
                            |> Manifest.parse
                            |> Some
                        with
                        | ManifestFormatException _ -> None
                        | _ -> None

                    match parsed with
                    | None -> Some "manifest is unparseable"
                    | Some manifest ->
                        match validate manifest with
                        | Ok() -> None
                        | Error failure -> Some(renderFailure failure)

            let issues = missing @ Option.toList manifestIssue

            if List.isEmpty issues then
                console.Stdout "check: OK"
                ExitCodes.Code.success
            else
                console.Stderr("check: " + String.concat "; " issues)
                ExitCodes.Code.contractError

        | Bootstrap (force, dryRun) ->
            let manifestPath = Path.Combine(repoRoot, "eng", "devhost-toolchain.json")
            let json = fs.ReadAllText manifestPath

            try
                let m = Manifest.parse json

                let bootstrapInputs: BootstrapInputs =
                    { RepoRoot = repoRoot
                      Layout = layout
                      Manifest = m
                      Force = force
                      DryRun = dryRun }

                if dryRun then
                    for step in planSteps () do
                        console.Stdout(describe step)

                    ExitCodes.Code.success
                else
                    let failures =
                        Async.RunSynchronously(Bootstrap.run http runner fs env bootstrapInputs cancellation)

                    if failures.IsEmpty then
                        console.Stdout "bootstrap: OK"
                        ExitCodes.Code.success
                    else
                        for f in failures do
                            console.Stderr("bootstrap: " + renderFailure f)

                        ExitCodes.Code.capabilityFailure
            with
            | ManifestFormatException msg ->
                console.Stderr("bootstrap: malformed manifest: " + msg)
                ExitCodes.Code.contractError

        | Doctor (json, allowDirty) ->
            let manifestPath = Path.Combine(repoRoot, "eng", "devhost-toolchain.json")

            let inputs: DoctorInputs =
                { RepoRoot = repoRoot
                  Layout = layout
                  ManifestPath = manifestPath
                  AllowDirty = allowDirty
                  RunTests = false }

            let outcome, human, jsonOut = Doctor.run runner fs env inputs

            if json then
                console.Stdout jsonOut
            else
                console.Stdout human

            (match Domain.classify outcome with
             | Success -> ExitCodes.Code.success
             | CapabilityFailure -> ExitCodes.Code.capabilityFailure
             | ContractError -> ExitCodes.Code.contractError)

        | Env shell ->
            let resolved =
                match shell with
                | Some s -> s
                | None -> detectShell env

            let nodeVer = readNodeVersion repoRoot

            let v =
                match nodeVer with
                | Ok nv -> ToolVersion.value nv
                | _ -> "0.0.0"

            let existingPath = env.GetEnv "PATH" |> Option.defaultValue ""
            let text = ShellEnvironment.renderForShell resolved layout existingPath v
            console.Stdout text
            ExitCodes.Code.success

        | InstallShellHook shell ->
            let resolved =
                match shell with
                | Some s -> s
                | None -> detectShell env

            let path = profilePathFor env resolved
            let binPath = Path.Combine(layout.ToolRoot, "bin", "circus-dev")
            let block = renderBlock binPath resolved

            match applyProfile fs path block false with
            | Appended ->
                console.Stdout("installed block into " + path)
                ExitCodes.Code.success
            | ReplacedExisting ->
                console.Stdout("updated block in " + path)
                ExitCodes.Code.success
            | NoChangeNeeded ->
                console.Stdout("no change: " + path)
                ExitCodes.Code.success
            | DuplicateBlocks n ->
                console.Stderr(sprintf "duplicate managed blocks (%d): %s" n path)
                ExitCodes.Code.contractError
            | MalformedProfile f
            | WriteError f ->
                console.Stderr("install-shell-hook: " + renderFailure f)
                ExitCodes.Code.contractError

        | Verify kind ->
            let inputs: VerifyInputs =
                { RepoRoot = repoRoot
                  Layout = layout
                  AllowDirty = false }

            let collect kind' =
                Async.RunSynchronously(Verify.runTarget runner fs env inputs kind')

            let results =
                match kind with
                | VerifyAll ->
                    [ yield! collect Verify.Source
                      yield! collect Verify.Docker
                      yield! collect Verify.Gate ]
                | _ ->
                    let target =
                        match kind with
                        | VerifySource -> Verify.Source
                        | VerifyDocker -> Verify.Docker
                        | VerifyGate -> Verify.Gate
                        | _ -> Verify.Gate

                    collect (target: Verify.VerifyTarget)

            let outcome = { Checks = results }

            (match Domain.classify outcome with
             | Success ->
                 console.Stdout "verify: OK"
                 ExitCodes.Code.success
             | CapabilityFailure ->
                 console.Stderr "verify: failed"

                 for r in results do
                     match r.Status with
                     | Failed f -> console.Stderr(" - " + r.Name + ": " + renderFailure f)
                     | _ -> ()

                 ExitCodes.Code.capabilityFailure
             | ContractError -> ExitCodes.Code.contractError)

/// Process entry point.
[<EntryPoint>]
let main (argv: string[]) : int =
    try
        let runner = RealProcessRunner() :> IProcessRunner
        let fs = RealFilesystem() :> IFilesystem
        let env = RealEnvironment() :> IEnvironment
        let console = RealConsole() :> IConsole
        let http = RealHttp(TimeSpan.FromMinutes 15.0) :> IHttp
        execute (argv |> Array.toList) runner fs env console http
    with ex ->
        eprintfn "circus-dev: internal error: %s" ex.Message
        ExitCodes.Code.contractError
