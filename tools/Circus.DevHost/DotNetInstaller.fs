module Circus.DevHost.DotNetInstaller

open System
open System.IO
open System.Text.Json
open System.Threading

open Domain
open Circus.DevHost.Adapters
open Circus.DevHost.Archives
open Circus.DevHost.Downloads
open Circus.DevHost.ProcessRunner

/// Planned or executed action for a single install.
type InstallAction =
    | NoAction
    | InstallOf of string
    | ReplaceOf of string
    | VerifyInstall of string

/// The URL used to resolve exact versions without invoking
/// `dotnet-install.sh`. We deliberately avoid shelling out for the
/// metadata query so the bootstrap flow is testable and reproducible.
let channelReleaseUrl =
    "https://dotnetcli.azureedge.net/dotnet/release-metadata/10.0/releases.json"

/// Fetch the release-metadata JSON. Pure side effect: writes to a temp
/// file, reads it, deletes it.
let fetchChannelReleaseJson (http: IHttp) (cancellation: CancellationToken) : Async<Result<string, DevHostFailure>> =
    async {
        let tempPath =
            Path.Combine(Path.GetTempPath(), "dotnet-releases-" + Guid.NewGuid().ToString("n") + ".json")

        let dl = http.Download(channelReleaseUrl, tempPath, NoPayloadHash, cancellation)
        let! result = dl

        match result with
        | Error e ->
            try
                if File.Exists tempPath then
                    File.Delete tempPath
            with _ ->
                ()

            return Error e
        | Ok _ ->
            try
                let text = File.ReadAllText tempPath

                try
                    File.Delete tempPath
                with _ ->
                    ()

                return Ok text
            with ex ->
                return Error(DownloadFailure(channelReleaseUrl, ex.Message))
    }

/// Resolves the SDK tarball URL and SHA-512 hash for a given target SDK version.
let private pickSdkRelease (json: string) (version: ToolVersion) : Result<string * string, DevHostFailure> =
    try
        use doc = JsonDocument.Parse(json)
        let root = doc.RootElement
        let releases = root.GetProperty("releases")
        let target = ToolVersion.value version
        let mutable matchElement: JsonElement option = None

        for r in releases.EnumerateArray() do
            match matchElement with
            | None ->
                let sdkVersion = r.GetProperty("sdk").GetProperty("version").GetString()

                if sdkVersion = target then
                    matchElement <- Some r
            | Some _ -> ()

        match matchElement with
        | Some el ->
            let files = el.GetProperty("sdk").GetProperty("files")
            let mutable chosenUrl = ""
            let mutable chosenHash = ""

            for f in files.EnumerateArray() do
                let rid = f.GetProperty("rid").GetString()
                let name = f.GetProperty("name").GetString()

                if rid = "linux-x64" && not (isNull name) && name.EndsWith(".tar.gz") then
                    let url = f.GetProperty("url").GetString()
                    let hash = f.GetProperty("hash").GetString()

                    if not (String.IsNullOrEmpty url) then
                        chosenUrl <- url

                    if not (String.IsNullOrEmpty hash) then
                        chosenHash <- hash

            if String.IsNullOrEmpty chosenUrl || String.IsNullOrEmpty chosenHash then
                Error(MalformedAuthorityFile "release-metadata:linux-x64 URL/hash missing")
            else
                Ok(chosenUrl, chosenHash)
        | None -> Error(MalformedAuthorityFile("release-metadata:no sdk " + target))
    with ex ->
        Error(DownloadFailure("dotnet-release-metadata", ex.Message))

/// Compute the planned action for `.NET SDK`. Used by `--dry-run`.
let planDotnet (fs: IFilesystem) (expectedVersion: ToolVersion) (dotnetRoot: string) : InstallAction =
    let sdk = Path.Combine(dotnetRoot, "dotnet")

    if not (fs.IsFile sdk) then
        InstallOf(ToolVersion.value expectedVersion)
    else
        VerifyInstall(ToolVersion.value expectedVersion)

/// Build a ProcessSpec for `dotnet --version`.
let private dotnetVersionSpec (dotnetRoot: string) =
    let dotnetBin = Path.Combine(dotnetRoot, "dotnet")
    let spec = ProcessRunner.spec dotnetBin
    let spec = spec.WithArguments [ "--version" ]
    let spec = spec.WithEnvironment("DOTNET_ROOT", dotnetRoot)
    let spec = spec.WithTimeout(TimeSpan.FromSeconds(30.0))
    spec.Build()

/// Verify that the installed `dotnet --version` matches the expected.
let verifyDotnet
    (runner: IProcessRunner)
    (dotnetRoot: string)
    (expectedVersion: ToolVersion)
    : Result<string, DevHostFailure> =
    let dotnetBin = Path.Combine(dotnetRoot, "dotnet")

    if not (File.Exists dotnetBin) then
        Error(MissingTool DotNetSdk)
    else
        let spec = dotnetVersionSpec dotnetRoot

        match ProcessRunner.runSync runner spec with
        | Error e -> Error e
        | Ok r ->
            let actual = (r.StandardOutput).Trim()

            if actual = ToolVersion.value expectedVersion then
                Ok actual
            else
                Error(WrongToolVersion(DotNetSdk, ToolVersion.value expectedVersion, actual))

/// Atomic install of the .NET SDK into `dotnetRoot`.
let installDotnet
    (http: IHttp)
    (runner: IProcessRunner)
    (dotnetRoot: string)
    (expectedVersion: ToolVersion)
    (force: bool)
    (cacheDir: string)
    (cancellation: CancellationToken)
    : Async<Result<string, DevHostFailure>> =
    async {
        let existing =
            if force then
                None
            else
                match verifyDotnet runner dotnetRoot expectedVersion with
                | Ok actual -> Some actual
                | Error _ -> None

        match existing with
        | Some actual -> return Ok actual
        | None ->
            let! metadataOutcome = fetchChannelReleaseJson http cancellation

            match metadataOutcome with
            | Error failure -> return Error failure
            | Ok json ->
                match pickSdkRelease json expectedVersion with
                | Error failure -> return Error failure
                | Ok(url, expectedHash) ->
                    let archiveName = Path.GetFileName((Uri url).AbsolutePath)
                    let tempPath = Path.Combine(Path.GetTempPath(), archiveName)
                    let integrity = Sha512 expectedHash
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
                            let extractResult =
                                safeExtractVerified
                                    realDirectoryOperations
                                    runner
                                    cached
                                    dotnetRoot
                                    (fun candidateRoot ->
                                        verifyDotnet runner candidateRoot expectedVersion |> Result.map ignore)

                            try
                                if File.Exists tempPath then
                                    File.Delete tempPath
                            with _ ->
                                ()

                            match extractResult with
                            | Error failure -> return Error failure
                            | Ok _ -> return verifyDotnet runner dotnetRoot expectedVersion
    }

/// Detect whether `dotnet fsi` produces the "F# toolchain OK" string.
let fsInteractiveWorks (runner: IProcessRunner) (dotnetRoot: string) : Result<unit, DevHostFailure> =
    let dotnetBin = Path.Combine(dotnetRoot, "dotnet")
    let spec = ProcessRunner.spec dotnetBin
    let spec = spec.WithArguments [ "fsi"; "--quiet" ]
    let spec = spec.WithEnvironment("DOTNET_ROOT", dotnetRoot)
    let spec = spec.WithStandardInput "printfn \"F# toolchain OK\"\n;;"
    let spec = spec.WithTimeout(TimeSpan.FromSeconds(30.0))
    let finalSpec = spec.Build()

    match ProcessRunner.runSync runner finalSpec with
    | Ok r when r.StandardOutput.Contains("F# toolchain OK") -> Ok()
    | Ok _ -> Error(VerificationFailure "fsi did not echo expected token")
    | Error e -> Error e

let fetchDotnetReleaseIndex = fetchChannelReleaseJson
