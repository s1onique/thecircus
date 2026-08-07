module Circus.Tooling.FSharpDiagnostics.AtomicPublish

// =============================================================================
// Atomic publication
// =============================================================================
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01-CORRECTION06B
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
//   write path and the canonical commit/rollback path.  No
//   environment-variable hooks, sleeps, chmod tricks, or global mutable
//   failure switches are present.
//
// Commit and rollback discipline (Correction06B):
//   1. Snapshot the canonical pair (file-exists + bytes) through the seam
//      so the caller can prove byte-identical restoration after a failure.
//   2. Replace candidate canonical file: ReplaceFile(staged, canonical,
//      backup) when the canonical file exists; MoveFile(staged, canonical)
//      when the canonical file is absent.
//   3. Replace summary canonical file: ReplaceFile(staged, canonical,
//      backup) when the canonical file exists; MoveFile(staged, canonical)
//      when the canonical file is absent.
//   4. On any failure during the commit, roll back each already-mutated
//      file using DeleteFile(new canonical) followed by MoveFile(backup,
//      canonical) when the previous canonical file was present, or
//      DeleteFile(new canonical) when the previous canonical file was
//      absent.
//
// Path discipline (Correction06B reviewer-verified):
//   - staging directory is a sibling of canonicalDir (same parent filesystem)
//   - backup file is a SIBLING of the canonical FILE (lives inside canonicalDir)
//   - canonical, staging, and backup are therefore all on the same
//     filesystem tree; `File.Replace` requires this for its atomic swap
//     semantics.  No staging or backup is placed under
//     `Path.GetTempPath()` or `/tmp`.

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

      /// True when the supplied path exists as a file.  Must not throw on
      /// a missing file.
      FileExists : string -> bool
      /// Move `source` to `destination`.  Must throw on failure.  Production
      /// default delegates to `File.Move`.  Used both for installing an
      /// absent canonical file from the staging directory and for
      /// restoring a backed-up canonical file during rollback.
      MoveFile : string -> string -> unit
      /// Atomically replace the contents of `destination` with the contents
      /// of `source`, creating a backup of the previous `destination` bytes
      /// at `backup`.  Production default delegates to `File.Replace`.  All
      /// three paths must reside on the same filesystem.
      ReplaceFile : string -> string -> string -> unit
      /// Delete the file at the supplied path.  Must throw on failure.
      /// Used during rollback to remove a newly-installed canonical file
      /// when the previous canonical file was absent.
      DeleteFile : string -> unit
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

      FileExists =
        fun (path: string) -> File.Exists(path)
      MoveFile =
        fun (source: string) (destination: string) ->
            File.Move(source, destination)
      ReplaceFile =
        fun (source: string) (destination: string) (backup: string) ->
            File.Replace(source, destination, backup)
      DeleteFile =
        fun (path: string) -> File.Delete(path)
    }

// -----------------------------------------------------------------------------
// Forward declarations
// -----------------------------------------------------------------------------

/// A unit of work: a logical filename and the bytes to write.
type PendingFile =
    { CanonicalFileName: string
      Body: string }

// -----------------------------------------------------------------------------
// Canonical pair snapshot
// -----------------------------------------------------------------------------

/// One canonical file observed before publication.  Absent files are
/// represented as `Absent`, NOT as zero-byte `Present` — distinguishing
/// "missing" from "explicitly empty" is required for rollback semantics.
type CanonicalFileSnapshot =
    | Absent
    | Present of byte[]

/// Snapshot of the canonical pair (candidate + summary) taken before any
/// canonical mutation.  Used to compute the rollback target when the
/// canonical commit phase fails partway through.
type CanonicalPairSnapshot =
    { Candidate : CanonicalFileSnapshot
      Summary : CanonicalFileSnapshot }

/// Capture a `CanonicalPairSnapshot` for the supplied canonical directory
/// using the filesystem seam.  Uses `ops.FileExists` to test for presence
/// and `ops.ReadAllBytes` to capture bytes.  The two filenames are taken
/// from the supplied `PendingFile` list; both must be supplied by the
/// caller.
let snapshotCanonicalPair
    (ops: AtomicPublishOps)
    (canonicalDir: string)
    (files: PendingFile list)
    : CanonicalPairSnapshot =
    let captureOne (filename: string) : CanonicalFileSnapshot =
        let fullPath = Path.Combine(canonicalDir, filename)

        if ops.FileExists fullPath then
            Present(ops.ReadAllBytes fullPath)
        else
            Absent

    match files with
    | [ c; s ] ->
        { Candidate = captureOne c.CanonicalFileName
          Summary = captureOne s.CanonicalFileName }
    | _ ->
        failwith
            (sprintf
                "snapshotCanonicalPair: canonical pair requires exactly two pending files, got %d"
                (List.length files))

// -----------------------------------------------------------------------------
// Typed failure model
// -----------------------------------------------------------------------------

/// Phases of the atomic publish path.  Known failures preserve the exact
/// phase in which they were observed.
[<RequireQualifiedAccess>]
type AtomicPublishPhase =
    // Pre-commit staging write path (Correction06A).
    | StageDirectory
    | StageOpen
    | StageWrite
    | StageFlush
    | StageVerify

    // Commit path (Correction06B).
    /// Capturing the pre-commit canonical pair snapshot.
    | Snapshot
    /// Reserved for any distinct backup operation.  The current
    /// implementation folds backup into the replacement primitive
    /// (`File.Replace` creates the backup as part of the replacement), so
    /// this phase is not currently used by a separate fault injection
    /// point.  It is kept in the phase DU so a future slice that exposes
    /// backup as a separate operation can carry the exact phase without
    /// reshaping the type.
    | Backup
    /// Atomic replacement of a canonical file with its staged bytes.
    | Install
    /// Deleting a newly installed canonical file during rollback when the
    /// previous canonical file was absent.
    | RollbackDelete
    /// Restoring a backed-up canonical file during rollback when the
    /// previous canonical file was present.
    | RollbackRestore

let atomicPublishPhaseToString (p: AtomicPublishPhase) : string =
    match p with
    | AtomicPublishPhase.StageDirectory -> "stage-directory"
    | AtomicPublishPhase.StageOpen -> "stage-open"
    | AtomicPublishPhase.StageWrite -> "stage-write"
    | AtomicPublishPhase.StageFlush -> "stage-flush"
    | AtomicPublishPhase.StageVerify -> "stage-verify"
    | AtomicPublishPhase.Snapshot -> "snapshot"
    | AtomicPublishPhase.Backup -> "backup"
    | AtomicPublishPhase.Install -> "install"
    | AtomicPublishPhase.RollbackDelete -> "rollback-delete"
    | AtomicPublishPhase.RollbackRestore -> "rollback-restore"

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
    | AtomicPublishPhase.Snapshot -> "snapshot"
    | AtomicPublishPhase.Backup -> "backup"
    | AtomicPublishPhase.Install -> "replace-or-move"
    | AtomicPublishPhase.RollbackDelete -> "rollback-delete"
    | AtomicPublishPhase.RollbackRestore -> "rollback-restore"

/// Typed publication failure preserving the exact phase, path, operation,
/// exception type, and detail.  Operation is always phase-specific.
type AtomicPublishFailure =
    { Phase: AtomicPublishPhase
      Path: string
      Operation: string
      ExceptionType: string
      Detail: string }

/// Recovery state of the canonical pair after publication.  This is the
/// typed equivalent of "is the canonical pair in its expected post-state
/// after we returned?"
///
///   - `NeverModified`         — the canonical pair is byte-identical to
///                                the pre-publication state.
///   - `RestoredByteIdentical` — a partial canonical mutation occurred and
///                                was rolled back; the canonical pair is
///                                byte-identical to the pre-publication
///                                state.
///   - `Committed`             — the canonical pair was successfully
///                                replaced with the staged bytes.
///
/// Rollback failure is not yet represented; it is reserved for
/// Correction06C and would be encoded as `MayHaveChanged`.
type AtomicRecoveryState =
    | NeverModified
    | RestoredByteIdentical
    | Committed
    /// The canonical pair was mutated and may not have been restored to its
    /// pre-publication bytes.  Returned when a commit failure triggered a
    /// rollback attempt but the post-rollback canonical bytes differ from the
    /// pre-snapshot, OR when the post-rollback snapshot could not be observed
    /// at all.  Introduced in Correction06B so a canonical mutation that is
    /// not provably restored cannot be mis-labelled NeverModified.
    | MayHaveChanged

/// Payload of a successful publication.
type AtomicPublishSuccess =
    {
      /// (filename * sha256) for each PendingFile that was successfully
      /// written and verified against disk bytes.
      OutputHashes: (string * string) list
      /// True when the canonical outputs were unchanged by this call
      /// (always true on Published).
      CanonicalByteIdenticalAfterFailure: bool
      /// Recovery state of the canonical pair after this call.
      RecoveryState: AtomicRecoveryState
    }

/// Payload of a failed publication.
type AtomicPublishFailureReport =
    {
      /// List of typed failures observed during the publish path.  Each
      /// failure preserves its phase.
      Failures: AtomicPublishFailure list
      /// True when no partial change was observed in the canonical
      /// root after the failure.
      CanonicalByteIdenticalAfterFailure: bool
      /// Path of the staging directory when it still exists on disk.
      RetainedStagingPath: string option
      /// Recovery state of the canonical pair after the failure and any
      /// rollback.  May be `NeverModified`, `RestoredByteIdentical`, or
      /// `MayHaveChanged`.  `MayHaveChanged` is reported when a commit
      /// failure triggered a rollback attempt whose post-state either
      /// differs from the pre-snapshot or could not be observed at all
      /// (e.g. the post-rollback snapshot failed).
      RecoveryState: AtomicRecoveryState
    }

/// Typed publication outcome.  Successful publication reports the SHA-256
/// hash of each canonical output.  Failed publication reports the typed
/// AtomicPublishFailure(s) observed during the publish path.
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

/// Compute the backup path for a canonical file.  Always a sibling of
/// the canonical file under the same parent directory so the rollback
/// restore is on the same filesystem.
let private computeBackupPath (canonicalPath: string) : string =
    canonicalPath + ".bak"

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

/// Decide whether the canonical post-state is byte-identical to the
/// pre-snapshot.  Used to populate the canonical-byte-identical flag on
/// the typed publication outcome.
let private canonicalBytesPreserved
    (preSnap: CanonicalPairSnapshot)
    (postSnap: CanonicalPairSnapshot)
    : bool =
    let eq (a: CanonicalFileSnapshot) (b: CanonicalFileSnapshot) : bool =
        match a, b with
        | Absent, Absent -> true
        | Present x, Present y ->
            if x.Length <> y.Length then false
            else
                let mutable i = 0
                let mutable ok = true

                while i < x.Length && ok do
                    if x.[i] <> y.[i] then
                        ok <- false

                    i <- i + 1

                ok
        | _ -> false

    eq preSnap.Candidate postSnap.Candidate && eq preSnap.Summary postSnap.Summary

// -----------------------------------------------------------------------------
// Stage-write path (pre-commit, owned by Correction06A)
// -----------------------------------------------------------------------------

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
// Commit and rollback (owned by Correction06B)
// -----------------------------------------------------------------------------

/// Result of attempting to replace one canonical file with its staged
/// replacement.
type private CommitStepResult =
    /// The canonical file was successfully replaced.  `BackupPath` is
    /// populated when the canonical file existed before replacement and
    /// was replaced via `ReplaceFile` (so it now holds the previous
    /// canonical bytes).
    | CommitSucceeded of PreSnapshot: CanonicalFileSnapshot * BackupPath: string option
    /// The canonical file could not be replaced.  `PreSnapshot` records
    /// the state of the canonical file before this commit attempt and is
    /// used to drive the rollback.
    | CommitFailed of Failure: AtomicPublishFailure * PreSnapshot: CanonicalFileSnapshot

/// Attempt to install one staged file into its canonical location using
/// the seam.  Captures the pre-mutation snapshot so the caller can drive
/// rollback on a later failure.  Throws are caught and reported as typed
/// failures.
let private commitOneFile
    (ops: AtomicPublishOps)
    (canonicalDir: string)
    (stagedPath: string)
    (canonicalName: string)
    : CommitStepResult =
    let canonicalPath = Path.Combine(canonicalDir, canonicalName)
    let backupPath = computeBackupPath canonicalPath

    let preSnapshot =
        if ops.FileExists canonicalPath then
            Present(ops.ReadAllBytes canonicalPath)
        else
            Absent

    try
        match preSnapshot with
        | Present _ ->
            ops.ReplaceFile stagedPath canonicalPath backupPath
            CommitSucceeded(preSnapshot, BackupPath = Some backupPath)
        | Absent ->
            ops.MoveFile stagedPath canonicalPath
            CommitSucceeded(preSnapshot, BackupPath = None)
    with ex ->
        CommitFailed(
            Failure = failureFromException AtomicPublishPhase.Install canonicalPath ex,
            PreSnapshot = preSnapshot)

/// Attempt to roll back a single previously-installed canonical file.
/// Drives the rollback through the seam and records the typed phase on
/// any rollback failure (rollback-failure injection is reserved for
/// Correction06C, but the typed reporting is in place now so the surface
/// is stable).
let private rollbackOneFile
    (ops: AtomicPublishOps)
    (canonicalDir: string)
    (canonicalName: string)
    (preSnapshot: CanonicalFileSnapshot)
    (backupPath: string option)
    (accumulatedFailures: ResizeArray<AtomicPublishFailure>)
    : unit =
    let canonicalPath = Path.Combine(canonicalDir, canonicalName)

    match preSnapshot with
    | Absent ->
        // Previous canonical was absent.  Rollback removes the
        // newly-installed file.
        if ops.FileExists canonicalPath then
            try
                ops.DeleteFile canonicalPath
            with ex ->
                accumulatedFailures.Add(
                    failureFromException AtomicPublishPhase.RollbackDelete canonicalPath ex
                )
    | Present _ ->
        // Previous canonical was present.  Rollback restores from the
        // backup that `ReplaceFile` (or our MoveFile+MoveFile sequence)
        // created.
        match backupPath with
        | Some bp ->
            try
                if ops.FileExists canonicalPath then
                    ops.DeleteFile canonicalPath
            with ex ->
                accumulatedFailures.Add(
                    failureFromException AtomicPublishPhase.RollbackDelete canonicalPath ex
                )

            // A missing backup is a rollback failure, not a no-op.  Surface
            // it as a typed RollbackRestore failure so the typed outcome never
            // claims successful recovery on a missing backup.
            if not (ops.FileExists bp) then
                accumulatedFailures.Add(
                    { Phase = AtomicPublishPhase.RollbackRestore
                      Path = canonicalPath
                      Operation = operationForPhase AtomicPublishPhase.RollbackRestore
                      ExceptionType = ""
                      Detail = "expected rollback backup is missing" }
                )
            else
                try
                    ops.MoveFile bp canonicalPath
                with ex ->
                    accumulatedFailures.Add(
                        failureFromException AtomicPublishPhase.RollbackRestore canonicalPath ex
                    )
        | None ->
            // No backup available (the canonical file was reported as
            // absent at install time but is now present).  Surface this
            // as a rollback failure.
            accumulatedFailures.Add(
                { Phase = AtomicPublishPhase.RollbackRestore
                  Path = canonicalPath
                  Operation = operationForPhase AtomicPublishPhase.RollbackRestore
                  ExceptionType = ""
                  Detail = "no backup path available for rollback restore" }
            )

/// Drive the commit phase for the canonical pair from a known staging
/// directory.  This is the production entry point used by
/// `publishWithDependencies`.
///
/// On success: every canonical file has been replaced.
///
/// On any failure during commit: each already-mutated file is rolled
/// back using its captured pre-snapshot.  Rollback failures are
/// accumulated into the returned failure list; the primary commit
/// failure remains the first element of the returned list.
///
/// Returns (commitOk, rollbackAttempted, failures).  `rollbackAttempted`
/// is true when at least one canonical file was mutated and then rolled
/// back.  When the failure occurs before any mutation, rollback is not
/// attempted and `rollbackAttempted` is false.
let private commitCanonicalPairFromStaging
    (ops: AtomicPublishOps)
    (canonicalDir: string)
    (stagingDir: string)
    (files: PendingFile list)
    : Result<unit, (AtomicPublishFailure list) * bool> =
    match files with
    | [ c; s ] ->
        let failures = ResizeArray<AtomicPublishFailure>()
        let rollbackFailures = ResizeArray<AtomicPublishFailure>()
        let mutable rollbackAttempted = false

        // 1) Commit candidate
        let candidateStaged = Path.Combine(stagingDir, c.CanonicalFileName)

        let candidateStep =
            commitOneFile ops canonicalDir candidateStaged c.CanonicalFileName

        let candidateCommitted, candidateSnapshot, candidateBackup =
            match candidateStep with
            | CommitSucceeded (preSnapshot, bp) -> true, preSnapshot, bp
            | CommitFailed(failure, pre) ->
                failures.Add failure
                false, pre, None

        // 2) Commit summary (only if candidate succeeded)
        if candidateCommitted then
            let summaryStaged = Path.Combine(stagingDir, s.CanonicalFileName)

            match commitOneFile ops canonicalDir summaryStaged s.CanonicalFileName with
            | CommitSucceeded _ ->
                // Both files succeeded.  No rollback needed.
                ()
            | CommitFailed(failure, _pre) ->
                failures.Add failure

                // Summary commit failed.  Roll back the candidate.
                rollbackAttempted <- true

                rollbackOneFile
                    ops
                    canonicalDir
                    c.CanonicalFileName
                    candidateSnapshot
                    candidateBackup
                    rollbackFailures

        // Surface rollback failures after the primary commit failure so
        // the typed outcome is never collapsed into a single string.
        if rollbackFailures.Count > 0 then
            for rf in rollbackFailures do
                failures.Add rf

        if failures.Count = 0 then
            Ok()
        else
            Error((failures |> Seq.toList), rollbackAttempted)
    | _ ->
        // Cardinality failure: the canonical-pair contract requires exactly
        // two pending files.  This must be raised before any staging or
        // canonical mutation so the caller cannot accidentally publish a
        // sub-pair or a super-pair.
        Error
            ( [ { Phase = AtomicPublishPhase.Install
                  Path = canonicalDir
                  Operation = "canonical-pair-cardinality"
                  ExceptionType = ""
                  Detail =
                    sprintf
                        "commitCanonicalPairFromStaging requires exactly two pending files, got %d"
                        (List.length files) } ],
              false )

// -----------------------------------------------------------------------------
// publishWithDependencies — staged write + commit + rollback through the seam
// -----------------------------------------------------------------------------

/// Publish the supplied files atomically into `canonicalDir` using the
/// supplied filesystem seam.  Every filesystem operation — staging,
/// snapshot, install, rollback — runs through `ops` so tests can observe
/// call sequencing and fault specific operations.
///
/// Pre-commit failures (`StageDirectory`, `StageOpen`, `StageWrite`,
/// `StageFlush`, `StageVerify`) are reported with the exact phase
/// preserved and the canonical outputs remain byte-identical to the
/// pre-snapshot.
///
/// Commit failures (`Install`, `RollbackDelete`, `RollbackRestore`) are
/// reported with the exact phase preserved.  When the candidate commit
/// succeeds but the summary commit fails, the candidate is rolled back
/// to its pre-mutation bytes so the canonical pair returns to its
/// pre-publication state.
let publishWithDependencies
    (ops: AtomicPublishOps)
    (canonicalDir: string)
    (files: PendingFile list)
    : AtomicPublishResult =

    // Cardinality check: the canonical-pair contract requires exactly two
    // pending files.  This must fail BEFORE any staging, snapshot, or
    // canonical I/O so the caller cannot accidentally publish a sub-pair
    // or a super-pair.
    if not (match files with [ _c; _s ] -> true | _ -> false) then
        AtomicPublishResult.Failed
            { Failures =
                [ { Phase = AtomicPublishPhase.Install
                    Path = canonicalDir
                    Operation = "canonical-pair-cardinality"
                    ExceptionType = ""
                    Detail =
                        sprintf
                            "publishWithDependencies requires exactly two pending files, got %d"
                            (List.length files) } ]
              CanonicalByteIdenticalAfterFailure = true
              RetainedStagingPath = None
              RecoveryState = AtomicRecoveryState.NeverModified }
    else

    let staging = computeStagingDir canonicalDir

    let _ = ()
    // (Continue below — the body of publishWithDependencies is the
    // post-cardinality-check flow.)



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
              RetainedStagingPath = None
              RecoveryState = AtomicRecoveryState.NeverModified }
    else
        // 1) Snapshot the canonical pair through the seam.  Any seam
        // failure here is reported as a Snapshot failure.
        let snapFailures = ResizeArray<AtomicPublishFailure>()

        let preSnapOpt =
            try
                Ok(snapshotCanonicalPair ops canonicalDir files)
            with ex ->
                snapFailures.Add(
                    failureFromException AtomicPublishPhase.Snapshot canonicalDir ex
                )
                Error()

        match preSnapOpt with
        | Error () ->
            AtomicPublishResult.Failed
                { Failures = List.ofSeq snapFailures
                  CanonicalByteIdenticalAfterFailure = true
                  RetainedStagingPath = None
                  RecoveryState = AtomicRecoveryState.NeverModified }
        | Ok preSnap ->
            // 2) Create staging directory
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
                      RetainedStagingPath = None
                      RecoveryState = AtomicRecoveryState.NeverModified }
            | None ->
                // 3) Stage each file via the seam
                let stagingFailures = ResizeArray<AtomicPublishFailure>()
                let hashes = ResizeArray<string * string>()

                let mutable continueStaging = true

                for f in files do
                    if continueStaging then
                        let fullPath = Path.Combine(staging, f.CanonicalFileName)

                        match stageFileWithDependencies ops fullPath f with
                        | Ok h ->
                            hashes.Add(f.CanonicalFileName, h)
                        | Error failure ->
                            stagingFailures.Add(failure)
                            // Stop staging the remaining files.
                            continueStaging <- false

                if not (stagingFailures.Count = 0) then
                    // Cleanup staging (best-effort).  Never masks the typed failure.
                    let _ = tryRemoveDir staging

                    AtomicPublishResult.Failed
                        { Failures = List.ofSeq stagingFailures
                          CanonicalByteIdenticalAfterFailure = true
                          RetainedStagingPath =
                            if Directory.Exists staging then Some staging else None
                          RecoveryState = AtomicRecoveryState.NeverModified }
                else
                    // 4) Commit the canonical pair through the seam.
                    match commitCanonicalPairFromStaging ops canonicalDir staging files with
                    | Ok () ->
                        let _ = tryRemoveDir staging

                        AtomicPublishResult.Published
                            { OutputHashes = List.ofSeq hashes
                              CanonicalByteIdenticalAfterFailure = true
                              RecoveryState = AtomicRecoveryState.Committed }
                    | Error (commitFailures, rollbackAttempted) ->
                        // Best-effort staging cleanup.  Never masks the
                        // typed commit failure.
                        let _ = tryRemoveDir staging

                        // Re-snapshot the canonical pair after any
                        // rollback to compute the canonical-byte-identical
                        // flag and the typed recovery state.
                        let postSnapResult =
                            try
                                Ok(snapshotCanonicalPair ops canonicalDir files)
                            with _ ->
                                Error()

                        // Post-rollback observation: when the post-rollback snapshot
                        // succeeded, compare byte-for-byte against the pre-snapshot.
                        // When it FAILED (postSnapResult = Error), we MUST NOT
                        // fabricate byte identity by falling back to preSnap; the
                        // only honest value for CanonicalByteIdenticalAfterFailure
                        // is `false` until a proper post-state observation succeeds.
                        let preserved =
                            match postSnapResult with
                            | Ok post -> canonicalBytesPreserved preSnap post
                            | Error () -> false
                        // When no rollback was attempted, the canonical
                        // state is unchanged (NeverModified).  When
                        // rollback was attempted and the bytes match the
                        // pre-snapshot, the canonical state was
                        // RestoredByteIdentical.
                        // Recovery-state semantics:
                        //   - rollback not attempted + canonical bytes
                        //     unchanged  -> NeverModified
                        //   - rollback attempted + canonical bytes
                        //     match pre-snapshot -> RestoredByteIdentical
                        //   - rollback attempted + canonical bytes differ
                        //     from pre-snapshot -> MayHaveChanged
                        //   - rollback attempted + post-rollback snapshot
                        //     could not be observed at all -> MayHaveChanged
                        // NeverModified MUST NOT be returned for any state
                        // that has been mutated, whether or not the
                        // mutation was successfully reverted.
                        let recoveryState =
                            match postSnapResult, rollbackAttempted with
                            | Error (), _ ->
                                // Post-rollback observation failed.  The
                                // canonical pair may have been mutated by
                                // the failed commit; we cannot truthfully
                                // claim NeverModified.
                                if rollbackAttempted then
                                    AtomicRecoveryState.MayHaveChanged
                                else
                                    AtomicRecoveryState.NeverModified
                            | Ok _, false ->
                                AtomicRecoveryState.NeverModified
                            | Ok post, true ->
                                if canonicalBytesPreserved preSnap post then
                                    AtomicRecoveryState.RestoredByteIdentical
                                else
                                    AtomicRecoveryState.MayHaveChanged

                        AtomicPublishResult.Failed
                            { Failures = commitFailures
                              CanonicalByteIdenticalAfterFailure = preserved
                              RetainedStagingPath =
                                if Directory.Exists staging then Some staging else None
                              RecoveryState = recoveryState }

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
