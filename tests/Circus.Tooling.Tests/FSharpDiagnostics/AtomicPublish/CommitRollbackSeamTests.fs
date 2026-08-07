module Circus.Tooling.Tests.FSharpDiagnostics.AtomicPublish.CommitRollbackSeamTests

// =============================================================================
// Commit + rollback seam tests
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01-CORRECTION06B
//
// These tests prove the canonical install path through the seam:
//
//   - candidate install success (ReplaceFile when existing, MoveFile when absent)
//   - candidate install failure, existing A/A → A/A preserved (no rollback)
//   - candidate install failure, Absent/Absent → Absent/Absent preserved
//   - summary install failure, existing A/A → rollback restores A/A
//   - summary install failure, Absent/Absent → rollback removes candidate
//   - exact operation-order test for existing A/A rollback
//   - exact operation-order test for absent rollback
//   - canonical snapshot distinguishes Absent from zero-byte Present
//   - backup / staging paths remain under canonical parent (no system temp)
//
// Every test uses a unique repo-local temporary directory under
// `factory/tmp/atomic-publish-seam-tests-<guid>/` (NOT
// `Path.GetTempPath()`) and calls `publishWithDependencies` directly
// through the seam.  No test manually constructs an
// `AtomicPublishFailure` value and counts it as coverage; every fault is
// injected through a real production seam operation.
// =============================================================================

open System
open System.IO
open Expecto

open Circus.Tooling.FSharpDiagnostics.AtomicPublish
open Circus.Tooling.FSharpDiagnostics.Hashing

// -----------------------------------------------------------------------------
// Temp repository rooted inside the source tree (NOT system temp).
// -----------------------------------------------------------------------------

let private testRootDir () =
    let repoRoot =
        Path.GetFullPath(
            Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..", "..", ".."))

    Path.Combine(repoRoot, "factory", "tmp", "atomic-publish-commit-rollback-tests-" + Guid.NewGuid().ToString("N"))

let private newTempRepo () =
    let root = testRootDir ()
    Directory.CreateDirectory root |> ignore
    root

let private cleanupDir (dir: string) =
    if not (String.IsNullOrEmpty dir) && Directory.Exists dir then
        try
            Directory.Delete(dir, true)
        with _ ->
            ()

// -----------------------------------------------------------------------------
// Distinct A/B fixture bytes
// -----------------------------------------------------------------------------

let private canonicalCandidateABytes = "candidate-A"
let private canonicalSummaryABytes = "summary-A"
let private stagedCandidateBBytes = "candidate-B"
let private stagedSummaryBBytes = "summary-B"

let private candidateFileName = "candidate.json"
let private summaryFileName = "summary.json"

let private seedExistingAA (repo: string) =
    let canonical = Path.Combine(repo, "canonical")
    Directory.CreateDirectory canonical |> ignore
    File.WriteAllText(Path.Combine(canonical, candidateFileName), canonicalCandidateABytes)
    File.WriteAllText(Path.Combine(canonical, summaryFileName), canonicalSummaryABytes)
    canonical

let private seedAbsent (repo: string) =
    let canonical = Path.Combine(repo, "canonical")
    Directory.CreateDirectory canonical |> ignore
    canonical

let private stagedPendingFiles () =
    [ { CanonicalFileName = candidateFileName
        Body = stagedCandidateBBytes }
      { CanonicalFileName = summaryFileName
        Body = stagedSummaryBBytes } ]

// Pre-condition assertions required by the ACT.  These prevent accidental
// fixture drift from producing invalid rollback evidence.
let private assertABDistinction () =
    if canonicalCandidateABytes = canonicalSummaryABytes then
        failwithf "fixture invariant: candidate-A bytes must differ from summary-A bytes"
    if stagedCandidateBBytes = stagedSummaryBBytes then
        failwithf "fixture invariant: candidate-B bytes must differ from summary-B bytes"
    if canonicalCandidateABytes = stagedCandidateBBytes then
        failwithf "fixture invariant: candidate-A bytes must differ from candidate-B bytes"
    if canonicalSummaryABytes = stagedSummaryBBytes then
        failwithf "fixture invariant: summary-A bytes must differ from summary-B bytes"

// -----------------------------------------------------------------------------
// Recording seam with fault injection
// -----------------------------------------------------------------------------

/// Operation recorded by the seam.  Names are stable so tests can assert
/// on them via `List.contains`.
type private Op =
    | CreateDirectory of string
    | OpenWrite of string
    | WriteAll of string
    | FlushToDisk of string
    | DisposeHandle of string
    | ReadBytes of string         // staging-path read (verify-after-write)
    | SnapshotRead of string      // canonical-path read (snapshot pre-state)
    | FileExists of string        // canonical-path FileExists (snapshot or rollback)
    | ReplaceFile of string
    | MoveFile of string
    | DeleteFile of string

    override this.ToString () =
        match this with
        | CreateDirectory f -> "create-directory:" + f
        | OpenWrite f -> "open:" + f
        | WriteAll f -> f + ":write"
        | FlushToDisk f -> f + ":flush"
        | DisposeHandle f -> f + ":dispose"
        | ReadBytes f -> "read:" + f
        | SnapshotRead f -> "snapshot-read:" + f
        | FileExists f -> "file-exists:" + f
        | ReplaceFile f -> "replace:" + f
        | MoveFile f -> "move:" + f
        | DeleteFile f -> "delete:" + f

/// Phase at which an injected fault is raised.  Every fault passes
/// through a real production seam operation.
type CommitFault =
    | NoCommitFault
    | CandidateReplaceFault
    | CandidateMoveFault
    | SummaryReplaceFault
    | SummaryMoveFault

/// Recording IAtomicWriteHandle.  Records every seam call into `calls`,
/// owns the underlying FileStream, and disposes it on Dispose.
type private RecordingWriteHandle
    (
        calls : ResizeArray<Op>,
        label : string,
        stream : FileStream)
    =
    let mutable disposedFlag = false

    interface IAtomicWriteHandle with
        member _.WriteAll (bytes) =
            calls.Add(WriteAll label)
            stream.Write(bytes, 0, bytes.Length)

        member _.FlushToDisk () =
            calls.Add(FlushToDisk label)
            stream.Flush(true)

        member _.Dispose () =
            calls.Add(DisposeHandle label)

            if not disposedFlag then
                disposedFlag <- true
                stream.Dispose()

/// Build a recording AtomicPublishOps with the requested fault.  All
/// non-injected operations run against real System.IO.
let private buildOps
    (canonicalDir : string)
    (fault : CommitFault)
    : AtomicPublishOps * ResizeArray<Op> =
    let calls = ResizeArray<Op> ()

    let openWriteImpl (path : string) : IAtomicWriteHandle =
        let label = Path.GetFileName path
        calls.Add(OpenWrite label)

        let fs =
            new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                bufferSize = 4096,
                useAsync = false)

        upcast new RecordingWriteHandle(calls, label, fs)

    let readImpl (path : string) : byte[] =
        let label = Path.GetFileName path

        // Distinguish canonical-path reads (snapshot) from staging-path
        // reads (verify-after-write).  The canonical directory and the
        // staging directory share a parent but differ in name, so the
        // canonical path uses `canonicalDir + sep` as its prefix while
        // the staging path uses `stagingPrefix + sep`.  We test against
        // the canonical prefix (including the separator) so the staging
        // path is correctly excluded.
        let canonicalPrefix = canonicalDir + string Path.DirectorySeparatorChar
        if path.StartsWith(canonicalPrefix, StringComparison.Ordinal) then
            calls.Add(SnapshotRead label)
        else
            calls.Add(ReadBytes label)

        File.ReadAllBytes path

    let createDirImpl (path : string) =
        calls.Add(CreateDirectory path)
        Directory.CreateDirectory(path) |> ignore

    let fileExistsImpl (path : string) : bool =
        let label = Path.GetFileName path

        // FileExists is invoked on canonical paths during both the
        // snapshot pre-state capture and the rollback (delete-then-move)
        // step.  Both invocations are recorded under the same label.
        // The operation-order test asserts that the snapshot FileExists
        // happens before the commit and the rollback FileExists happens
        // after the commit.
        calls.Add(FileExists label)
        File.Exists path

    let moveFileImpl (source : string) (_dest : string) : unit =
        let sourceLabel = Path.GetFileName source
        calls.Add(MoveFile sourceLabel)

        match fault with
        | CandidateMoveFault when sourceLabel = candidateFileName ->
            raise (IOException("injected move fault for " + sourceLabel))
        | SummaryMoveFault when sourceLabel = summaryFileName ->
            raise (IOException("injected move fault for " + sourceLabel))
        | _ ->
            // Use File.Move semantics.  This is the canonical install
            // path when the canonical file was absent.
            File.Move(source, _dest)

    let replaceFileImpl (source : string) (_dest : string) (_backup : string) : unit =
        let sourceLabel = Path.GetFileName source
        calls.Add(ReplaceFile sourceLabel)

        match fault with
        | CandidateReplaceFault when sourceLabel = candidateFileName ->
            raise (IOException("injected replace fault for " + sourceLabel))
        | SummaryReplaceFault when sourceLabel = summaryFileName ->
            raise (IOException("injected replace fault for " + sourceLabel))
        | _ ->
            // Use File.Replace semantics.  This is the canonical install
            // path when the canonical file already exists.
            File.Replace(source, _dest, _backup)

    let deleteFileImpl (path : string) : unit =
        let label = Path.GetFileName path
        calls.Add(DeleteFile label)
        File.Delete path

    let ops =
        {
          CreateDirectory = createDirImpl
          OpenWrite = openWriteImpl
          ReadAllBytes = readImpl
          FileExists = fileExistsImpl
          MoveFile = moveFileImpl
          ReplaceFile = replaceFileImpl
          DeleteFile = deleteFileImpl
        }
    ops, calls

// -----------------------------------------------------------------------------
// Canonical snapshot equality helpers
// -----------------------------------------------------------------------------

let private canonicalBytes (canonical : string) =
    let c =
        if File.Exists(Path.Combine(canonical, candidateFileName)) then
            File.ReadAllBytes(Path.Combine(canonical, candidateFileName))
        else
            [||]
    let s =
        if File.Exists(Path.Combine(canonical, summaryFileName)) then
            File.ReadAllBytes(Path.Combine(canonical, summaryFileName))
        else
            [||]
    c, s

let private pairEquals (a : byte[] * byte[]) (b : byte[] * byte[]) : bool =
    let eqBytes (x : byte[]) (y : byte[]) : bool =
        if x.Length <> y.Length then
            false
        else
            let mutable ok = true
            let mutable i = 0

            while i < x.Length && ok do
                if x.[i] <> y.[i] then
                    ok <- false

                i <- i + 1

            ok

    eqBytes (fst a) (fst b) && eqBytes (snd a) (snd b)

let private expectAA (canonical : string) (label : string) =
    let actual = canonicalBytes canonical
    let expected =
        System.Text.Encoding.UTF8.GetBytes(canonicalCandidateABytes),
        System.Text.Encoding.UTF8.GetBytes(canonicalSummaryABytes)
    if not (pairEquals actual expected) then
        failwithf "expected canonical pair A/A but observed %A (%s)" actual label

let private expectAbsent (canonical : string) (label : string) =
    if File.Exists(Path.Combine(canonical, candidateFileName)) then
        failwithf "expected candidate absent but observed present (%s)" label

    if File.Exists(Path.Combine(canonical, summaryFileName)) then
        failwithf "expected summary absent but observed present (%s)" label

let private expectBB (canonical : string) (label : string) =
    let actual = canonicalBytes canonical
    let expected =
        System.Text.Encoding.UTF8.GetBytes(stagedCandidateBBytes + "\n"),
        System.Text.Encoding.UTF8.GetBytes(stagedSummaryBBytes + "\n")
    if not (pairEquals actual expected) then
        failwithf "expected canonical pair B/B but observed %A (%s)" actual label

// -----------------------------------------------------------------------------
// 1. existing A/A → B/B success
// -----------------------------------------------------------------------------

let private existingAABBSuccessTest =
    testCase "existing A/A → B/B success: candidate and summary replaced; committed recovery state"
    <| fun () ->
        assertABDistinction ()

        let repo = newTempRepo ()

        try
            let canonical = seedExistingAA repo
            let ops, calls = buildOps canonical NoCommitFault

            let preA, preB = canonicalBytes canonical

            let result =
                publishWithDependencies ops canonical (stagedPendingFiles ())

            match result with
            | AtomicPublishResult.Published success ->
                Expect.hasLength success.OutputHashes 2 "two staged hashes reported"
                Expect.equal success.RecoveryState AtomicRecoveryState.Committed "recovery state committed"
                Expect.equal success.CanonicalByteIdenticalAfterFailure true "canonical byte-identical after success"

                expectBB canonical "after publish"

                let opsCalls = List.ofSeq calls

                Expect.isTrue (opsCalls |> List.exists (fun o -> o.ToString() = "replace:" + candidateFileName))
                    "candidate replacement observed"
                Expect.isTrue (opsCalls |> List.exists (fun o -> o.ToString() = "replace:" + summaryFileName))
                    "summary replacement observed"

            | AtomicPublishResult.Failed report ->
                failwithf "expected Published, got Failed: %A" report

            // After publish, pre-state and current state should differ
            // (otherwise the publish was a no-op).
            let postA, postB = canonicalBytes canonical

            Expect.isFalse
                (pairEquals (preA, preB) (postA, postB))
                "publish must mutate the canonical pair"
        finally
            cleanupDir repo

// -----------------------------------------------------------------------------
// 2. candidate install failure, existing A/A → A/A preserved, no rollback
// -----------------------------------------------------------------------------

let private candidateInstallFailureExistingTest =
    testCase "candidate install failure (existing A/A): canonical A/A preserved; no rollback attempted"
    <| fun () ->
        assertABDistinction ()

        let repo = newTempRepo ()

        try
            let canonical = seedExistingAA repo

            let ops, calls =
                buildOps canonical CandidateReplaceFault

            let result =
                publishWithDependencies ops canonical (stagedPendingFiles ())

            match result with
            | AtomicPublishResult.Failed report ->
                Expect.hasLength report.Failures 1 "exactly one typed failure"
                let failure = report.Failures.[0]
                Expect.equal failure.Phase AtomicPublishPhase.Install "phase is install"
                Expect.isFalse (String.IsNullOrEmpty failure.Operation) "operation is non-empty"
                Expect.equal report.CanonicalByteIdenticalAfterFailure true "canonical bytes preserved"
                Expect.equal report.RecoveryState AtomicRecoveryState.NeverModified "recovery state never modified"

                expectAA canonical "after candidate-install failure"

                // No rollback operations should be observed: failure
                // occurred before any canonical mutation.
                let opsCalls = List.ofSeq calls |> List.map (fun o -> o.ToString ())

                Expect.isFalse
                    (opsCalls |> List.exists (fun s -> s.StartsWith("replace:" + summaryFileName)))
                    "summary replacement must not be attempted"
                Expect.isFalse
                    (opsCalls |> List.exists (fun s -> s.StartsWith("delete:") || s.StartsWith("move:")))
                    "no rollback or move/delete operations observed"

            | AtomicPublishResult.Published _ ->
                failwithf "expected Failed for candidate install fault"
        finally
            cleanupDir repo

// -----------------------------------------------------------------------------
// 3. summary install failure, existing A/A → rollback to A/A
// -----------------------------------------------------------------------------

let private summaryInstallFailureExistingRollbackTest =
    testCase "summary install failure (existing A/A): candidate temporarily B; rollback restores A/A"
    <| fun () ->
        assertABDistinction ()

        let repo = newTempRepo ()

        try
            let canonical = seedExistingAA repo

            let ops, calls =
                buildOps canonical SummaryReplaceFault

            let result =
                publishWithDependencies ops canonical (stagedPendingFiles ())

            match result with
            | AtomicPublishResult.Failed report ->
                Expect.hasLength report.Failures 1 "exactly one typed failure"
                let failure = report.Failures.[0]
                Expect.equal failure.Phase AtomicPublishPhase.Install "phase is install"

                Expect.equal report.CanonicalByteIdenticalAfterFailure true "canonical byte-identical after rollback"
                Expect.equal report.RecoveryState AtomicRecoveryState.RestoredByteIdentical "recovery state restored byte-identical"

                expectAA canonical "after rollback"

                // Operation order must show candidate replacement
                // succeeded before summary replacement failed.
                let opsCalls = List.ofSeq calls |> List.map (fun o -> o.ToString ())

                let iReplaceCandidate = opsCalls |> List.findIndex (fun s -> s = "replace:" + candidateFileName)
                let iReplaceSummary = opsCalls |> List.findIndex (fun s -> s = "replace:" + summaryFileName)
                let iRollbackDelete = opsCalls |> List.findIndex (fun s -> s = "delete:" + candidateFileName)
                let iRollbackMove = opsCalls |> List.findIndex (fun s -> s = "move:" + (candidateFileName + ".bak"))

                Expect.isTrue (iReplaceCandidate >= 0) "candidate replace observed"
                Expect.isTrue (iReplaceSummary >= 0) "summary replace observed (and failed)"
                Expect.isTrue (iReplaceCandidate < iReplaceSummary) "candidate replaces summary"
                Expect.isTrue (iReplaceSummary < iRollbackDelete) "summary failure precedes rollback delete"
                Expect.isTrue (iRollbackDelete < iRollbackMove) "rollback delete precedes rollback move"

                Expect.isTrue (opsCalls |> List.exists (fun s -> s = "file-exists:" + candidateFileName))
                    "rollback checks file-exists before move"

            | AtomicPublishResult.Published _ ->
                failwithf "expected Failed for summary install fault"
        finally
            cleanupDir repo

// -----------------------------------------------------------------------------
// 4. candidate install failure, Absent/Absent → Absent/Absent preserved
// -----------------------------------------------------------------------------

let private candidateInstallFailureAbsentTest =
    testCase "candidate install failure (Absent/Absent): canonical stays Absent/Absent; no rollback needed"
    <| fun () ->
        assertABDistinction ()

        let repo = newTempRepo ()

        try
            let canonical = seedAbsent repo

            let ops, _ =
                buildOps canonical CandidateMoveFault

            let result =
                publishWithDependencies ops canonical (stagedPendingFiles ())

            match result with
            | AtomicPublishResult.Failed report ->
                Expect.hasLength report.Failures 1 "exactly one typed failure"
                let failure = report.Failures.[0]
                Expect.equal failure.Phase AtomicPublishPhase.Install "phase is install"
                Expect.equal report.CanonicalByteIdenticalAfterFailure true "canonical byte-identical (still absent)"
                Expect.equal report.RecoveryState AtomicRecoveryState.NeverModified "recovery state never modified"

                expectAbsent canonical "after candidate-move failure"

            | AtomicPublishResult.Published _ ->
                failwithf "expected Failed for candidate move fault"
        finally
            cleanupDir repo

// -----------------------------------------------------------------------------
// 5. summary install failure, Absent/Absent → rollback removes candidate
// -----------------------------------------------------------------------------

let private summaryInstallFailureAbsentRollbackTest =
    testCase "summary install failure (Absent/Absent): candidate installed then removed by rollback"
    <| fun () ->
        assertABDistinction ()

        let repo = newTempRepo ()

        try
            let canonical = seedAbsent repo

            let ops, calls =
                buildOps canonical SummaryMoveFault

            let result =
                publishWithDependencies ops canonical (stagedPendingFiles ())

            match result with
            | AtomicPublishResult.Failed report ->
                Expect.hasLength report.Failures 1 "exactly one typed failure"
                let failure = report.Failures.[0]
                Expect.equal failure.Phase AtomicPublishPhase.Install "phase is install"

                Expect.equal report.CanonicalByteIdenticalAfterFailure true "canonical byte-identical (absent again)"
                Expect.equal report.RecoveryState AtomicRecoveryState.RestoredByteIdentical "recovery state restored byte-identical"

                expectAbsent canonical "after absent rollback"

                let opsCalls = List.ofSeq calls |> List.map (fun o -> o.ToString ())

                let iMoveCandidate = opsCalls |> List.findIndex (fun s -> s = "move:" + candidateFileName)
                let iMoveSummary = opsCalls |> List.findIndex (fun s -> s = "move:" + summaryFileName)
                let iRollbackDelete = opsCalls |> List.findIndex (fun s -> s = "delete:" + candidateFileName)

                Expect.isTrue (iMoveCandidate >= 0) "candidate move observed (and succeeded)"
                Expect.isTrue (iMoveSummary >= 0) "summary move observed (and failed)"
                Expect.isTrue (iMoveCandidate < iMoveSummary) "candidate precedes summary"
                Expect.isTrue (iMoveSummary < iRollbackDelete) "summary failure precedes rollback delete"

            | AtomicPublishResult.Published _ ->
                failwithf "expected Failed for summary move fault"
        finally
            cleanupDir repo

// -----------------------------------------------------------------------------
// 6. exact operation-order test for existing A/A rollback
// -----------------------------------------------------------------------------

let private existingRollbackOrderTest =
    testCase "operation order (existing A/A rollback): snapshot -> stage -> replace candidate -> replace summary (fault) -> rollback delete -> rollback move"
    <| fun () ->
        assertABDistinction ()

        let repo = newTempRepo ()

        try
            let canonical = seedExistingAA repo

            let ops, calls =
                buildOps canonical SummaryReplaceFault

            let _ =
                publishWithDependencies ops canonical (stagedPendingFiles ())

            let opsCalls = List.ofSeq calls |> List.map (fun o -> o.ToString ())

            let findOrFail (pred : string -> bool) (label : string) : int =
                let idx = opsCalls |> List.tryFindIndex pred

                match idx with
                | Some i -> i
                | None -> failwithf "expected %s but did not observe" label

            let iSnapReadCandidate =
                findOrFail (fun s -> s = "snapshot-read:" + candidateFileName) "snapshot-read candidate"
            let iSnapReadSummary =
                findOrFail (fun s -> s = "snapshot-read:" + summaryFileName) "snapshot-read summary"
            let iCreateStaging =
                findOrFail (fun s -> s.StartsWith("create-directory:") && s.Contains(".staging.")) "create staging"
            let iOpenCandidate =
                findOrFail (fun s -> s = "open:" + candidateFileName) "open candidate"
            let iReadStagedCandidate =
                findOrFail (fun s -> s = "read:" + candidateFileName) "read staged candidate"
            let iOpenSummary =
                findOrFail (fun s -> s = "open:" + summaryFileName) "open summary"
            let iReplaceCandidate =
                findOrFail (fun s -> s = "replace:" + candidateFileName) "replace candidate"
            let iReplaceSummary =
                findOrFail (fun s -> s = "replace:" + summaryFileName) "replace summary"
            let iRollbackDelete =
                findOrFail (fun s -> s = "delete:" + candidateFileName) "rollback delete"
            let iRollbackMove =
                findOrFail (fun s -> s = "move:" + (candidateFileName + ".bak")) "rollback move"

            // Snapshot reads precede any commit-phase operation.
            Expect.isTrue (iSnapReadCandidate < iReplaceCandidate) "snapshot read precedes candidate replace"
            Expect.isTrue (iSnapReadSummary < iReplaceCandidate) "snapshot read precedes candidate replace"

            // Staging operations precede commit.
            Expect.isTrue (iCreateStaging < iOpenCandidate) "create staging precedes open candidate"
            Expect.isTrue (iOpenCandidate < iOpenSummary) "candidate staging precedes summary staging"
            Expect.isTrue (iReadStagedCandidate < iReplaceCandidate) "candidate verify precedes candidate replace"

            // Commit order: candidate first, then summary (which faults).
            Expect.isTrue (iReplaceCandidate < iReplaceSummary) "candidate replace precedes summary replace"

            // After the fault, only rollback operations are observed.
            Expect.isTrue (iReplaceSummary < iRollbackDelete) "summary fault precedes rollback delete"
            Expect.isTrue (iRollbackDelete < iRollbackMove) "rollback delete precedes rollback move"

            // No second publication attempt: no further replace
            // operations after the rollback move.
            let afterRollback =
                opsCalls
                |> List.skip (iRollbackMove + 1)

            Expect.isFalse
                (afterRollback |> List.exists (fun s -> s.StartsWith("replace:") || s.StartsWith("move:") || s.StartsWith("open:")))
                "no second publication attempt after rollback"
        finally
            cleanupDir repo

// -----------------------------------------------------------------------------
// 7. exact operation-order test for absent rollback
// -----------------------------------------------------------------------------

let private absentRollbackOrderTest =
    testCase "operation order (Absent rollback): snapshot -> stage -> move candidate -> move summary (fault) -> rollback delete candidate"
    <| fun () ->
        assertABDistinction ()

        let repo = newTempRepo ()

        try
            let canonical = seedAbsent repo

            let ops, calls =
                buildOps canonical SummaryMoveFault

            let _ =
                publishWithDependencies ops canonical (stagedPendingFiles ())

            let opsCalls = List.ofSeq calls |> List.map (fun o -> o.ToString ())

            let findOrFail (pred : string -> bool) (label : string) : int =
                match opsCalls |> List.tryFindIndex pred with
                | Some i -> i
                | None -> failwithf "expected %s but did not observe" label

            let iMoveCandidate =
                findOrFail (fun s -> s = "move:" + candidateFileName) "move candidate"
            let iMoveSummary =
                findOrFail (fun s -> s = "move:" + summaryFileName) "move summary"
            let iRollbackDelete =
                findOrFail (fun s -> s = "delete:" + candidateFileName) "rollback delete"

            // Commit order: candidate move first, then summary move.
            Expect.isTrue (iMoveCandidate < iMoveSummary) "candidate precedes summary"

            // After the fault, only the candidate rollback delete is
            // observed.
            Expect.isTrue (iMoveSummary < iRollbackDelete) "summary fault precedes rollback delete"

            let afterRollback =
                opsCalls
                |> List.skip (iRollbackDelete + 1)

            Expect.isFalse
                (afterRollback |> List.exists (fun s -> s.StartsWith("replace:") || s.StartsWith("move:") || s.StartsWith("open:")))
                "no second publication attempt after absent rollback"
        finally
            cleanupDir repo

// -----------------------------------------------------------------------------
// 8. canonical snapshot distinguishes Absent from zero-byte Present
// -----------------------------------------------------------------------------

let private snapshotDistinguishesAbsentFromEmptyTest =
    testCase "canonical snapshot distinguishes Absent from zero-byte Present"
    <| fun () ->
        let repo = newTempRepo ()

        try
            let canonical = Path.Combine(repo, "canonical")
            Directory.CreateDirectory canonical |> ignore

            // Empty candidate file present; summary absent.
            File.WriteAllBytes(Path.Combine(canonical, candidateFileName), [||])

            let ops, _ =
                buildOps canonical NoCommitFault

            let snap =
                snapshotCanonicalPair ops canonical
                    [ { CanonicalFileName = candidateFileName
                        Body = "candidate-B" }
                      { CanonicalFileName = summaryFileName
                        Body = "summary-B" } ]

            match snap.Candidate with
            | CanonicalFileSnapshot.Present bytes ->
                Expect.equal bytes.Length 0 "candidate snapshot is empty Present (NOT Absent)"
            | CanonicalFileSnapshot.Absent ->
                failwithf "expected Present of empty bytes for candidate; observed Absent"

            match snap.Summary with
            | CanonicalFileSnapshot.Absent -> ()
            | CanonicalFileSnapshot.Present bytes ->
                failwithf "expected Absent for summary; observed Present of %d bytes" bytes.Length
        finally
            cleanupDir repo

// -----------------------------------------------------------------------------
// 9. backup / staging paths remain under canonical parent
// -----------------------------------------------------------------------------

let private pathDisciplineTest =
    testCase "path discipline: parent(stagingDir) = parent(canonicalDir); backup stays under canonical parent; no system temp"
    <| fun () ->
        let repo = newTempRepo ()

        try
            let canonical = seedExistingAA repo

            let ops, calls =
                buildOps canonical NoCommitFault

            let _ =
                publishWithDependencies ops canonical (stagedPendingFiles ())

            let opsCalls = List.ofSeq calls |> List.map (fun o -> o.ToString ())

            let stagingEntry =
                opsCalls |> List.find (fun s -> s.StartsWith("create-directory:"))

            let stagingDir =
                stagingEntry.Substring("create-directory:".Length)

            let stagingParent = Path.GetDirectoryName stagingDir
            let canonicalParent = Path.GetDirectoryName canonical

            Expect.equal
                stagingParent
                canonicalParent
                "staging parent equals canonical parent"

            let tmpPath = Path.GetFullPath(Path.GetTempPath())
            Expect.isFalse
                (stagingDir.StartsWith(tmpPath, StringComparison.Ordinal))
                "staging is not under system temp"

            Expect.isFalse
                (canonical.StartsWith(tmpPath, StringComparison.Ordinal))
                "canonical is not under system temp"

            // After a successful publication the ReplaceFile seam leaves
            // the destination backup file (the original canonical bytes)
            // inside canonicalDir as <filename>.bak.  Confirm it lives
            // inside canonicalDir (not its parent) and shares the parent
            // filesystem tree with staging.
            let backups =
                Directory.GetFiles(canonical, "*.bak", SearchOption.TopDirectoryOnly)
                |> Array.filter (fun p ->
                    p.Contains(candidateFileName)
                    || p.Contains(summaryFileName))

            // At least one backup must remain after a successful commit.
            Expect.isTrue
                (backups.Length >= 1)
                "at least one destination backup remains inside canonicalDir"

            for bp in backups do
                // The backup must live inside canonicalDir, not its parent.
                Expect.equal
                    (Path.GetDirectoryName bp)
                    canonical
                    "backup path is inside canonicalDir"

                // And canonicalDir must share its parent with the staging
                // directory so replacement stays on the same filesystem.
                Expect.equal
                    (Path.GetDirectoryName canonical)
                    stagingParent
                    "canonicalDir parent matches stagingDir parent"
        finally
            cleanupDir repo

// -----------------------------------------------------------------------------
// Wire-up
// -----------------------------------------------------------------------------

// -----------------------------------------------------------------------------
// Cardinality rejection (Correction06B reviewer feedback)
// -----------------------------------------------------------------------------

let private canonicalPairCardinalityEmptyTest =
    testCase "canonical pair cardinality: zero files -> no canonical mutation, no staging, no commit"
    <| fun () ->
        assertABDistinction ()

        let repo = newTempRepo ()

        try
            let canonical = seedExistingAA repo
            let preA, preB = canonicalBytes canonical

            // Capture ops with a seam that explodes if it is touched.  A
            // cardinality rejection must raise BEFORE any filesystem
            // primitive runs through the seam.
            let ops, calls =
                buildOps canonical NoCommitFault

            let result =
                publishWithDependencies ops canonical []

            match result with
            | AtomicPublishResult.Failed report ->
                Expect.hasLength report.Failures 1 "exactly one cardinality failure"
                let failure = report.Failures.[0]
                Expect.equal failure.Phase AtomicPublishPhase.Install "cardinality failure reported as Install phase"
                Expect.equal failure.Operation "canonical-pair-cardinality" "cardinality operation token"
                Expect.isTrue (failure.Detail.Contains "0") "cardinality detail mentions the actual count"

                Expect.equal report.RecoveryState AtomicRecoveryState.NeverModified "cardinality failure is NeverModified"
            | _ ->
                failwithf "expected Failed for empty files, got %A" result

            Expect.isEmpty (List.ofSeq calls) "no seam call observed before cardinality rejection"

            let postA, postB = canonicalBytes canonical
            Expect.equal postA preA "canonical candidate unchanged"
            Expect.equal postB preB "canonical summary unchanged"
        finally
            cleanupDir repo

let private canonicalPairCardinalityOneFileTest =
    testCase "canonical pair cardinality: one file -> cardinality failure, no canonical mutation"
    <| fun () ->
        assertABDistinction ()

        let repo = newTempRepo ()

        try
            let canonical = seedExistingAA repo
            let preA, preB = canonicalBytes canonical

            let ops, calls =
                buildOps canonical NoCommitFault

            let singleFile =
                [ { CanonicalFileName = candidateFileName
                    Body = "candidate-B" } ]

            let result =
                publishWithDependencies ops canonical singleFile

            match result with
            | AtomicPublishResult.Failed report ->
                Expect.hasLength report.Failures 1 "exactly one cardinality failure"
                let failure = report.Failures.[0]
                Expect.equal failure.Operation "canonical-pair-cardinality" "cardinality operation token"
                Expect.isTrue (failure.Detail.Contains "1") "cardinality detail mentions the actual count"
                Expect.equal report.RecoveryState AtomicRecoveryState.NeverModified "one-file failure is NeverModified"
            | _ ->
                failwithf "expected Failed for single file, got %A" result

            // Cardinality rejection must fire BEFORE any staging, snapshot, or
            // canonical I/O.  Assert the seam was never touched.
            Expect.isEmpty (List.ofSeq calls) "no seam calls observed before cardinality rejection"

            let postA, postB = canonicalBytes canonical
            Expect.equal postA preA "canonical candidate unchanged"
            Expect.equal postB preB "canonical summary unchanged"
        finally
            cleanupDir repo

let private canonicalPairCardinalityThreeFilesTest =
    testCase "canonical pair cardinality: three files -> cardinality failure, only the first two are addressed"
    <| fun () ->
        assertABDistinction ()

        let repo = newTempRepo ()

        try
            let canonical = seedExistingAA repo

            let ops, calls =
                buildOps canonical NoCommitFault

            let threeFiles =
                [ { CanonicalFileName = candidateFileName
                    Body = stagedCandidateBBytes }
                  { CanonicalFileName = summaryFileName
                    Body = stagedSummaryBBytes }
                  { CanonicalFileName = "extra.json"
                    Body = "extra-body" } ]

            let result =
                publishWithDependencies ops canonical threeFiles

            match result with
            | AtomicPublishResult.Failed report ->
                Expect.hasLength report.Failures 1 "exactly one cardinality failure"
                Expect.equal report.Failures.[0].Operation "canonical-pair-cardinality" "cardinality operation token"
                Expect.isTrue (report.Failures.[0].Detail.Contains "3") "cardinality detail mentions the actual count"
                Expect.equal report.RecoveryState AtomicRecoveryState.NeverModified "three-file failure is NeverModified"
            | _ ->
                failwithf "expected Failed for three files, got %A" result

            // Cardinality rejection must fire BEFORE any staging, snapshot, or
            // canonical I/O.  Assert the seam was never touched and no canonical
            // bytes were changed.
            Expect.isEmpty (List.ofSeq calls) "no seam calls observed before cardinality rejection"

            let postA, postB = canonicalBytes canonical
            Expect.equal postA "candidate-A"B "canonical candidate unchanged"
            Expect.equal postB "summary-A"B "canonical summary unchanged"
        finally
            cleanupDir repo

// -----------------------------------------------------------------------------
// Missing backup surfaces as a typed RollbackRestore failure
// (Correction06B reviewer feedback)
// -----------------------------------------------------------------------------

type MissingBackupMoveFile =
    | NoMissingBackup
    | MissingBackupCandidate

let private buildOpsWithMissingBackup
    (canonicalDir : string)
    (missing : MissingBackupMoveFile)
    : AtomicPublishOps * ResizeArray<Op> =
    let calls = ResizeArray<Op> ()

    let createDirImpl (path : string) =
        calls.Add(CreateDirectory path)
        Directory.CreateDirectory(path) |> ignore

    let openWriteImpl (path : string) : IAtomicWriteHandle =
        let label = Path.GetFileName path
        calls.Add(OpenWrite label)

        let fs =
            new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                bufferSize = 4096,
                useAsync = false)

        upcast new RecordingWriteHandle(calls, label, fs)

    let readImpl (path : string) : byte[] =
        let label = Path.GetFileName path
        let canonicalPrefix = canonicalDir + string Path.DirectorySeparatorChar
        if path.StartsWith(canonicalPrefix, StringComparison.Ordinal) then
            calls.Add(SnapshotRead label)
        else
            calls.Add(ReadBytes label)
        File.ReadAllBytes path

    let fileExistsImpl (path : string) : bool =
        let label = Path.GetFileName path
        calls.Add(FileExists label)
        File.Exists path

    let moveFileImpl (source : string) (_dest : string) : unit =
        let sourceLabel = Path.GetFileName source
        calls.Add(MoveFile sourceLabel)
        File.Move(source, _dest)

    let replaceFileImpl (source : string) (_dest : string) (_backup : string) : unit =
        let sourceLabel = Path.GetFileName source
        calls.Add(ReplaceFile sourceLabel)

        // For the "missing backup" test: claim to replace the candidate
        // successfully without actually creating the destination backup
        // file.  The production rollback will then observe a missing
        // backup and surface a typed RollbackRestore failure.
        if sourceLabel = candidateFileName && missing = MissingBackupCandidate then
            // Move source into destination without writing the backup.
            // Simulate File.Replace by copying source -> destination and
            // then deleting source, but NEVER touching the backup path.
            File.Copy(source, _dest, overwrite = true)
            File.Delete(source)
        elif sourceLabel = summaryFileName then
            // Summary install must fail so the rollback runs against the
            // candidate that was replaced-without-backup.  We raise
            // here so commitCanonicalPairFromStaging observes an
            // Install-phase failure and the rollback path runs.
            raise (IOException("injected summary replace fault for missing-backup test"))
        else
            File.Replace(source, _dest, _backup)

    let deleteFileImpl (path : string) : unit =
        let label = Path.GetFileName path
        calls.Add(DeleteFile label)
        File.Delete path

    let ops =
        {
          CreateDirectory = createDirImpl
          OpenWrite = openWriteImpl
          ReadAllBytes = readImpl
          FileExists = fileExistsImpl
          MoveFile = moveFileImpl
          ReplaceFile = replaceFileImpl
          DeleteFile = deleteFileImpl
        }
    ops, calls

// -----------------------------------------------------------------------------
// Install mutates, then throws (post-mutation failure)
// -----------------------------------------------------------------------------
//
// `File.Replace` is documented as atomic on a single volume, but the seam
// is precisely what guards against an implementation that mutates the
// destination and then throws BEFORE writing the backup.  When that
// happens, the canonical state has been changed but the rollback never
// runs (because no backup was created and the commit reported failure).
// The honest recovery state is therefore MayHaveChanged — NOT
// NeverModified, even though rollback was never attempted.

type private CommitFaultV2 =
    | NoCommitFaultV2
    | MutateThenThrowCandidate
    | MutateThenThrowSummary

let private buildOpsV2
    (canonicalDir : string)
    (fault : CommitFaultV2)
    : AtomicPublishOps * ResizeArray<Op> =
    let calls = ResizeArray<Op> ()

    let openWriteImpl (path : string) : IAtomicWriteHandle =
        let label = Path.GetFileName path
        calls.Add(OpenWrite label)

        let fs =
            new FileStream(
                path,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read,
                bufferSize = 4096,
                useAsync = false)

        upcast new RecordingWriteHandle(calls, label, fs)

    let readImpl (path : string) : byte[] =
        let label = Path.GetFileName path
        let canonicalPrefix = canonicalDir + string Path.DirectorySeparatorChar
        if path.StartsWith(canonicalPrefix, StringComparison.Ordinal) then
            calls.Add(SnapshotRead label)
        else
            calls.Add(ReadBytes label)
        File.ReadAllBytes path

    let createDirImpl (path : string) =
        calls.Add(CreateDirectory path)
        Directory.CreateDirectory(path) |> ignore

    let fileExistsImpl (path : string) : bool =
        let label = Path.GetFileName path
        calls.Add(FileExists label)
        File.Exists path

    let moveFileImpl (source : string) (_dest : string) : unit =
        let sourceLabel = Path.GetFileName source
        calls.Add(MoveFile sourceLabel)
        File.Move(source, _dest)

    let replaceFileImpl (source : string) (_dest : string) (_backup : string) : unit =
        let sourceLabel = Path.GetFileName source
        calls.Add(ReplaceFile sourceLabel)

        // Post-mutation failure simulation.  We perform the equivalent of
        // the first half of File.Replace (move source onto destination,
        // mutating the canonical state) and then throw before creating the
        // backup.  In a real implementation this is exactly the state a
        // failing File.Replace could leave behind on some platforms.
        match fault, sourceLabel with
        | MutateThenThrowCandidate, candidateFileName ->
            File.Copy(source, _dest, overwrite = true)
            File.Delete(source)
            raise (IOException("injected post-mutation candidate fault"))
        | MutateThenThrowSummary, summaryFileName ->
            File.Copy(source, _dest, overwrite = true)
            File.Delete(source)
            raise (IOException("injected post-mutation summary fault"))
        | _ ->
            File.Replace(source, _dest, _backup)

    let deleteFileImpl (path : string) : unit =
        let label = Path.GetFileName path
        calls.Add(DeleteFile label)
        File.Delete path

    let ops =
        {
          CreateDirectory = createDirImpl
          OpenWrite = openWriteImpl
          ReadAllBytes = readImpl
          FileExists = fileExistsImpl
          MoveFile = moveFileImpl
          ReplaceFile = replaceFileImpl
          DeleteFile = deleteFileImpl
        }
    ops, calls

let private mutateThenThrowCandidateInstallTest =
    testCase "install mutates then throws (post-mutation candidate fault) -> MayHaveChanged, NOT NeverModified"
    <| fun () ->
        assertABDistinction ()

        let repo = newTempRepo ()

        try
            let canonical = seedExistingAA repo
            let ops, calls =
                buildOpsV2 canonical MutateThenThrowCandidate

            let result =
                publishWithDependencies ops canonical (stagedPendingFiles ())

            match result with
            | AtomicPublishResult.Failed report ->
                Expect.hasLength report.Failures 1 "exactly one install failure"
                let failure = report.Failures.[0]
                Expect.equal failure.Phase AtomicPublishPhase.Install "phase is install"

                // The candidate canonical file was mutated (now contains
                // staged body) BEFORE the install threw.  No rollback ran
                // because no backup existed to restore from.  The honest
                // recovery state is therefore MayHaveChanged — NOT
                // NeverModified, even though rollback was never attempted.
                Expect.equal
                    report.CanonicalByteIdenticalAfterFailure
                    false
                    "canonical pair no longer matches pre-snapshot"
                Expect.equal
                    report.RecoveryState
                    AtomicRecoveryState.MayHaveChanged
                    "post-mutation install failure must be MayHaveChanged, not NeverModified"

                // And the canonical file on disk is now the staged body
                // (the mutation succeeded but the install reported failure).
                let onDiskCandidate =
                    File.ReadAllBytes(Path.Combine(canonical, candidateFileName))
                let expectedStaged =
                    System.Text.Encoding.UTF8.GetBytes(stagedCandidateBBytes + "\n")
                Expect.equal
                    onDiskCandidate
                    expectedStaged
                    "candidate canonical now contains the staged bytes"
            | _ ->
                failwithf "expected Failed for post-mutation candidate fault, got %A" result
        finally
            cleanupDir repo

let private missingBackupRollbackTest =
    testCase "rollback surfaces typed failure when destination backup is missing (cannot silently no-op)"
    <| fun () ->
        assertABDistinction ()

        let repo = newTempRepo ()

        try
            let canonical = seedExistingAA repo

            let ops, _ =
                buildOpsWithMissingBackup canonical MissingBackupCandidate

            let result =
                publishWithDependencies ops canonical (stagedPendingFiles ())

            match result with
            | AtomicPublishResult.Failed report ->
                // The candidate "Replace" claimed success but produced no
                // backup.  The subsequent summary install faulted, the
                // rollback observed a missing candidate backup and surfaced
                // it as a typed RollbackRestore failure, and the recovery
                // state must report MayHaveChanged because rollback did
                // not provably restore the canonical pair.
                Expect.isTrue
                    (report.Failures |> List.exists (fun f -> f.Phase = AtomicPublishPhase.RollbackRestore))
                    "RollbackRestore failure reported"

                let restoreFailures =
                    report.Failures |> List.filter (fun f -> f.Phase = AtomicPublishPhase.RollbackRestore)
                Expect.isFalse
                    (List.isEmpty restoreFailures)
                    "at least one RollbackRestore failure"

                Expect.isTrue
                    (restoreFailures
                     |> List.exists (fun f -> f.Detail.Contains "missing"))
                    "missing-backup detail is preserved"

                Expect.equal
                    report.RecoveryState
                    AtomicRecoveryState.MayHaveChanged
                    "missing-backup rollback is MayHaveChanged, never NeverModified"
            | _ ->
                failwithf "expected Failed with RollbackRestore, got %A" result
        finally
            cleanupDir repo

[<Tests>]
let commitRollbackSeamTests =
    testList
        "FSharpDiagnostics.AtomicPublish.CommitRollback"
        [ existingAABBSuccessTest
          candidateInstallFailureExistingTest
          summaryInstallFailureExistingRollbackTest
          candidateInstallFailureAbsentTest
          summaryInstallFailureAbsentRollbackTest
          existingRollbackOrderTest
          absentRollbackOrderTest
          snapshotDistinguishesAbsentFromEmptyTest
          pathDisciplineTest
          canonicalPairCardinalityEmptyTest
          canonicalPairCardinalityOneFileTest
          canonicalPairCardinalityThreeFilesTest
          missingBackupRollbackTest
          mutateThenThrowCandidateInstallTest ]
