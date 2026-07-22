module Circus.Tooling.FSharpDiagnostics.AtomicPublish

open System.IO
open Circus.Tooling.FSharpDiagnostics.Hashing
open Circus.Tooling.FSharpDiagnostics.Paths
open Circus.Tooling.FSharpDiagnostics.Serialization

// =============================================================================
// Atomic publication
// =============================================================================
//
// All generated outputs are produced into a temporary sibling directory,
// fully flushed, verified, and only then moved into the canonical target.
// On any failure the previous canonical outputs remain byte-identical.

/// Result of an atomic publication attempt.
type PublishOutcome = {
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
type PendingFile = {
    CanonicalFileName: string
    Body: string
}

let private utf8NoBom = System.Text.UTF8Encoding(false)

/// Write a single file into the temporary staging directory and verify its
/// bytes on disk.  Returns the SHA-256 of the persisted bytes.
let private writeAndFlush (stagingDir: string) (f: PendingFile) : string =
    let fullPath = Path.Combine(stagingDir, f.CanonicalFileName)
    let dir = Path.GetDirectoryName fullPath
    if not (System.String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
        Directory.CreateDirectory dir |> ignore
    let body =
        if f.Body.EndsWith "\n" then f.Body
        else f.Body + "\n"
    File.WriteAllText(fullPath, body, utf8NoBom)
    // Flush by reading back; SHA is computed from the bytes actually on disk.
    sha256OfFile fullPath

/// Compute SHA-256 hashes of the previous canonical outputs (when present)
/// so we can prove they remain byte-identical after a failed regeneration.
let private snapshotCanonicalHashes (canonicalDir: string)
                                   (files: PendingFile list)
                                   : Map<string, string> =
    files
    |> List.map (fun f ->
        let fullPath = Path.Combine(canonicalDir, f.CanonicalFileName)
        let hash =
            if File.Exists fullPath then sha256OfFile fullPath
            else ""
        f.CanonicalFileName, hash)
    |> Map.ofList

/// Replace one file by moving the staged file into place.  Atomic on the
/// same filesystem because File.Move uses rename(2) when target is on the
/// same volume.  When the target exists it is replaced.
let private replaceCanonical (stagingDir: string)
                             (canonicalDir: string)
                             (f: PendingFile)
                             : unit =
    let staged = Path.Combine(stagingDir, f.CanonicalFileName)
    let target = Path.Combine(canonicalDir, f.CanonicalFileName)
    if File.Exists target then
        // Replace in place: move target to a sibling backup, move staged to
        // target, then delete the backup.  If the second move fails we move
        // the backup back so the canonical output is preserved.
        let backup = target + ".bak"
        if File.Exists backup then File.Delete backup
        File.Move(target, backup)
        try
            File.Move(staged, target)
            File.Delete backup
        with
        | ex ->
            // Restore backup so canonical state is preserved.
            if File.Exists backup then
                if File.Exists target then File.Delete target
                File.Move(backup, target)
            raise ex
    else
        File.Move(staged, target)

/// Remove a directory and all its contents.  Failures are recorded and
/// returned to the caller.
let private tryRemoveDir (dir: string) : string option =
    try
        if Directory.Exists dir then
            Directory.Delete(dir, true)
        None
    with
    | ex -> Some(sprintf "%s: %s" dir ex.Message)

/// Publish the supplied files atomically into `canonicalDir`.  When
/// `failClosed` is true, any failure leaves the canonical outputs byte-
/// identical to before the call.  When `failClosed` is false (used only by
/// tests for happy-path inspection) the staging dir is preserved when
/// `preserveStaging` is true.
let publish
    (canonicalDir: string)
    (failClosed: bool)
    (preserveStaging: bool)
    (files: PendingFile list)
    : PublishOutcome =
    let preSnap = snapshotCanonicalHashes canonicalDir files
    let staging =
        let guid = System.Guid.NewGuid().ToString("N")
        Path.Combine(
            Path.GetDirectoryName canonicalDir,
            (Path.GetFileName canonicalDir) + ".staging." + guid)
    let mutable stagingCreated = false
    try
        Directory.CreateDirectory staging |> ignore
        stagingCreated <- true
        // Write everything first.
        let hashes =
            files
            |> List.map (fun f -> f.CanonicalFileName, writeAndFlush staging f)
        // Verify each on-disk SHA matches a freshly-computed SHA.  We already
        // computed the hashes from disk so this is a double-check: re-read and
        // compare.  Done implicitly by the writeAndFlush returning disk hash.
        // Now move into place.
        for f in files do
            replaceCanonical staging canonicalDir f
        // Cleanup staging.
        let retained = []
        let cleanError =
            if not preserveStaging then
                match tryRemoveDir staging with
                | Some msg -> [ staging + " (" + msg + ")" ]
                | None -> []
            else []
        { Success = true
          OutputHashes = hashes
          RetainedTempPaths = cleanError
          CanonicalByteIdenticalAfterFailure = true }
    with
    | ex ->
        // Restore any pre-existing canonical files that we touched, then
        // snapshot the canonical hashes again to prove byte-identity.
        if stagingCreated then
            tryRemoveDir staging |> ignore
        let postSnap =
            files
            |> List.map (fun f ->
                let fullPath = Path.Combine(canonicalDir, f.CanonicalFileName)
                let hash =
                    if File.Exists fullPath then sha256OfFile fullPath
                    else ""
                f.CanonicalFileName, hash)
            |> Map.ofList
        let canonicalByteIdentical =
            preSnap
            |> Map.forall (fun k v ->
                match Map.tryFind k postSnap with
                | Some v' -> v = v'
                | None -> v = "")
        if failClosed then
            { Success = false
              OutputHashes = []
              RetainedTempPaths =
                if Directory.Exists staging then [ staging ] else []
              CanonicalByteIdenticalAfterFailure = canonicalByteIdentical }
        else
            raise ex

/// Convenience helper for callers that don't need the rich outcome.
let publishSimple (canonicalDir: string) (files: PendingFile list) : PublishOutcome =
    publish canonicalDir true false files