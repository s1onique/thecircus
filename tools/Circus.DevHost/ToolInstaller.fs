module Circus.DevHost.ToolInstaller

open System
open System.IO
open System.Threading

open Domain
open Circus.DevHost.Adapters
open Circus.DevHost.Archives
open Circus.DevHost.Downloads
open Circus.DevHost.ProcessRunner

/// Parse the `actionlint -version` output.
let normalizeActionlintOutput (text: string) : string =
    let first = text.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries) |> Array.tryHead
    match first with
    | Some s -> ToolVersion.normalize (s.Trim())
    | None -> ""

/// Parse the `shellcheck --version` output.
let parseShellCheckVersion (text: string) : string =
    for line in text.Split([| '\n'; '\r' |]) do
        let parts = line.Split([| ": " |], 2, StringSplitOptions.None)
        if parts.Length = 2 && parts.[0].Trim() = "version" then
            return parts.[1].Trim()
    return ""

/// One-target install plan entry.
type ToolPlanEntry = {
    Name: string
    Action: string
}

/// Verify the actionlint binary reports the expected version.
let verifyActionlint
    (runner: IProcessRunner)
    (binPath: string)
    (expected: ToolVersion)
    : Result<string, DevHostFailure> =
    if not (File.Exists binPath) then Error(MissingTool Actionlint)
    else
        let spec = mkSpec binPath [ "-version" ] (Directory.GetCurrentDirectory()) Map.empty (TimeSpan.FromSeconds(15.0)) None
        match runSync runner spec with
        | Error e -> Error e
        | Ok r ->
            let actual = normalizeActionlintOutput r.StandardOutput
            if actual = ToolVersion.value expected then Ok actual
            else Error(WrongToolVersion(Actionlint, ToolVersion.value expected, actual))

/// Verify the ShellCheck binary reports the expected version.
let verifyShellCheck
    (runner: IProcessRunner)
    (binPath: string)
    (expected: ToolVersion)
    : Result<string, DevHostFailure> =
    if not (File.Exists binPath) then Error(MissingTool ShellCheck)
    else
        let spec = mkSpec binPath [ "--version" ] (Directory.GetCurrentDirectory()) Map.empty (TimeSpan.FromSeconds(15.0)) None
        match runSync runner spec with
        | Error e -> Error e
        | Ok r ->
            let actual = parseShellCheckVersion r.StandardOutput
            if actual = ToolVersion.value expected then Ok actual
            else Error(WrongToolVersion(ShellCheck, ToolVersion.value expected, actual))

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
        | Error _ -> ()
        let archiveName = Path.GetFileName(Uri url.OriginalString)
        let tempPath = Path.Combine(Path.GetTempDirectory(), archiveName)
        let! dlOutcome = http.Download(url, tempPath, sha256, cancellation)
        match dlOutcome with
        | Error e ->
            (try if File.Exists tempPath then File.Delete tempPath with _ -> ())
            return Error e
        | Ok _ ->
            match cachePlace cacheDir archiveName sha256 tempPath with
            | Error e ->
                (try if File.Exists tempPath then File.Delete tempPath with _ -> ())
                return Error e
            | Ok cached ->
                let extractRoot = Path.Combine(tmpDir, toolKind.ToString().ToLowerInvariant())
                if not (Directory.Exists extractRoot) then
                    Directory.CreateDirectory extractRoot |> ignore
                let extractResult = safeExtract runner cached extractRoot
                (try if File.Exists tempPath then File.Delete tempPath with _ -> ())
                match extractResult with
                | Error e -> return Error e
                | Ok _ ->
                    let candidates =
                        Directory.GetFiles(extractRoot, binaryName, SearchOption.AllDirectories)
                    if Array.isEmpty candidates then
                        return Error(LayoutWrong(extractRoot, sprintf "missing %s" binaryName))
                    let source = candidates.[0]
                    fs.CreateDirectory binDir
                    let dest = Path.Combine(binDir, binaryName)
                    if File.Exists dest then
                        try File.Delete dest with _ -> ()
                    File.Copy(source, dest)
                    File.SetUnixFileMode(dest,
                        UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                        ||| UnixFileMode.GroupRead ||| UnixFileMode.GroupExecute
                        ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherExecute)
                    verify ()
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
        http runner fs cacheDir tmpDir binDir url sha256
        "actionlint" Actionlint
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
        http runner fs cacheDir tmpDir binDir url sha256
        "shellcheck" ShellCheck
        (fun () -> verifyShellCheck runner (Path.Combine(binDir, "shellcheck")) expected)
        cancellation

/// Compose the plan entry for an individual tool used by --dry-run.
let planToolInstall (binaryName: string) (version: ToolVersion) : ToolPlanEntry =
    { Name = binaryName; Action = sprintf "install %s v%s" binaryName (ToolVersion.value version) }
