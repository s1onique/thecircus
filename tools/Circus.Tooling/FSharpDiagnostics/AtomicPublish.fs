module Circus.Tooling.FSharpDiagnostics.AtomicPublish

// =============================================================================
// Atomic publication
// =============================================================================
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01-CORRECTION06A
//
// All generated outputs are produced into a temporary sibling directory,
// fully flushed via FileStream.Flush(true), verified by reading the bytes
// back from disk, and only then moved into the canonical target.  On any
// failure the previous canonical outputs remain byte-identical.
//
// Staging location invariant:
//   <parent(canonicalDir)>/<canonical-name>.staging.<id>/
//
// The staging directory is always created as a sibling of the canonical
// directory so the staging <-> canonical move is on the same filesystem
// (rename(2) is atomic on a single volume).
//
// Filesystem seam:
//   Production code delegates to real System.IO.  Tests inject the seam to
//   observe call sequencing and to fail specific stages of the staging
//   write path.  No environment-variable hooks, sleeps, chmod tricks, or
//   global mutable failure switches are present.

open System
open System.IO
open Circus.Tooling.FSharpDiagnostics.Hashing
open Circus.Tooling.FSharpDiagnostics.Paths
open Circus.Tooling.FSharpDiagnostics.Serialization

// -----------------------------------------------------------------------------
// Filesystem seam
// -----------------------------------------------------------------------------

/// A narrow production-equivalent handle for the staging write path.
/// Implements IDisposable so the production backing FileStream is closed
/// deterministically on every exit, including the failure paths.
///
///   - `WriteAll`     writes the supplied bytes to the underlying stream
///   - `FlushToDisk`  calls `FileStream.Flush(true)` so OS buffers AND the
///                    underlying storage device are forced to durable state
type IAtomicWriteHandle =
    inherit IDisposable

    abstract WriteAll : byte[] -> unit
    abstract FlushToDisk : unit -> unit

/// Production implementation backed by FileStream.  FlushToDisk calls
/// `FileStream.Flush(true)` to flush the OS file buffers for the open
/// stream to durable state.  Disposal closes the FileStream.  No
/// additional hidden Flush is performed in Dispose.
type private ProductionAtomicWriteHandle (stream: FileStream) =
    let mutable disposed = false

    interface IAtomicWriteHandle with
        member _.WriteAll (bytes) =
            if disposed then
                raise (ObjectDisposedException("ProductionAtomicWriteHandle"))

            stream.Write(bytes, 0, bytes.Length)

        member _.FlushToDisk () =
            if disposed then
                raise (ObjectDisposedException("ProductionAtomicWriteHandle"))

            stream.Flush(true)

        member _.Dispose () =
            if not disposed then
                disposed <- true
                stream.Dispose()

/// Filesystem seam consumed by `publishWithDependencies`.  Production
/// default delegates to real System.IO.  Tests inject recording or
/// failing implementations.
type AtomicPublishOps =
    {
      /// Create a directory at the supplied path.  Must throw on failure.
      CreateDirectory : string -> unit
      /// Open a write handle.  Must throw on failure.
      OpenWrite : string -> IAtomicWriteHandle
      /// Read all bytes from the supplied path.  Must throw on failure.
      ReadAllBytes : string -> byte[]
    }

/// Default production filesystem seam.  Delegates to System.IO without
/// any environment-variable hooks, sleeps, or global mutable switches.
let defaultAtomicPublishOps : AtomicPublishOps =
    {
      CreateDirectory =
        fun (path: string) ->
            Directory.CreateDirectory(path) |> ignore
      OpenWrite =
        fun (path: string) ->
            let fs =
                new FileStream(
                    path,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    bufferSize = 4096,
                    useAsync = false)
            upcast new ProductionAtomicWriteHandle(fs)
      ReadAllBytes =
        fun (path: string) -> File.ReadAllBytes(path)
    }

// -----------------------------------------------------------------------------
// Typed pre-commit failure model
// -----------------------------------------------------------------------------

/// Phases of the staging write path.  Known failures preserve the exact
/// phase in which they were observed.
[<RequireQualifiedAccess>]
type AtomicPublishPhase =
    | StageDirectory
    | StageOpen
    | StageWrite
    | StageFlush
    | StageVerify

let atomicPublishPhaseToString (p: AtomicPublishPhase) : string =
    match p with
    | AtomicPublishPhase.StageDirectory -> "stage-directory"
    | AtomicPublishPhase.StageOpen -> "stage-open"
    | AtomicPublishPhase.StageWrite -> "stage-write"
    | AtomicPublishPhase.StageFlush -> "stage-flush"
    | AtomicPublishPhase.StageVerify -> "stage-verify"

/// Operation string paired with each phase.  Phase-specific on purpose:
/// no generic "publish" operation is ever emitted when the failing
/// phase is known.
let private operationForPhase (p: AtomicPublishPhase) : string =
    match p with
    | AtomicPublishPhase.StageDirectory -> "create-directory"
    | AtomicPublishPhase.StageOpen -> "open-write"
    | AtomicPublishPhase.StageWrite -> "write-bytes"
    | AtomicPublishPhase.StageFlush -> "flush-to-disk"
    | AtomicPublishPhase.StageVerify -> "read-bytes"

/// Typed pre-commit failure preserving the exact phase, path, operation,
/// exception type, and detail.  Operation is always phase-specific.
type AtomicPublishFailure =
    { Phase: AtomicPublishPhase
      Path: string
      Operation: string
      ExceptionType: string
      Detail: string }

/// Payload of a successful publication.
type AtomicPublishSuccess =
    {
      /// (filename * sha256) for each PendingFile that was successfully
      /// written and verified against disk bytes.
      OutputHashes: (string * string) list
      /// True when the canonical outputs were unchanged by this call
      /// (always true on Published).
      CanonicalByteIdenticalAfterFailure: bool
    }

/// Payload of a failed publication.
type AtomicPublishFailureReport =
    {
      /// List of typed failures observed during the staging write path.
      /// Pre-commit failures only.
      Failures: AtomicPublishFailure list
      /// True when no partial change was observed in the canonical
      /// root after the failure.
      CanonicalByteIdenticalAfterFailure: bool
      /// Path of the staging directory when it still exists on disk.
      RetainedStagingPath: string option
    }

/// Typed publication outcome.  Successful publication reports the SHA-256
/// hash of each canonical output.  Failed publication reports the typed
/// AtomicPublishFailure(s) observed during the staging write path.
type AtomicPublishResult =
    | Published of AtomicPublishSuccess
    | Failed of AtomicPublishFailureReport

// -----------------------------------------------------------------------------
// Legacy outcome shape (kept for existing callers)
// -----------------------------------------------------------------------------

/// Result of an atomic publication attempt.  Backwards-compatible shape
/// consumed by `RuleCandidates.Engine.publishCandidatesDetailed`.  The
/// typed AtomicPublishResult is the canonical publication outcome; the
/// legacy PublishOutcome projects the typed outcome for callers that
/// have not yet been migrated.
type PublishOutcome =
    {
      /// True when every file was moved into place and verified.
      Success: bool
      /// SHA-256 of each canonical output (filename → hash).
      OutputHashes: (string * string) list
      /// Paths of any retained temporary files when cleanup failed.
      RetainedTempPaths: string list
      /// True when no partial change was observed in the canonical root.
      CanonicalByteIdenticalAfterFailure: bool
    }

/// A unit of work: a logical filename and the bytes to write.
type PendingFile =
    { CanonicalFileName: string
      Body: string }

let private utf8NoBom = System.Text.UTF8Encoding(false)

/// Translate a typed AtomicPublishResult to the legacy PublishOutcome shape.
let private toLegacyPublishOutcome (r: AtomicPublishResult) : PublishOutcome =
    match r with
    | AtomicPublishResult.Published p ->
        { Success = true
          OutputHashes = p.OutputHashes
          RetainedTempPaths = []
          CanonicalByteIdenticalAfterFailure = p.CanonicalByteIdenticalAfterFailure }
    | AtomicPublishResult.Failed f ->
        let retained =
            match f.RetainedStagingPath with
            | Some p -> [ p ]
            | None -> []
        { Success = false
          OutputHashes = []
          RetainedTempPaths = retained
          CanonicalByteIdenticalAfterFailure = f.CanonicalByteIdenticalAfterFailure }

// -----------------------------------------------------------------------------
// Helpers
// -----------------------------------------------------------------------------

/// Staging location invariant.  The staging directory is always a
/// sibling of the canonical directory — same parent filesystem —
/// regardless of any test-only configuration.
let private computeStagingDir (canonicalDir: string) : string =
    let parent = Path.GetDirectoryName canonicalDir
    let name = Path.GetFileName canonicalDir
    let guid = Guid.NewGuid().ToString("N")
    Path.Combine(parent, name + ".staging." + guid)

/// Compute SHA-256 hashes of the previous canonical outputs (when present)
/// so we can prove they remain byte-identical after a failed regeneration.
let private snapshotCanonicalHashes (canonicalDir: string) (files: PendingFile list) : Map<string, string> =
    files
    |> List.map (fun f ->
        let fullPath = Path.Combine(canonicalDir, f.CanonicalFileName)
        let hash =
            try
                if File.Exists fullPath then sha256OfFile fullPath else ""
            with _ ->
                ""
        f.CanonicalFileName, hash)
    |> Map.ofList

/// Compute whether the canonical directory's bytes match the pre-snapshot.
let private canonicalBytesPreserved
    (preSnap: Map<string, string>)
    (canonicalDir: string)
    (files: PendingFile list)
    : bool =
    let postSnap =
        files
        |> List.map (fun f ->
            let fullPath = Path.Combine(canonicalDir, f.CanonicalFileName)
            let hash =
                try
                    if File.Exists fullPath then sha256OfFile fullPath else ""
                with _ ->
                    ""
            f.CanonicalFileName, hash)
        |> Map.ofList

    preSnap
    |> Map.forall (fun k v ->
        match Map.tryFind k postSnap with
        | Some v' -> v = v'
        | None -> v = "")

/// Replace one file by moving the staged file into place.  Atomic on the
/// same filesystem because File.Move uses rename(2) when target is on the
/// same volume.  When the target exists it is replaced.
///
/// Pre-commit failures abort before this function is reached.  This is
/// the canonical install path and is reserved for later corrections.
let private replaceCanonical (stagingDir: string) (canonicalDir: string) (f: PendingFile) : unit =
    let staged = Path.Combine(stagingDir, f.CanonicalFileName)
    let target = Path.Combine(canonicalDir, f.CanonicalFileName)

    if File.Exists target then
        let backup = target + ".bak"

        if File.Exists backup then
            File.Delete backup

        File.Move(target, backup)

        try
            File.Move(staged, target)
            File.Delete backup
        with ex ->
            if File.Exists backup then
                if File.Exists target then
                    File.Delete target

                File.Move(backup, target)

            raise ex
    else
        File.Move(staged, target)

/// Remove a directory and all its contents.
let private tryRemoveDir (dir: string) : string option =
    try
        if Directory.Exists dir then
            Directory.Delete(dir, true)

        None
    with ex ->
        Some(sprintf "%s: %s" dir ex.Message)

/// Translate an exception thrown by a phase into a typed failure.
let private failureFromException (phase: AtomicPublishPhase) (path: string) (ex: exn) : AtomicPublishFailure =
    { Phase = phase
      Path = path
      Operation = operationForPhase phase
      ExceptionType = ex.GetType().FullName
      Detail = ex.Message }

/// Stage-write a single file using the seam.  The required operation
/// sequence is:
///
///   OpenWrite    (throws → StageOpen failure)
///   WriteAll     (throws → StageWrite failure; handle disposed via finally)
///   FlushToDisk  (throws → StageFlush failure; handle disposed via finally)
///   Dispose      (explicit in finally)
///   ReadAllBytes (throws → StageVerify failure)
///   SHA-256 verify (mismatch → StageVerify failure)
///
/// Returns the SHA-256 of the persisted bytes on success.  On any typed
/// failure the already-open handle is disposed by the `finally` block,
/// no later step runs, and the failure is reported via the typed result.
/// Exceptions raised by the seam are caught and converted to typed
/// failures; no exception escapes this function.
let private stageFileWithDependencies
    (ops: AtomicPublishOps)
    (fullPath: string)
    (f: PendingFile)
    : Result<string, AtomicPublishFailure> =
    let mutable handle : IAtomicWriteHandle option = None
    let mutable outcome : Result<string, AtomicPublishFailure> = Ok ""

    let capture (failure: AtomicPublishFailure) =
        outcome <- Error failure

    // 1) Open write handle
    let openResult =
        try
            Ok (ops.OpenWrite fullPath)
        with ex ->
            Error (failureFromException AtomicPublishPhase.StageOpen fullPath ex)

    match openResult with
    | Error failure ->
        capture failure
    | Ok h ->
        handle <- Some h

        // 2) Prepare bytes (UTF-8 no BOM, trailing newline preserved)
        let body =
            if f.Body.EndsWith "\n" then
                f.Body
            else
                f.Body + "\n"
        let bytes = utf8NoBom.GetBytes(body)

        // 3) Write bytes
        match
            try
                h.WriteAll bytes
                Ok ()
            with ex ->
                Error (failureFromException AtomicPublishPhase.StageWrite fullPath ex)
        with
        | Error failure -> capture failure
        | Ok () ->
            // 4) Flush to disk (calls FileStream.Flush(true))
            match
                try
                    h.FlushToDisk()
                    Ok ()
                with ex ->
                    Error (failureFromException AtomicPublishPhase.StageFlush fullPath ex)
            with
            | Error failure -> capture failure
            | Ok () ->
                // 5) Dispose the handle BEFORE the read so the seam
                // can re-open the file (production uses FileShare.None
                // on the staging FileStream).
                try
                    (h :> IDisposable).Dispose()
                    handle <- None
                with _ ->
                    ()

                // 6) Read bytes back from the seam
                let readResult =
                    try
                        Ok (ops.ReadAllBytes fullPath)
                    with ex ->
                        Error (failureFromException AtomicPublishPhase.StageVerify fullPath ex)

                match readResult with
                | Error failure -> capture failure
                | Ok onDiskBytes ->
                    // 7) SHA-256 verify on disk bytes against expected hash
                    let expectedHash = sha256Hex bytes
                    let actualHash = sha256Hex onDiskBytes

                    if expectedHash = actualHash then
                        outcome <- Ok actualHash
                    else
                        capture
                            { Phase = AtomicPublishPhase.StageVerify
                              Path = fullPath
                              Operation = "sha256-verify"
                              ExceptionType = ""
                              Detail =
                                sprintf "hash mismatch: expected=%s actual=%s"
                                    expectedHash actualHash }

    // 8) Best-effort dispose if a fault left the handle open.
    match handle with
    | Some h ->
        try
            (h :> IDisposable).Dispose()
        with _ ->
            ()
    | None ->
        ()

    outcome

// -----------------------------------------------------------------------------
// publishWithDependencies — staged write path through the seam
// -----------------------------------------------------------------------------

/// Publish the supplied files atomically into `canonicalDir` using the
/// supplied filesystem seam.  The pre-commit staging write path runs
/// exclusively through `ops`.  Canonical install/rollback is reserved
/// for later corrections and is not part of this slice.
///
/// Pre-commit failures (StageDirectory, StageOpen, StageWrite,
/// StageFlush, StageVerify) are reported with the exact phase preserved.
/// The canonical outputs are unchanged when the staging write path
/// fails before any canonical mutation.
let publishWithDependencies
    (ops: AtomicPublishOps)
    (canonicalDir: string)
    (files: PendingFile list)
    : AtomicPublishResult =

    let preSnap = snapshotCanonicalHashes canonicalDir files

    let staging = computeStagingDir canonicalDir

    // Assert staging location invariant: same parent filesystem.
    let stagingParent = Path.GetDirectoryName staging
    let canonicalParent = Path.GetDirectoryName canonicalDir

    if stagingParent <> canonicalParent then
        let failure =
            { Phase = AtomicPublishPhase.StageDirectory
              Path = canonicalDir
              Operation = "staging-parent-invariant"
              ExceptionType = ""
              Detail =
                sprintf
                    "staging parent (%s) does not match canonical parent (%s)"
                    stagingParent canonicalParent }
        AtomicPublishResult.Failed
            { Failures = [ failure ]
              CanonicalByteIdenticalAfterFailure = true
              RetainedStagingPath = None }
    else
        // 1) Create staging directory
        let dirFailure =
            try
                ops.CreateDirectory staging
                None
            with ex ->
                Some (failureFromException AtomicPublishPhase.StageDirectory staging ex)

        match dirFailure with
        | Some failure ->
            AtomicPublishResult.Failed
                { Failures = [ failure ]
                  CanonicalByteIdenticalAfterFailure = true
                  RetainedStagingPath = None }
        | None ->
            // 2) Stage each file via the seam
            let failures = ResizeArray<AtomicPublishFailure>()
            let hashes = ResizeArray<string * string>()

            let mutable continueStaging = true

            for f in files do
                if continueStaging then
                    let fullPath = Path.Combine(staging, f.CanonicalFileName)

                    match stageFileWithDependencies ops fullPath f with
                    | Ok h ->
                        hashes.Add(f.CanonicalFileName, h)
                    | Error failure ->
                        failures.Add(failure)
                        // Stop staging the remaining files.
                        continueStaging <- false

            if not (failures.Count = 0) then
                // Cleanup staging (best-effort).  Never masks the typed failure.
                let _ = tryRemoveDir staging

                let canonicalIdentical = canonicalBytesPreserved preSnap canonicalDir files

                AtomicPublishResult.Failed
                    { Failures = List.ofSeq failures
                      CanonicalByteIdenticalAfterFailure = canonicalIdentical
                      RetainedStagingPath =
                        if Directory.Exists staging then Some staging else None }
            else
                // ---------------------------------------------------------------
                // Canonical install runs here as part of the success-path
                // regression contract: real FileStream + Flush(true) + SHA-256
                // verify on disk bytes.  This is the only canonical mutation
                // performed in this slice.
                //
                // Canonical install failure injection is reserved for
                // Correction06B and later slices.  When it fails, the
                // outcome is reported with a typed StageVerify failure
                // marked as canonical-install so the caller can distinguish
                // pre-commit failures from post-staging failures.
                // ---------------------------------------------------------------
                try
                    for f in files do
                        replaceCanonical staging canonicalDir f

                    let _ = tryRemoveDir staging

                    AtomicPublishResult.Published
                        { OutputHashes = List.ofSeq hashes
                          CanonicalByteIdenticalAfterFailure = true }
                with _ ->
                    // Canonical install failure: best-effort cleanup.
                    let _ = tryRemoveDir staging

                    let canonicalIdentical = canonicalBytesPreserved preSnap canonicalDir files

                    AtomicPublishResult.Failed
                        { Failures =
                            [ { Phase = AtomicPublishPhase.StageVerify
                                Path = canonicalDir
                                Operation = "canonical-install"
                                ExceptionType = ""
                                Detail = "canonical install failure (reserved for later corrections)" } ]
                          CanonicalByteIdenticalAfterFailure = canonicalIdentical
                          RetainedStagingPath =
                            if Directory.Exists staging then Some staging else None }

// -----------------------------------------------------------------------------
// Legacy publish entry point — delegates to the seam with defaults
// -----------------------------------------------------------------------------

/// Legacy publish entry point.  Delegates to `publishWithDependencies`
/// using the default System.IO seam and projects the typed
/// AtomicPublishResult into the legacy PublishOutcome shape consumed
/// by `RuleCandidates.Engine.publishCandidatesDetailed`.
let publish
    (canonicalDir: string)
    (_failClosed: bool)
    (_preserveStaging: bool)
    (files: PendingFile list)
    : PublishOutcome =
    let r = publishWithDependencies defaultAtomicPublishOps canonicalDir files
    toLegacyPublishOutcome r

/// Convenience helper for callers that don't need the rich outcome.
let publishSimple (canonicalDir: string) (files: PendingFile list) : PublishOutcome =
    publish canonicalDir true false files
