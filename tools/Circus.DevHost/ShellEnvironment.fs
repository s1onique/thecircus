module Circus.DevHost.ShellEnvironment

open System
open System.IO

open Domain
open Circus.DevHost.Adapters

/// POSIX single-quote shell escape. We wrap the value in `'...'` and
/// replace any `'` inside the string with `'\''`. Both Bash and Zsh share
/// this rule so a single escape works for both shells.
let shellEscape (value: string) : string =
    "'" + value.Replace("'", "'\\''") + "'"

/// Remove duplicate PATH entries while preserving order. The first
/// occurrence wins. Empty entries are dropped.
let dedupPath (entries: string list) : string list =
    let mutable seen : Set<string> = Set.empty
    let mutable result : string list = []
    for e in entries do
        if not (String.IsNullOrEmpty e) then
            if not (Set.contains e seen) then
                seen <- Set.add e seen
                result <- e :: result
    List.rev result

/// Convert a list of paths to a colon-separated PATH value (POSIX form).
let renderPath (entries: string list) : string =
    dedupPath entries |> String.concat ":"

/// The build-time inputs needed to render a shell environment.
type EnvironmentSpec = {
    CircusToolRoot: string
    CircusVenvs: string
    CircusNode: string
    DotNetRoot: string
    NodeBin: string
    VenvBin: string
    ExistingPath: string
}

/// The deterministic ordering of variables emitted by `circus-dev env`.
let variableNames : string list = [
    "CIRCUS_TOOL_ROOT"
    "CIRCUS_VENVS"
    "CIRCUS_NODE"
    "DOTNET_ROOT"
    "DOTNET_CLI_TELEMETRY_OPTOUT"
    "DOTNET_NOLOGO"
    "PATH"
]

/// Render an `EnvironmentSpec` to a deterministic shell script body.
///
/// Both Bash and Zsh accept single-quoted strings. We use the same form
/// for both so the test cases can compare output directly.
let renderEnvironment (spec: EnvironmentSpec) : string =
    let exports : (string * string) list =
        [
            "CIRCUS_TOOL_ROOT", spec.CircusToolRoot
            "CIRCUS_VENVS", spec.CircusVenvs
            "CIRCUS_NODE", spec.CircusNode
            "DOTNET_ROOT", spec.DotNetRoot
            "DOTNET_CLI_TELEMETRY_OPTOUT", "1"
            "DOTNET_NOLOGO", "1"
        ]

    let combinedPath =
        dedupPath [
            spec.NodeBin
            spec.VenvBin
            spec.DotNetRoot
            Path.Combine(spec.DotNetRoot, "tools")
            spec.ExistingPath
        ]

    let all =
        exports @ [ "PATH", renderPath combinedPath ]
    all
    |> List.map (fun (k, v) -> sprintf "export %s=%s" k (shellEscape v))
    |> String.concat "\n"

/// Build an `EnvironmentSpec` from a layout and the parent PATH.
let specFromLayout
    (layout: Paths.Layout)
    (existingPath: string)
    (nodeVersion: string)
    : EnvironmentSpec =
    {
        CircusToolRoot = layout.ToolRoot
        CircusVenvs = layout.Venvs
        CircusNode = Paths.nodeDirectory layout nodeVersion
        DotNetRoot = layout.DotNet
        NodeBin = Path.Combine(Paths.nodeDirectory layout nodeVersion, "bin")
        VenvBin = Path.Combine(layout.PolicyVenv, "bin")
        ExistingPath = existingPath
    }

/// Detect the shell flavor used by the caller. Honors `SHELL`, then
/// falls back to bash. Used by `install-shell-hook`.
let detectShell (env: IEnvironment) : Shell =
    match env.GetEnv "SHELL" with
    | Some s when s.EndsWith "/zsh" -> Zsh
    | Some s when s.EndsWith "/bash" -> Bash
    | _ -> Bash

/// Select a shell explicitly.
let shellFromName (name: string) : Shell option =
    match name with
    | "bash" -> Some Bash
    | "zsh" -> Some Zsh
    | "auto" -> None  // means "detect later"
    | _ -> None

/// Compose an environment render target for a shell. The output is the
/// same text for both shells — only the variable name differs and we use
/// a uniform format.
let renderForShell (shell: Shell) (layout: Paths.Layout) (existingPath: string) (nodeVersion: string) : string =
    let _ = shell
    let spec = specFromLayout layout existingPath nodeVersion
    renderEnvironment spec
