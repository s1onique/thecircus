module Circus.DevHost.Paths

open System
open System.IO

/// Internal accessor for the default tool root. Kept separate so the
/// environment-resolution rule remains explicit and testable.
module Layout_Helpers =
    let defaultToolRoot: string =
        let explicitRoot = Environment.GetEnvironmentVariable "CIRCUS_TOOL_ROOT"

        if not (String.IsNullOrEmpty explicitRoot) then
            explicitRoot
        else
            let home = Environment.GetEnvironmentVariable "HOME"

            if String.IsNullOrEmpty home then
                "/tmp/circus-dev"
            else
                Path.Combine(home, ".local", "share", "circus-dev")

/// All path constants used by `circus-dev`.
type Layout =
    { ToolRoot: string
      Bin: string
      Cache: string
      Downloads: string
      DotNet: string
      NodeRoot: string
      Venvs: string
      PolicyVenv: string
      Evidence: string
      Manifest: string
      Tmp: string }

module Layout =
    let ofRoot (toolRoot: string) : Layout =
        let root = toolRoot.TrimEnd(Path.DirectorySeparatorChar)

        { ToolRoot = root
          Bin = Path.Combine(root, "bin")
          Cache = Path.Combine(root, "cache")
          Downloads = Path.Combine(root, "downloads")
          DotNet = Path.Combine(root, "dotnet")
          NodeRoot = Path.Combine(root, "node")
          Venvs = Path.Combine(root, "venvs")
          PolicyVenv = Path.Combine(root, "venvs", "policy")
          Evidence = Path.Combine(root, "evidence")
          Manifest = Path.Combine(root, "manifest.json")
          Tmp = Path.Combine(root, "tmp") }

    let defaultLayout () : Layout = ofRoot Layout_Helpers.defaultToolRoot

let nodeDirectory (layout: Layout) (version: string) : string =
    Path.Combine(layout.NodeRoot, "v" + version)

let toolBinary (binDir: string) (name: string) : string = Path.Combine(binDir, name)

let combine (parts: string list) : string =
    String.concat (string Path.DirectorySeparatorChar) parts

let makeAbsolute (root: string) (relativePath: string) : string =
    if Path.IsPathRooted relativePath then
        relativePath
    else
        Path.Combine(root, relativePath)

/// Resolve an executable through PATH and then explicit fallback paths.
/// The existence predicate is injected so doctor checks remain testable.
let locateInPath
    (fileExists: string -> bool)
    (pathValue: string option)
    (fallbacks: string list)
    (executable: string)
    : string option =
    let pathCandidates =
        pathValue
        |> Option.defaultValue ""
        |> fun value -> value.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        |> Array.map (fun directory -> Path.Combine(directory, executable))
        |> Array.toList

    pathCandidates @ fallbacks |> List.distinct |> List.tryFind fileExists
