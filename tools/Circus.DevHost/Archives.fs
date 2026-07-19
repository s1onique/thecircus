module Circus.DevHost.Archives

open System
open System.IO

open Domain
open Circus.DevHost.ProcessRunner

type ArchiveKind =
    | TarXz
    | TarGz
    | Zip
    | RawBinary

let classifyArchive (fileName: string) : ArchiveKind =
    if fileName.EndsWith ".tar.xz" || fileName.EndsWith ".txz" then TarXz
    elif fileName.EndsWith ".tar.gz" || fileName.EndsWith ".tgz" then TarGz
    elif fileName.EndsWith ".zip" then Zip
    else RawBinary

let isInside (root: string) (target: string) : bool =
    let norm = Path.GetFullPath(target).Replace("\\", "/")
    let basePath = Path.GetFullPath(root).Replace("\\", "/")
    let trimmedBase = if basePath.EndsWith "/" then basePath else basePath + "/"
    norm.StartsWith(trimmedBase, StringComparison.Ordinal)

type ArchiveListing = { Members: string list }

let listTarball
    (runner: IProcessRunner)
    (archive: string)
    : Result<ArchiveListing, DevHostFailure> =
    let spec = mkSpec "tar" [ "-tJf"; archive ] (Directory.GetCurrentDirectory()) Map.empty (TimeSpan.FromSeconds(60.0)) None
    match runSync runner spec with
    | Error e -> Error e
    | Ok r ->
        let members =
            r.StandardOutput.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.toList
        Ok { Members = members }

let listZip
    (runner: IProcessRunner)
    (archive: string)
    : Result<ArchiveListing, DevHostFailure> =
    let spec = mkSpec "unzip" [ "-Z1"; archive ] (Directory.GetCurrentDirectory()) Map.empty (TimeSpan.FromSeconds(60.0)) None
    match runSync runner spec with
    | Error e -> Error e
    | Ok r ->
        let members =
            r.StandardOutput.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.toList
        Ok { Members = members }

let hasTraversal (memberPath: string) : bool =
    let trimmed = memberPath.Replace("\\", "/")
    if trimmed.StartsWith "/" then true
    elif trimmed.Contains "../" then true
    elif trimmed = ".." || trimmed.StartsWith "../" then true
    else false

let rejectIfTraversal
    (destinationRoot: string)
    (listing: ArchiveListing)
    : Result<unit, DevHostFailure> =
    let mutable bad : string option = None
    for m in listing.Members do
        if hasTraversal m then
            bad <- Some m
        else
            let combined = Path.Combine(destinationRoot, m.Replace("/", Path.DirectorySeparatorChar.ToString()))
            if not (isInside destinationRoot combined) then
                bad <- Some m
    match bad with
    | Some m -> Error(ExtractionFailure(destinationRoot, sprintf "traversal: %s" m))
    | None -> Ok ()

let extractTarXz
    (runner: IProcessRunner)
    (archive: string)
    (destinationRoot: string)
    : Result<unit, DevHostFailure> =
    let spec = mkSpec "tar" [ "-xJf"; archive; "-C"; destinationRoot; "--strip-components=1" ] (Directory.GetCurrentDirectory()) Map.empty (TimeSpan.FromMinutes(10.0)) None
    match runSync runner spec with
    | Ok _ -> Ok ()
    | Error e ->
        if not (File.Exists archive) then Error(ExtractionFailure(archive, "archive missing"))
        else Error e

let extractTarGz
    (runner: IProcessRunner)
    (archive: string)
    (destinationRoot: string)
    : Result<unit, DevHostFailure> =
    let spec = mkSpec "tar" [ "-xzf"; archive; "-C"; destinationRoot; "--strip-components=1" ] (Directory.GetCurrentDirectory()) Map.empty (TimeSpan.FromMinutes(10.0)) None
    match runSync runner spec with
    | Ok _ -> Ok ()
    | Error e ->
        if not (File.Exists archive) then Error(ExtractionFailure(archive, "archive missing"))
        else Error e

let extractAtomic
    (runner: IProcessRunner)
    (archive: string)
    (finalDir: string)
    : Result<string, DevHostFailure> =
    try
        if not (Directory.Exists finalDir) then
            Directory.CreateDirectory(finalDir) |> ignore
        let tempDir = Path.Combine(finalDir, ".extract-" + Guid.NewGuid().ToString("n"))
        Directory.CreateDirectory tempDir |> ignore
        let kind = classifyArchive archive
        let extractOutcome =
            match kind with
            | TarXz -> extractTarXz runner archive tempDir
            | TarGz -> extractTarGz runner archive tempDir
            | _ -> Error(ExtractionFailure(archive, sprintf "unsupported archive format %A" kind))
        match extractOutcome with
        | Error e ->
            try Directory.Delete(tempDir, true) with _ -> ()
            Error e
        | Ok () ->
            let mutable succeeded = false
            if Directory.Exists finalDir then
                let previousDir = Path.Combine(Path.GetDirectoryName(finalDir), ".previous-" + Guid.NewGuid().ToString("n"))
                let _ =
                    try
                        Directory.Move(finalDir, previousDir)
                        Directory.Move(tempDir, finalDir)
                        try Directory.Delete(previousDir, true) with _ -> ()
                        true
                    with _ -> false
                succeeded <- true
            else
                let _ =
                    try Directory.Move(tempDir, finalDir); true
                    with _ -> false
                succeeded <- true
            if succeeded then Ok finalDir
            else Error(ExtractionFailure(archive, "atomic swap failed"))
    with ex ->
        Error(ExtractionFailure(archive, ex.Message))

let safeExtract
    (runner: IProcessRunner)
    (archive: string)
    (finalDir: string)
    : Result<string, DevHostFailure> =
    let kind = classifyArchive archive
    let listingResult =
        match kind with
        | TarXz -> listTarball runner archive
        | TarGz -> listTarball runner archive
        | Zip -> listZip runner archive
        | RawBinary -> Ok { Members = [] }
    match listingResult with
    | Error e -> Error e
    | Ok listing ->
        match rejectIfTraversal finalDir listing with
        | Error e -> Error e
        | Ok () ->
            extractAtomic runner archive finalDir
