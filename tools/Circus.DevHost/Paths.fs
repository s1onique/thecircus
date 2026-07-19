module Circus.DevHost.Paths

open System
open System.IO

/// All path constants used by `circus-dev`. The default install root is the
/// repository convention: `$HOME/.local/share/circus-dev`. Operators may
/// override the location by exporting `CIRCUS_TOOL_ROOT` *before* invoking
/// the binary.
type Layout = {
    ToolRoot: string
    Bin: string
    Cache: string
    Downloads: string
    DotNet: string
    NodeRoot: string
    Venvs: string
    PolicyVenv: string
    Evidence: string
    Manifest: string
    Tmp: string
}

module Layout =

    /// Build a layout rooted at the given tool root directory, creating any
    /// missing directories after the call (the caller decides whether
    /// creating is allowed by passing `create=true`).
    let ofRoot (toolRoot: string) : Layout =
        let root = toolRoot.TrimEnd(Path.DirectorySeparatorChar)
        {
            ToolRoot = root
            Bin = Path.Combine(root, "bin")
            Cache = Path.Combine(root, "cache")
            Downloads = Path.Combine(root, "downloads")
            DotNet = Path.Combine(root, "dotnet")
            NodeRoot = Path.Combine(root, "node")
            Venvs = Path.Combine(root, "venvs")
            PolicyVenv = Path.Combine(root, "venvs", "policy")
            Evidence = Path.Combine(root, "evidence")
            Manifest = Path.Combine(root, "manifest.json")
            Tmp = Path.Combine(root, "tmp")
        }

    let defaultLayout () : Layout = ofRoot Layout_Helpers.defaultToolRoot

/// Internal accessor for the default tool root. Exposed via `default` but
/// kept here to make the resolution rule auditable.
module Layout_Helpers =
    let defaultToolRoot : string =
        let explicitRoot = Environment.GetEnvironmentVariable "CIRCUS_TOOL_ROOT"
        if not (String.IsNullOrEmpty explicitRoot) then explicitRoot
        else
            let home = Environment.GetEnvironmentVariable "HOME"
            if String.IsNullOrEmpty home then "/tmp/circus-dev"
            else Path.Combine(home, ".local", "share", "circus-dev")

/// Predicate used by installer flows to decide where to write a Node tool.
let nodeDirectory (layout: Layout) (version: string) : string =
    Path.Combine(layout.NodeRoot, "v" + version)

/// Compute the canonical binary path of a tool given its version.
let toolBinary (binDir: string) (name: string) : string =
    Path.Combine(binDir, name)

/// Helper used by tests to build deterministic path strings independent of
/// the developer's actual HOME.
let combine (parts: string list) : string =
    String.concat (string Path.DirectorySeparatorChar) parts

/// Resolve a path to its absolute form without touching the filesystem.
let makeAbsolute (root: string) (rel: string) : string =
    if Path.IsPathRooted rel then rel
    else Path.Combine(root, rel)
