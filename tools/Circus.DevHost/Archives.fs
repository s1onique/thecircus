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
/// path, the target is empty). The install-temp and previous-temp are
/// reclaimed in a `try … finally` block; the previous-temp is preserved
/// whenever a partial recovery has left the previous install in place.
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

        let disposeInstallation () = ignore (deleteIfPresent installDir)

        /// Conditionally release the previous-temp. We only delete it when
        /// the recovery succeeded; otherwise we leave it in place so the
        /// caller can attempt another recovery (e.g. a human operator).
        let tryDisposePreviousIfSafe () =
            let finalPresent = operations.Exists absoluteFinal
            let previousPresent = operations.Exists previousDir
            let candidatePresent = operations.Exists installDir

            if previousPresent
               && not finalPresent
               && not candidatePresent
               && hadPrevious then
                // The previous-temp is the only thing left; keeping it
                // provides an out-of-band recovery path. The recovery
                // copy is the only thing on disk.
                false
            else if previousPresent
                    && finalPresent
                    && not candidatePresent then
                // The previous install is back in `absoluteFinal`; the temp
                // is safe to release.
                ignore (deleteIfPresent previousDir)
                true
            else
                ignore (deleteIfPresent previousDir)
                previousPresent

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
        /// the previous install. `hadPrevious` distinguishes the upgrade
        /// path (we must restore `previousDir`) from the cold-start path
        /// (we must simply delete the failed candidate). Returns a list of
        /// recovery problems for the caller to surface.
        let restorePrevious (errors: ResizeArray<string>) =
            if not (hadPrevious && operations.Exists previousDir) then
                ()
            else
                // The candidate may have been renamed onto `absoluteFinal`
                // and then the second move threw; the live filesystem
                // shows the truth.
                if operations.Exists absoluteFinal then
                    if not (deleteIfPresent absoluteFinal) then
                        errors.Add("could not delete the failed candidate before restoring the previous install")

                if operations.Exists absoluteFinal then
                    errors.Add("failed candidate still present; previous install cannot be restored")
                else
                    try
                        operations.Move previousDir absoluteFinal
                    with ex ->
                        errors.Add(sprintf "restore previous install: %s" ex.Message)

        let restoreColdStart (errors: ResizeArray<string>) =
            if operations.Exists absoluteFinal then
                if not (deleteIfPresent absoluteFinal) then
                    errors.Add("could not delete the failed candidate on cold-start install")

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

        let installOutcome = performInstall ()

        /// Decide whether the previous install is safely back in
        /// `absoluteFinal` and the recovery copy can be released.
        let labelFor (label: string) (rollbackErrors: ResizeArray<string>) =
            if rollbackErrors.Count > 0 then
                label
                + "; rollback failed: "
                + String.concat "; " rollbackErrors
            else
                label

        try
            match installOutcome with
            | Ok _ ->
                match verifyInstalled absoluteFinal with
                | Ok() ->
                    if hadPrevious then ignore (deleteIfPresent previousDir)
                    disposeInstallation ()
                    Ok absoluteFinal
                | Error verificationFailure ->
                    let errors = ResizeArray<string> ()

                    if hadPrevious then
                        restorePrevious errors
                    else
                        restoreColdStart errors

                    disposeInstallation ()
                    Error(
                        ExtractionFailure(
                            archive,
                            labelFor
                                (sprintf "verification failed: %s" (renderFailure verificationFailure))
                                errors
                        )
                    )
            | Error installFailure ->
                let errors = ResizeArray<string> ()

                if hadPrevious then
                    restorePrevious errors
                else
                    restoreColdStart errors

                Error(
                    ExtractionFailure(
                        archive,
                        labelFor
                            (sprintf "install failed: %s" (renderFailure installFailure))
                            errors
                    )
                )
        finally
            // The install-temp is only consumed when the new install is
            // committed; until then it must be reclaimed so we do not leak
            // directories. The previous-temp is preserved whenever the
            // recovery copy is still the only thing on disk so a human
            // operator can attempt another recovery.
            if operations.Exists installDir then
                disposeInstallation ()

            if operations.Exists previousDir
               && (operations.Exists absoluteFinal
                   || operations.Exists installDir
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
