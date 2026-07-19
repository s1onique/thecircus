module Circus.DevHost.Repository

open System
open System.IO
open System.Text.RegularExpressions

open Domain

/// A snapshot of repository identity used by doctor, bootstrap, and verify.
type Identity =
    { Root: string
      Branch: string
      Commit: string
      Tree: string
      IsDirty: bool
      HeadStatus: string }

module Identity =

    /// Empty identity used when git is not available.
    let empty (root: string) : Identity =
        { Root = root
          Branch = ""
          Commit = ""
          Tree = ""
          IsDirty = true
          HeadStatus = "missing-git" }

/// Parse `global.json` and extract the `.sdk.version` value. Returns a
/// `DevHostFailure` rather than relying on exception classification.
let readDotNetVersion (repoRoot: string) : Result<ToolVersion, DevHostFailure> =
    let path = Path.Combine(repoRoot, "global.json")

    if not (File.Exists path) then
        Error(MissingAuthorityFile path)
    else
        try
            let text = File.ReadAllText path
            let m = Regex.Match(text, "\"version\"\s*:\s*\"([0-9]+\.[0-9]+\.[0-9]+)\"")

            if not m.Success then
                Error(MalformedAuthorityFile path)
            else
                let raw = m.Groups.[1].Value

                match ToolVersion.parse raw with
                | Ok v -> Ok v
                | Error _ -> Error(MalformedAuthorityFile path)
        with _ ->
            Error(MalformedAuthorityFile path)

/// Read the *compiler* version from `web/elm.json`. We deliberately ignore
/// the npm package version because the elm npm installer (`0.19.2-0`)
/// embeds the compiler (`0.19.2`). See Section 13.
let readElmCompilerVersion (repoRoot: string) : Result<ToolVersion, DevHostFailure> =
    let path = Path.Combine(repoRoot, "web", "elm.json")

    if not (File.Exists path) then
        Error(MissingAuthorityFile path)
    else
        try
            let text = File.ReadAllText path
            let m = Regex.Match(text, "\"elm-version\"\s*:\s*\"([0-9]+\.[0-9]+\.[0-9]+)\"")

            if not m.Success then
                Error(MalformedAuthorityFile path)
            else
                let raw = m.Groups.[1].Value

                match ToolVersion.parse raw with
                | Ok v -> Ok v
                | Error _ -> Error(MalformedAuthorityFile path)
        with _ ->
            Error(MalformedAuthorityFile path)

/// Read the Node.js version from `FROM node:<version>` in
/// `Dockerfile.frontend`. Fails closed when the file does not contain a
/// single, fully-qualified version.
let readNodeVersion (repoRoot: string) : Result<ToolVersion, DevHostFailure> =
    let path = Path.Combine(repoRoot, "Dockerfile.frontend")

    if not (File.Exists path) then
        Error(MissingAuthorityFile path)
    else
        try
            let text = File.ReadAllText path

            let m =
                Regex.Match(text, "^FROM\s+node:([0-9]+\.[0-9]+\.[0-9]+)", RegexOptions.Multiline)

            if not m.Success then
                Error(MalformedAuthorityFile path)
            else
                let raw = m.Groups.[1].Value

                match ToolVersion.parse raw with
                | Ok v -> Ok v
                | Error _ -> Error(MalformedAuthorityFile path)
        with _ ->
            Error(MalformedAuthorityFile path)

/// Read the version authority for an Elm-only test on doctor. We compare
/// only the `dependencies.direct['elm']` declared in `package.json` to the
/// *compiler* version we want.
let readElmPackageVersion (repoRoot: string) : Result<string, DevHostFailure> =
    let path = Path.Combine(repoRoot, "web", "package.json")

    if not (File.Exists path) then
        Error(MissingAuthorityFile path)
    else
        try
            let text = File.ReadAllText path
            let m = Regex.Match(text, "\"elm\"\s*:\s*\"([0-9]+\.[0-9]+\.[0-9]+-0)\"")

            if not m.Success then
                Error(MalformedAuthorityFile path)
            else
                Ok m.Groups.[1].Value
        with _ ->
            Error(MalformedAuthorityFile path)

/// Pure helper used by the doctor to drive the dependency venv version.
let readPolicyPipVersion (manifestJson: string) : Result<string, DevHostFailure> =
    let version =
        Regex.Match(manifestJson, "\"pip\"\s*:\s*\"([0-9]+\.[0-9]+(?:\.[0-9]+)?)\"")

    if version.Success then
        Ok version.Groups.[1].Value
    else
        Error(MalformedAuthorityFile "eng/devhost-toolchain.json")

let readPolicyPyYamlVersion (manifestJson: string) : Result<ToolVersion, DevHostFailure> =
    let m = Regex.Match(manifestJson, "\"pyyaml\"\s*:\s*\"([0-9]+\.[0-9]+\.[0-9]+)\"")

    if not m.Success then
        Error(MalformedAuthorityFile "eng/devhost-toolchain.json")
    else
        match ToolVersion.parse m.Groups.[1].Value with
        | Ok v -> Ok v
        | Error _ -> Error(MalformedAuthorityFile "eng/devhost-toolchain.json")

let readActionlintVersion (manifestJson: string) : Result<ToolVersion, DevHostFailure> =
    let m =
        Regex.Match(
            manifestJson,
            "\"actionlint\"\s*:.*?\"version\"\s*:\s*\"([0-9]+\.[0-9]+\.[0-9]+)\"",
            RegexOptions.Singleline
        )

    if not m.Success then
        Error(MalformedAuthorityFile "eng/devhost-toolchain.json")
    else
        match ToolVersion.parse m.Groups.[1].Value with
        | Ok v -> Ok v
        | Error _ -> Error(MalformedAuthorityFile "eng/devhost-toolchain.json")

let readShellCheckVersion (manifestJson: string) : Result<ToolVersion, DevHostFailure> =
    let m =
        Regex.Match(
            manifestJson,
            "\"shellcheck\"\s*:.*?\"version\"\s*:\s*\"([0-9]+\.[0-9]+\.[0-9]+)\"",
            RegexOptions.Singleline
        )

    if not m.Success then
        Error(MalformedAuthorityFile "eng/devhost-toolchain.json")
    else
        match ToolVersion.parse m.Groups.[1].Value with
        | Ok v -> Ok v
        | Error _ -> Error(MalformedAuthorityFile "eng/devhost-toolchain.json")

/// Compute the dirty state by counting lines returned by `git status
/// --porcelain=v1`. We never rely on the exit code alone.
let readDirtyLines (workingDirectory: string) (probe: string -> string list) : int =
    let lines = probe workingDirectory
    List.length lines

/// Pure helper used by tests to compute a deterministic dirty signal.
let classifyPorcelain (lines: string list) : bool = not (List.isEmpty lines)
