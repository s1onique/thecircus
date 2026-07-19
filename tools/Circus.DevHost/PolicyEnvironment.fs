module Circus.DevHost.PolicyEnvironment

open System
open System.IO

open Domain
open Circus.DevHost.Adapters
open Circus.DevHost.ProcessRunner

/// Verify the policy venv has the expected PyYAML version.
let verifyPolicyVenv
    (runner: IProcessRunner)
    (venvDir: string)
    (expectedPyYaml: ToolVersion)
    : Result<string, DevHostFailure> =
    let python = Path.Combine(venvDir, "bin", "python")
    let pip = Path.Combine(venvDir, "bin", "pip")
    if not (File.Exists python) then Error(MissingTool PolicyPython)
    elif not (File.Exists pip) then Error(VerificationFailure "policy venv pip missing")
    else
        let args = [ "-c"; "import yaml; print(yaml.__version__)" ]
        let spec = mkSpec python args venvDir Map.empty (TimeSpan.FromSeconds(30.0)) None
        match runSync runner spec with
        | Error e -> Error e
        | Ok r ->
            let actual = (r.StandardOutput).Trim()
            if actual = ToolVersion.value expectedPyYaml then Ok actual
            else Error(WrongToolVersion(PyYaml, ToolVersion.value expectedPyYaml, actual))

/// Detect the host `python3.12`.
let detectSystemPython (): string =
    let direct = "/usr/bin/python3.12"
    if File.Exists direct then direct
    else "python3.12"

/// Create the policy venv using the system python and install pinned packages.
let createPolicyVenv
    (runner: IProcessRunner)
    (venvDir: string)
    (pipVersion: ToolVersion)
    (pyYamlVersion: ToolVersion)
    : Result<string, DevHostFailure> =
    let python = detectSystemPython ()
    if not (File.Exists python) then Error(MissingTool PolicyPython)
    else
        let venvSpec =
            mkSpec python [ "-m"; "venv"; venvDir ] (Directory.GetCurrentDirectory()) Map.empty (TimeSpan.FromSeconds(60.0)) None
        match runSync runner venvSpec with
        | Error e -> Error e
        | Ok _ ->
            let pip = Path.Combine(venvDir, "bin", "pip")
            let pipSpec =
                mkSpec pip [ "install"; "--upgrade"; "pip==" + ToolVersion.value pipVersion ]
                    (Directory.GetCurrentDirectory()) Map.empty (TimeSpan.FromSeconds(90.0)) None
            match runSync runner pipSpec with
            | Error e -> Error e
            | Ok _ ->
                let pyYamlSpec =
                    mkSpec pip [ "install"; "PyYAML==" + ToolVersion.value pyYamlVersion ]
                        (Directory.GetCurrentDirectory()) Map.empty (TimeSpan.FromSeconds(90.0)) None
                match runSync runner pyYamlSpec with
                | Error e -> Error e
                | Ok _ ->
                    verifyPolicyVenv runner venvDir pyYamlVersion

/// Idempotent install: verify-then-(re)install for the policy venv.
let reconcilePolicyVenv
    (runner: IProcessRunner)
    (venvDir: string)
    (pipVersion: ToolVersion)
    (pyYamlVersion: ToolVersion)
    (force: bool)
    : Result<string, DevHostFailure> =
    if not force then
        match verifyPolicyVenv runner venvDir pyYamlVersion with
        | Ok actual -> Ok actual
        | Error _ -> createPolicyVenv runner venvDir pipVersion pyYamlVersion
    else createPolicyVenv runner venvDir pipVersion pyYamlVersion
