module Circus.DevHost.NodeInstaller

open System
open System.IO
open System.Threading

open Domain
open Circus.DevHost.Adapters
open Circus.DevHost.Archives
open Circus.DevHost.Downloads
open Circus.DevHost.ProcessRunner

/// Build the canonical download URLs and names for a specific Node.js
/// version. Pure.
let private urlsFor (version: ToolVersion) =
    let v = ToolVersion.value version
    let distBase = sprintf "https://nodejs.org/dist/v%s" v
    let archiveName = sprintf "node-v%s-linux-x64.tar.xz" v
    let shasumsUrl = distBase + "/SHASUMS256.txt"
    let archiveUrl = distBase + "/" + archiveName
    archiveName, archiveUrl, shasumsUrl

/// Parse SHASUMS256.txt and return the SHA-256 hash for `archiveName`.
let parseShasums (text: string) (archiveName: string) : Result<string, DevHostFailure> =
    let mutable matches: string list = []

    for line in text.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries) do
        if line.StartsWith "#" then
            ()
        else
            let parts =
                line.Trim().Split([| ' '; '\t' |], StringSplitOptions.RemoveEmptyEntries)

            if parts.Length = 2 && parts.[1] = archiveName then
                matches <- parts.[0] :: matches

    match List.length matches with
    | 0 -> Error(MalformedAuthorityFile("SHASUMS256.txt:" + archiveName))
    | 1 -> Ok(List.head matches)
    | _ -> Error(MalformedAuthorityFile("SHASUMS256.txt:duplicate:" + archiveName))

/// Verify the local Node binary reports the expected version.
let verifyNode
    (runner: IProcessRunner)
    (nodeDir: string)
    (expectedVersion: ToolVersion)
    : Result<string, DevHostFailure> =
    let nodeBin = Path.Combine(nodeDir, "bin", "node")

    if not (File.Exists nodeBin) then
        Error(MissingTool Node)
    else
        let spec =
            mkSpec nodeBin [ "--version" ] (Directory.GetCurrentDirectory()) Map.empty (TimeSpan.FromSeconds(20.0)) None

        match runSync runner spec with
        | Error e -> Error e
        | Ok r ->
            let actual = (r.StandardOutput).Trim().TrimStart('v')
            let expected = ToolVersion.value expectedVersion

            if actual = expected then
                Ok actual
            else
                Error(WrongToolVersion(Node, expected, actual))

/// Atomic install of Node.js.
let installNode
    (http: IHttp)
    (runner: IProcessRunner)
    (cacheDir: string)
    (destDir: string)
    (expectedVersion: ToolVersion)
    (force: bool)
    (cancellation: CancellationToken)
    : Async<Result<string, DevHostFailure>> =
    async {
        let existing =
            if force then
                None
            else
                match verifyNode runner destDir expectedVersion with
                | Ok actual -> Some actual
                | Error _ -> None

        match existing with
        | Some actual -> return Ok actual
        | None ->
            let archiveName, archiveUrl, shasumsUrl = urlsFor expectedVersion

            let tempShasums =
                Path.Combine(Path.GetTempPath(), "node-shasums-" + Guid.NewGuid().ToString("n") + ".txt")

            let tempArchive = Path.Combine(Path.GetTempPath(), archiveName)
            let! shasumsOutcome = http.Download(shasumsUrl, tempShasums, NoPayloadHash, cancellation)

            match shasumsOutcome with
            | Error failure ->
                try
                    if File.Exists tempShasums then
                        File.Delete tempShasums
                with _ ->
                    ()

                return Error failure
            | Ok _ ->
                let textOutcome =
                    try
                        Ok(File.ReadAllText tempShasums)
                    with ex ->
                        Error(DownloadFailure(shasumsUrl, ex.Message))

                try
                    if File.Exists tempShasums then
                        File.Delete tempShasums
                with _ ->
                    ()

                match textOutcome with
                | Error failure -> return Error failure
                | Ok shasumsText ->
                    match parseShasums shasumsText archiveName with
                    | Error failure -> return Error failure
                    | Ok expectedHash ->
                        let integrity = Sha256 expectedHash
                        let! downloadOutcome = http.Download(archiveUrl, tempArchive, integrity, cancellation)

                        match downloadOutcome with
                        | Error failure ->
                            try
                                if File.Exists tempArchive then
                                    File.Delete tempArchive
                            with _ ->
                                ()

                            return Error failure
                        | Ok _ ->
                            match cachePlace cacheDir archiveName integrity tempArchive with
                            | Error failure ->
                                try
                                    if File.Exists tempArchive then
                                        File.Delete tempArchive
                                with _ ->
                                    ()

                                return Error failure
                            | Ok cached ->
                                let extractResult =
                                    safeExtractVerified
                                        realDirectoryOperations
                                        runner
                                        cached
                                        destDir
                                        (fun candidateDir ->
                                            verifyNode runner candidateDir expectedVersion |> Result.map ignore)

                                try
                                    if File.Exists tempArchive then
                                        File.Delete tempArchive
                                with _ ->
                                    ()

                                match extractResult with
                                | Error failure -> return Error failure
                                | Ok _ -> return verifyNode runner destDir expectedVersion
    }

/// Plan the install action for `--dry-run` mode.
let planNode (fs: IFilesystem) (destDir: string) (expectedVersion: ToolVersion) : string =
    let nodeBin = Path.Combine(destDir, "bin", "node")

    if not (fs.IsFile nodeBin) then
        sprintf "install node v%s" (ToolVersion.value expectedVersion)
    else
        sprintf "verify node v%s" (ToolVersion.value expectedVersion)
