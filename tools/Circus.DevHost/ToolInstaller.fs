module Circus.DevHost.ToolInstaller

open System
open System.IO
open System.Threading

open Domain
open Circus.DevHost.Adapters
open Circus.DevHost.Archives
open Circus.DevHost.Downloads
open Circus.DevHost.ProcessRunner

/// Parse actionlint's version from either `1.7.12`, `v1.7.12`, or a
/// descriptive line such as `actionlint 1.7.12`.
let normalizeActionlintOutput (text: string) : string =
    let version =
        System.Text.RegularExpressions.Regex.Match(text, "(?:^|[^0-9])v?([0-9]+\\.[0-9]+\\.[0-9]+)(?:$|[^0-9])")

    if version.Success then version.Groups.[1].Value else ""

/// Parse the explicit `version:` field from ShellCheck's multi-line output.
let parseShellCheckVersion (text: string) : string =
    text.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
    |> Array.tryPick (fun line ->
        let separator = line.IndexOf ':'

        if separator > 0 && line.Substring(0, separator).Trim() = "version" then
            Some(line.Substring(separator + 1).Trim())
        else
            None)
    |> Option.defaultValue ""

/// One-target install plan entry.
type ToolPlanEntry = { Name: string; Action: string }

/// Verify the actionlint binary reports the expected version.
let verifyActionlint
    (runner: IProcessRunner)
    (binPath: string)
    (expected: ToolVersion)
    : Result<string, DevHostFailure> =
    if not (File.Exists binPath) then
        Error(MissingTool Actionlint)
    else
        let spec =
            mkSpec binPath [ "-version" ] (Directory.GetCurrentDirectory()) Map.empty (TimeSpan.FromSeconds(15.0)) None

        match runSync runner spec with
        | Error e -> Error e
        | Ok r ->
            let actual = normalizeActionlintOutput r.StandardOutput

            if actual = ToolVersion.value expected then
                Ok actual
            else
                Error(WrongToolVersion(Actionlint, ToolVersion.value expected, actual))

/// Verify the ShellCheck binary reports the expected version.
let verifyShellCheck
    (runner: IProcessRunner)
    (binPath: string)
    (expected: ToolVersion)
    : Result<string, DevHostFailure> =
    if not (File.Exists binPath) then
        Error(MissingTool ShellCheck)
    else
        let spec =
            mkSpec binPath [ "--version" ] (Directory.GetCurrentDirectory()) Map.empty (TimeSpan.FromSeconds(15.0)) None

        match runSync runner spec with
        | Error e -> Error e
        | Ok r ->
            let actual = parseShellCheckVersion r.StandardOutput

            if actual = ToolVersion.value expected then
                Ok actual
            else
                Error(WrongToolVersion(ShellCheck, ToolVersion.value expected, actual))

/// Generic single-binary installer: download, verify, extract, copy.
let installSingle
    (http: IHttp)
    (runner: IProcessRunner)
    (fs: IFilesystem)
    (cacheDir: string)
    (tmpDir: string)
    (binDir: string)
    (url: string)
    (sha256: string)
    (binaryName: string)
    (toolKind: Tool)
    (verify: unit -> Result<string, DevHostFailure>)
    (cancellation: CancellationToken)
    : Async<Result<string, DevHostFailure>> =
    async {
        match verify () with
        | Ok actual -> return Ok actual
        | Error _ ->
            let archiveName = Path.GetFileName((Uri url).AbsolutePath)
            let tempPath = Path.Combine(Path.GetTempPath(), archiveName)
            let integrity = Sha256 sha256
            let! downloadOutcome = http.Download(url, tempPath, integrity, cancellation)

            match downloadOutcome with
            | Error failure ->
                try
                    if File.Exists tempPath then
                        File.Delete tempPath
                with _ ->
                    ()

                return Error failure
            | Ok _ ->
                match cachePlace cacheDir archiveName integrity tempPath with
                | Error failure ->
                    try
                        if File.Exists tempPath then
                            File.Delete tempPath
                    with _ ->
                        ()

                    return Error failure
                | Ok cached ->
                    let extractRoot = Path.Combine(tmpDir, toolKind.ToString().ToLowerInvariant())
                    Directory.CreateDirectory extractRoot |> ignore
                    let extractResult = safeExtract runner cached extractRoot

                    try
                        if File.Exists tempPath then
                            File.Delete tempPath
                    with _ ->
                        ()

                    match extractResult with
                    | Error failure -> return Error failure
                    | Ok _ ->
                        let candidates =
                            Directory.GetFiles(extractRoot, binaryName, SearchOption.AllDirectories)

                        if Array.isEmpty candidates then
                            return Error(LayoutWrong(extractRoot, sprintf "missing %s" binaryName))
                        else
                            let source = candidates.[0]
                            fs.CreateDirectory binDir
                            let destination = Path.Combine(binDir, binaryName)

                            if File.Exists destination then
                                File.Delete destination

                            File.Copy(source, destination)
                            fs.MakeExecutable destination
                            return verify ()
    }

/// Compose the actionlint installer.
let installActionlint
    (http: IHttp)
    (runner: IProcessRunner)
    (fs: IFilesystem)
    (cacheDir: string)
    (tmpDir: string)
    (binDir: string)
    (url: string)
    (sha256: string)
    (expected: ToolVersion)
    (cancellation: CancellationToken)
    : Async<Result<string, DevHostFailure>> =
    installSingle
        http
        runner
        fs
        cacheDir
        tmpDir
        binDir
        url
        sha256
        "actionlint"
        Actionlint
        (fun () -> verifyActionlint runner (Path.Combine(binDir, "actionlint")) expected)
        cancellation

/// Compose the ShellCheck installer.
let installShellCheck
    (http: IHttp)
    (runner: IProcessRunner)
    (fs: IFilesystem)
    (cacheDir: string)
    (tmpDir: string)
    (binDir: string)
    (url: string)
    (sha256: string)
    (expected: ToolVersion)
    (cancellation: CancellationToken)
    : Async<Result<string, DevHostFailure>> =
    installSingle
        http
        runner
        fs
        cacheDir
        tmpDir
        binDir
        url
        sha256
        "shellcheck"
        ShellCheck
        (fun () -> verifyShellCheck runner (Path.Combine(binDir, "shellcheck")) expected)
        cancellation

/// Compose the plan entry for an individual tool used by --dry-run.
let planToolInstall (binaryName: string) (version: ToolVersion) : ToolPlanEntry =
    { Name = binaryName
      Action = sprintf "install %s v%s" binaryName (ToolVersion.value version) }
