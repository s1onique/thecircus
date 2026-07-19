module Circus.DevHost.Doctor

open System
open System.IO
open System.Diagnostics

open Domain
open Circus.DevHost.Adapters
open Circus.DevHost.DockerChecks
open Circus.DevHost.DotNetInstaller
open Circus.DevHost.Evidence
open Circus.DevHost.FrontendInstaller
open Circus.DevHost.LeamasChecks
open Circus.DevHost.NodeInstaller
open Circus.DevHost.Paths
open Circus.DevHost.PolicyEnvironment
open Circus.DevHost.ProcessRunner
open Circus.DevHost.Repository
open Circus.DevHost.SourceVerification
open Circus.DevHost.ToolInstaller

/// All inputs needed to run the doctor. The repo root is anchored to a
/// fixed location so tests can build a deterministic fixture.
type DoctorInputs = {
    RepoRoot: string
    Layout: Layout
    ManifestPath: string
    AllowDirty: bool
    RunTests: bool
}

/// Read the manifest once and reuse it across checks.
let loadManifest (path: string) : Result<string * Manifest.Manifest, DevHostFailure> =
    try
        let json = File.ReadAllText path
        let m = Manifest.parse json
        Ok (json, m)
    with
    | Manifest.ManifestFormatException msg ->
        Error(MalformedAuthorityFile ("eng/devhost-toolchain.json: " + msg))
    | :? FileNotFoundException ->
        Error(MissingAuthorityFile path)
    | ex ->
        Error(MalformedAuthorityFile ("eng/devhost-toolchain.json: " + ex.Message))

/// Tiny helpers so the calling code is short.
let private checkOk (name: string) (detail: string) : CheckResult =
    { Name = name; Status = Passed; Detail = Some detail }
let private checkFail (name: string) (f: DevHostFailure) : CheckResult =
    { Name = name; Status = Failed f; Detail = Some (renderFailure f) }
let private checkSkip (name: string) (reason: string) : CheckResult =
    { Name = name; Status = Skipped reason; Detail = Some reason }

/// Read git fields and produce an identity using direct `Process` calls
/// (no shell, no eval). Done deliberately; the alternative of piping
/// through `IProcessRunner` is not used because git is not in our bounded
/// exec set.
let readIdentity (repoRoot: string) : Result<Identity, DevHostFailure> =
    let runGit (args: string) : string =
        try
            use p = Process.Start(
                ProcessStartInfo(
                    FileName = "git",
                    Arguments = args,
                    WorkingDirectory = repoRoot,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false))
            p.WaitForExit(5000) |> ignore
            (p.StandardOutput.ReadToEnd()).Trim()
        with _ -> ""
    try
        let top = runGit "rev-parse --show-toplevel"
        if not (top = repoRoot) then
            Ok {
                Root = repoRoot
                Branch = ""
                Commit = ""
                Tree = ""
                IsDirty = true
                HeadStatus = "repo-mismatch"
            }
        else
            let branch = runGit "branch --show-current"
            let commit = runGit "rev-parse HEAD"
            let tree = runGit "rev-parse HEAD^{tree}"
            let dirtyOutput = runGit "status --porcelain=v1"
            Ok {
                Root = repoRoot
                Branch = branch
                Commit = commit
                Tree = tree
                IsDirty = dirtyOutput <> ""
                HeadStatus = "ok"
            }
    with _ ->
        Error(RepositoryIdentityFailure "git unavailable")

/// Capture optional tool versions from authority files. Used to decide
/// whether downstream checks should run.
let private readVersions (repoRoot: string) =
    let dotnet = readDotNetVersion repoRoot
    let node = readNodeVersion repoRoot
    let elm = readElmCompilerVersion repoRoot
    dotnet, node, elm

/// Pick the manifest authority fields used by individual checks.
let private manifestOutcomes (runner: IProcessRunner) (fs: IFilesystem) (layout: Layout) (manifestOutcome: Result<string * Manifest.Manifest, DevHostFailure>) =
    match manifestOutcome with
    | Ok (json, m) ->
        [
            let versionFailures =
                Manifest.reconcileAgainst m None None None
            if List.isEmpty versionFailures then
                checkOk "manifest" (m.Actionlint.Version + "/" + m.ShellCheck.Version)
            else
                checkFail "manifest" (List.head versionFailures)

            match readPolicyPyYamlVersion json with
            | Ok v ->
                match verifyPolicyVenv runner layout.PolicyVenv v with
                | Ok actual -> checkOk "policy-pyvenv" actual
                | Error f -> checkFail "policy-pyvenv" f
            | Error f -> checkFail "policy-pyvenv" f

            match readActionlintVersion json with
            | Ok v ->
                match verifyActionlint runner (Path.Combine(layout.Bin, "actionlint")) v with
                | Ok actual -> checkOk "actionlint" actual
                | Error f -> checkFail "actionlint" f
            | Error f -> checkFail "actionlint" f

            match readShellCheckVersion json with
            | Ok v ->
                match verifyShellCheck runner (Path.Combine(layout.Bin, "shellcheck")) v with
                | Ok actual -> checkOk "shellcheck" actual
                | Error f -> checkFail "shellcheck" f
            | Error f -> checkFail "shellcheck" f
        ]
    | Error f -> [ checkFail "manifest" f ]

/// Run all doctor checks and return the unified result list.
let checksFor
    (runner: IProcessRunner)
    (fs: IFilesystem)
    (env: IEnvironment)
    (inputs: DoctorInputs)
    : CheckResult list =

    let dotnetVer, nodeVer, elmVer = readVersions inputs.RepoRoot
    let manifestOutcome = loadManifest inputs.ManifestPath

    let hostChecks =
        let host = Platform.probeHost ()
        [
            checkOk "host.arch" host.Arch
            match isSupported host with
            | Ok () -> checkOk "host.os" host.PrettyName
            | Error f -> checkFail "host.os" f
        ]

    let repoChecks =
        match readIdentity inputs.RepoRoot with
        | Ok id ->
            [
                if id.IsDirty && not inputs.AllowDirty then
                    checkFail "repo.clean" RepositoryDirty
                else
                    checkOk "repo.clean" id.Branch
                checkOk "repo.commit" id.Commit
            ]
        | Error f -> [ checkFail "repo.identity" f ]

    let dotnetChecks =
        [
            match dotnetVer with
            | Ok v -> checkOk "dotnet-sdk" (ToolVersion.value v)
            | Error f -> checkFail "dotnet-sdk" f
            let placeholder = ToolVersion.unsafeParse "0.0.0"
            match verifyDotnet runner inputs.Layout.DotNet
                  (match dotnetVer with Ok v -> v | _ -> placeholder) with
            | Ok actual -> checkOk "dotnet-sdk-installed" actual
            | Error f -> checkFail "dotnet-sdk-installed" f
            match fsInteractiveWorks runner inputs.Layout.DotNet with
            | Ok () -> checkOk "dotnet-fsi" "ok"
            | Error f -> checkFail "dotnet-fsi" f
        ]

    let nodeChecks =
        [
            match nodeVer with
            | Ok v -> checkOk "node" (sprintf "v%s" (ToolVersion.value v))
            | Error f -> checkFail "node" f
            match nodeVer with
            | Some v ->
                match verifyNode runner (nodeDirectory inputs.Layout (ToolVersion.value v)) v with
                | Ok actual -> checkOk "node-installed" actual
                | Error f -> checkFail "node-installed" f
                let npmBin =
                    Path.Combine(nodeDirectory inputs.Layout (ToolVersion.value v), "bin", "npm")
                if fs.IsFile npmBin then checkOk "npm" "available"
                else checkFail "npm" (MissingTool Npm)
            | None -> checkSkip "node-installed" "no authority"
        ]

    let elmChecks =
        [
            match elmVer with
            | Ok v -> checkOk "elm-compiler-authority" (ToolVersion.value v)
            | Error f -> checkFail "elm-compiler-authority" f
            match elmVer with
            | Ok v ->
                match verifyElm runner (Path.Combine(inputs.RepoRoot, "web")) v with
                | Ok _ -> checkOk "elm" (ToolVersion.value v)
                | Error f -> checkFail "elm" f
                match verifyElmTest fs (Path.Combine(inputs.RepoRoot, "web")) with
                | Ok () -> checkOk "elm-test" "available"
                | Error f -> checkFail "elm-test" f
            | Error _ -> checkSkip "elm" "no authority"
        ]

    let manifestChecks = manifestOutcomes runner fs inputs.Layout manifestOutcome

    let dockerChecks =
        [
            match checkDockerBinary fs with
            | Ok _ -> checkOk "docker-binary" "/usr/bin/docker"
            | Error f -> checkFail "docker-binary" f
            match checkDirectDaemonAccess runner with
            | Ok _ -> checkOk "docker-daemon" "direct"
            | Error f -> checkFail "docker-daemon" f
            match checkBuildx runner with
            | Ok _ -> checkOk "docker-buildx" "ok"
            | Error f -> checkFail "docker-buildx" f
            match checkCompose runner with
            | Ok _ -> checkOk "docker-compose" "ok"
            | Error f -> checkFail "docker-compose" f
        ]

    let leamasChecks =
        match LeamasChecks.locateLeamas () with
        | Some path ->
            [
                match checkLeamasBinary runner path with
                | Ok actual -> checkOk "leamas" actual
                | Error f -> checkFail "leamas" f
                match checkFactoryDigest runner path with
                | Ok _ -> checkOk "leamas-factory-digest" "ok"
                | Error f -> checkFail "leamas-factory-digest" f
            ]
        | None -> [ checkFail "leamas" (MissingTool Leamas) ]

    let psqlCheck =
        let candidates = [ "/usr/bin/psql"; "/usr/local/bin/psql" ]
        if candidates |> List.exists (fun p -> fs.IsFile p) then
            checkOk "psql" "available"
        else checkSkip "psql" "not installed"

    let makefileCheck =
        let path = Path.Combine(inputs.RepoRoot, "Makefile")
        if fs.IsFile path then
            let text = fs.ReadAllText path
            let r = makefileHasTargets text [ "build-backend"; "test-backend"; "build-frontend" ]
            let missing = r |> List.filter (fun (_, has) -> not has) |> List.map fst
            if List.isEmpty missing then
                checkOk "make-targets" (sprintf "%d/%d targets" (r.Length - missing.Length) r.Length)
            else
                checkFail "make-targets" (VerificationFailure ("missing: " + String.concat "," missing))
        else checkFail "makefile" (MissingAuthorityFile "Makefile")

    let dockerfileCheck =
        let backend = Path.Combine(inputs.RepoRoot, "Dockerfile.backend")
        let frontend = Path.Combine(inputs.RepoRoot, "Dockerfile.frontend")
        let miss =
            [
                if not (fs.IsFile backend) then yield "Dockerfile.backend"
                if not (fs.IsFile frontend) then yield "Dockerfile.frontend"
            ]
        if List.isEmpty miss then checkOk "dockerfiles" "both present"
        else checkFail "dockerfiles" (MissingAuthorityFile (String.concat "," miss))

    hostChecks @ repoChecks @ dotnetChecks @ nodeChecks @ elmChecks
    @ manifestChecks @ dockerChecks @ leamasChecks @ [ psqlCheck; makefileCheck; dockerfileCheck ]

/// Render the human-readable, plain-text doctor output.
let renderHuman (results: CheckResult list) : string =
    let sb = System.Text.StringBuilder()
    sb.AppendLine "=== Circus DevHost Doctor ===" |> ignore
    for r in results do
        let prefix =
            match r.Status with
            | Passed -> "OK    "
            | Skipped _ -> "SKIP  "
            | Failed _ -> "FAIL  "
        sb.Append(sprintf "%s %s" prefix r.Name) |> ignore
        (match r.Detail with
         | Some d when d <> "" -> sb.Append(sprintf " :: %s" d) |> ignore
         | _ -> ())
        sb.AppendLine () |> ignore
    let anyFailed = results |> List.exists (fun r -> match r.Status with Failed _ -> true | _ -> false)
    sb.Append("=== Overall: " + (if anyFailed then "FAIL" else "PASS") + " ===") |> ignore
    sb.ToString()

/// Render JSON evidence for `doctor --json`.
let renderJson (commit: string) (tree: string) (toolVersion: string) (results: CheckResult list) : string =
    let report = Evidence.build commit tree toolVersion results
    Evidence.toJson report

/// Run the doctor end-to-end.
let run
    (runner: IProcessRunner)
    (fs: IFilesystem)
    (env: IEnvironment)
    (inputs: DoctorInputs)
    : AggregateOutcome * string * string =
    let results = checksFor runner fs env inputs
    let identity =
        match readIdentity inputs.RepoRoot with
        | Ok id -> id
        | Error _ -> Identity.empty inputs.RepoRoot
    let outcome = { Checks = results }
    let human = renderHuman results
    let json = renderJson identity.Commit identity.Tree "1.0.0" results
    outcome, human, json
