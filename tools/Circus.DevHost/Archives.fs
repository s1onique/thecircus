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
    if fileName.EndsWith ".tar.xz" || fileName.EndsWith ".txz" then
        TarXz
    elif fileName.EndsWith ".tar.gz" || fileName.EndsWith ".tgz" then
        TarGz
    elif fileName.EndsWith ".zip" then
        Zip
    else
        RawBinary

let isInside (root: string) (target: string) : bool =
    let normalizedTarget = Path.GetFullPath(target).Replace("\\", "/")
    let normalizedRoot = Path.GetFullPath(root).Replace("\\", "/")

    let prefix =
        if normalizedRoot.EndsWith "/" then
            normalizedRoot
        else
            normalizedRoot + "/"

    normalizedTarget.StartsWith(prefix, StringComparison.Ordinal)

type ArchiveListing = { Members: string list }

let private listingFromResult
    (outcome: Result<ProcessResult, DevHostFailure>)
    : Result<ArchiveListing, DevHostFailure> =
    outcome
    |> Result.map (fun result ->
        { Members =
            result.StandardOutput.Split([| '\n'; '\r' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.toList })

let listTarball (runner: IProcessRunner) (archive: string) : Result<ArchiveListing, DevHostFailure> =
    let compressionFlag =
        match classifyArchive archive with
        | TarXz -> "-tJf"
        | TarGz -> "-tzf"
        | _ -> "-tf"

    mkSpec
        "tar"
        [ compressionFlag; archive ]
        (Directory.GetCurrentDirectory())
        Map.empty
        (TimeSpan.FromSeconds 60.0)
        None
    |> runSync runner
    |> listingFromResult

let listZip (runner: IProcessRunner) (archive: string) : Result<ArchiveListing, DevHostFailure> =
    mkSpec "unzip" [ "-Z1"; archive ] (Directory.GetCurrentDirectory()) Map.empty (TimeSpan.FromSeconds 60.0) None
    |> runSync runner
    |> listingFromResult

let hasTraversal (memberPath: string) : bool =
    let normalized = memberPath.Replace("\\", "/")

    normalized.StartsWith "/"
    || (normalized.Split('/', StringSplitOptions.RemoveEmptyEntries)
        |> Array.exists ((=) ".."))

let rejectIfTraversal (destinationRoot: string) (listing: ArchiveListing) : Result<unit, DevHostFailure> =
    listing.Members
    |> List.tryFind (fun memberPath ->
        hasTraversal memberPath
        || not (
            isInside
                destinationRoot
                (Path.Combine(destinationRoot, memberPath.Replace("/", string Path.DirectorySeparatorChar)))
        ))
    |> function
        | Some memberPath -> Error(ExtractionFailure(destinationRoot, sprintf "traversal: %s" memberPath))
        | None -> Ok()

let private extractTar
    (runner: IProcessRunner)
    (flag: string)
    (archive: string)
    (destinationRoot: string)
    : Result<unit, DevHostFailure> =
    if not (File.Exists archive) then
        Error(ExtractionFailure(archive, "archive missing"))
    else
        mkSpec
            "tar"
            [ flag; archive; "-C"; destinationRoot; "--strip-components=1" ]
            (Directory.GetCurrentDirectory())
            Map.empty
            (TimeSpan.FromMinutes 10.0)
            None
        |> runSync runner
        |> Result.map ignore

let extractTarXz (runner: IProcessRunner) (archive: string) (destinationRoot: string) : Result<unit, DevHostFailure> =
    extractTar runner "-xJf" archive destinationRoot

let extractTarGz (runner: IProcessRunner) (archive: string) (destinationRoot: string) : Result<unit, DevHostFailure> =
    extractTar runner "-xzf" archive destinationRoot

/// Directory operations are injectable so rollback behavior can be exercised
/// without relying on platform-specific permission tricks.
type DirectoryOperations =
    { Exists: string -> bool
      Create: string -> unit
      Move: string -> string -> unit
      Delete: string -> unit }

let realDirectoryOperations: DirectoryOperations =
    { Exists = Directory.Exists
      Create = fun path -> Directory.CreateDirectory path |> ignore
      Move = fun source destination -> Directory.Move(source, destination)
      Delete =
        fun path ->
            if Directory.Exists path then
                Directory.Delete(path, true) }

let private extractionOutcome
    (runner: IProcessRunner)
    (archive: string)
    (destinationRoot: string)
    : Result<unit, DevHostFailure> =
    match classifyArchive archive with
    | TarXz -> extractTarXz runner archive destinationRoot
    | TarGz -> extractTarGz runner archive destinationRoot
    | kind -> Error(ExtractionFailure(archive, sprintf "unsupported archive format %A" kind))

/// Extract to a sibling directory, swap it into place, verify the new tree,
/// and restore the previous tree if either the move or verification fails.
/// Every recovery step inspects the live filesystem rather than post-call
/// flags, so an exception that is raised after a side-effect still leaves
/// the previous installation reachable. We never delete the previous
/// install unless we have already restored it (or, for the cold-start
/// path, the target is empty). The `try … finally` block reclaims the
/// install-temp and (only when the recovery copy is no longer the
/// authoritative fallback) the previous-temp.
let extractAtomicWith
    (operations: DirectoryOperations)
    (runner: IProcessRunner)
    (archive: string)
    (finalDir: string)
    (verifyInstalled: string -> Result<unit, DevHostFailure>)
    : Result<string, DevHostFailure> =
    let absoluteFinal = Path.GetFullPath finalDir
    let parentValue = Path.GetDirectoryName absoluteFinal

    if String.IsNullOrEmpty parentValue then
        Error(ExtractionFailure(archive, "destination has no parent directory"))
    else
        let parent = parentValue

        let installDir =
            Path.Combine(parent, ".circus-install-" + Guid.NewGuid().ToString("n"))

        let previousDir =
            Path.Combine(parent, ".circus-previous-" + Guid.NewGuid().ToString("n"))

        let hadPrevious = operations.Exists absoluteFinal

        let deleteIfPresent path =
            try
                if operations.Exists path then
                    operations.Delete path
                    true
                else
                    false
            with _ ->
                false

        let moveOrFail (source: string) (destination: string) =
            try
                operations.Move source destination
                Ok()
            with ex ->
                Error(
                    ExtractionFailure(
                        archive,
                        sprintf "move '%s' to '%s' failed: %s" source destination ex.Message
                    )
                )

        /// Inspect the live filesystem and bring `absoluteFinal` back to
        /// the previous install. Returns a recovery report that records
        /// whether the previous install is back in place, whether the
        /// recovery copy is still in `previousDir`, and any human-readable
        /// problems.
        let restorePrevious () =
            let mutable notes = ResizeArray<string> ()
            let mutable previousRecovered = false
            let mutable previousPreserved = false

            if not (hadPrevious && operations.Exists previousDir) then
                ()
            else
                let candidatePresent = operations.Exists absoluteFinal

                if candidatePresent then
                    if not (deleteIfPresent absoluteFinal) then
                        notes.Add("could not delete the failed candidate before restoring the previous install")

                if operations.Exists absoluteFinal then
                    notes.Add("failed candidate still present; previous install cannot be restored")
                else
                    try
                        operations.Move previousDir absoluteFinal
                        if not (operations.Exists previousDir) then
                            previousRecovered <- true
                    with ex ->
                        notes.Add(sprintf "restore previous install: %s" ex.Message)

            if operations.Exists previousDir then
                previousPreserved <- true

            (notes, previousRecovered, previousPreserved)

        let restoreColdStart () =
            let mutable notes = ResizeArray<string> ()

            if operations.Exists absoluteFinal then
                if not (deleteIfPresent absoluteFinal) then
                    notes.Add("could not delete the failed candidate on cold-start install")

            notes

        let performInstall () =
            try
                operations.Create parent
                operations.Create installDir

                match extractionOutcome runner archive installDir with
                | Error failure -> Error failure
                | Ok() ->
                    if hadPrevious then
                        match moveOrFail absoluteFinal previousDir with
                        | Error failure -> Error failure
                        | Ok() ->
                            match moveOrFail installDir absoluteFinal with
                            | Error failure -> Error failure
                            | Ok() -> Ok absoluteFinal
                    else
                        match moveOrFail installDir absoluteFinal with
                        | Error failure -> Error failure
                        | Ok() -> Ok absoluteFinal
            with ex ->
                Error(ExtractionFailure(archive, ex.Message))

        let labelFor (label: string) (notes: ResizeArray<string>) =
            if notes.Count > 0 then
                label + "; rollback notes: " + String.concat "; " notes
            else
                label

        let reportOutcome
            (label: string)
            (notes: ResizeArray<string>)
            (previousRecovered: bool)
            (previousPreserved: bool)
            : Result<string, DevHostFailure> =
            if hadPrevious
               && not previousRecovered
               && previousPreserved
               && operations.Exists absoluteFinal then
                Error(
                    ExtractionFailure(
                        archive,
                        labelFor
                            (sprintf
                                 "rollback incomplete; previous installation retained at %s"
                                 previousDir)
                            notes
                    )
                )
            else
                Error(ExtractionFailure(archive, labelFor label notes))

        try
            match performInstall () with
            | Ok _ ->
                match verifyInstalled absoluteFinal with
                | Ok() ->
                    // Successful verification: the new install is committed
                    // and the previous-temp can be released unconditionally.
                    if hadPrevious then
                        ignore (deleteIfPresent previousDir)

                    ignore (deleteIfPresent installDir)
                    Ok absoluteFinal
                | Error verificationFailure ->
                    let notes, previousRecovered, previousPreserved =
                        restorePrevious ()

                    ignore (deleteIfPresent installDir)
                    reportOutcome
                        (sprintf "verification failed: %s" (renderFailure verificationFailure))
                        notes
                        previousRecovered
                        previousPreserved
            | Error installFailure ->
                let notes, previousRecovered, previousPreserved =
                    restorePrevious ()

                reportOutcome
                    (sprintf "install failed: %s" (renderFailure installFailure))
                    notes
                    previousRecovered
                    previousPreserved
        finally
            // The install-temp is only consumed when the new install is
            // committed; until then it must be reclaimed so we do not leak
            // directories.
            if operations.Exists installDir then
                ignore (deleteIfPresent installDir)

            // The previous-temp is the only recovery copy on disk after a
            // failed install. It is safe to release only when the new
            // install is committed or when the cold-start path was taken.
            if operations.Exists previousDir
               && ((operations.Exists absoluteFinal && hadPrevious)
                   || not hadPrevious) then
                ignore (deleteIfPresent previousDir)

let extractAtomic (runner: IProcessRunner) (archive: string) (finalDir: string) : Result<string, DevHostFailure> =
    extractAtomicWith realDirectoryOperations runner archive finalDir (fun _ -> Ok())

let safeExtractVerified
    (operations: DirectoryOperations)
    (runner: IProcessRunner)
    (archive: string)
    (finalDir: string)
    (verifyInstalled: string -> Result<unit, DevHostFailure>)
    : Result<string, DevHostFailure> =
    let listingOutcome =
        match classifyArchive archive with
        | TarXz
        | TarGz -> listTarball runner archive
        | Zip -> listZip runner archive
        | RawBinary -> Ok { Members = [] }

    match listingOutcome with
    | Error failure -> Error failure
    | Ok listing ->
        rejectIfTraversal finalDir listing
        |> Result.bind (fun () -> extractAtomicWith operations runner archive finalDir verifyInstalled)

let safeExtract (runner: IProcessRunner) (archive: string) (finalDir: string) : Result<string, DevHostFailure> =
    safeExtractVerified realDirectoryOperations runner archive finalDir (fun _ -> Ok())
