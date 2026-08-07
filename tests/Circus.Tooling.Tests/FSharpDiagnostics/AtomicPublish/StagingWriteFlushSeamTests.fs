module Circus.Tooling.Tests.FSharpDiagnostics.AtomicPublish.StagingWriteFlushSeamTests

// =============================================================================
// Staging write-flush seam tests
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01-CORRECTION06A
//
// These tests prove the pre-commit staging write path:
//
//   - introduce a narrow filesystem seam (AtomicPublishOps)
//   - prove real FileStream + Flush(true) on the success path
//   - inject real failures at every pre-commit phase (directory,
//     open, write, flush, verify) for both the first and second
//     staged file (9 fault points)
//   - prove canonical byte preservation on every failure
//   - prove the staging location invariant: parent(stagingDir) =
//     parent(canonicalDir)
//   - prove operation sequencing after a fault: no further seam calls
//   - prove the absent canonical pair also stays absent on failure
//
// Tests MUST call publishWithDependencies directly.  Tests MUST NOT
// manually construct AtomicPublishFailure values and count that as
// coverage.  Every test uses a unique temporary repository rooted
// beneath the repo-local tmp directory (NOT Path.GetTempPath()).
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

    Path.Combine(repoRoot, "factory", "tmp", "atomic-publish-seam-tests-" + Guid.NewGuid().ToString("N"))

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

let private sha256Of (path: string) =
    if File.Exists path then sha256OfFile path else ""

let private canonicalBytes (canonical: string) =
    sha256Of (Path.Combine(canonical, "a.json")),
    sha256Of (Path.Combine(canonical, "b.json"))

// -----------------------------------------------------------------------------
// Recording seam
// -----------------------------------------------------------------------------

/// Phase at which an injected fault is raised.  Every fault passes
/// through a real production seam operation (CreateDirectory, OpenWrite,
/// WriteAll, FlushToDisk, ReadAllBytes).
type FaultPhase =
    | NoneFault
    | CreateDirectoryFault
    | FirstOpenFault
    | FirstWriteFault
    | FirstFlushFault
    | FirstVerifyFault
    | SecondOpenFault
    | SecondWriteFault
    | SecondFlushFault
    | SecondVerifyFault

let private faultToPhase =
    function
    | NoneFault -> None
    | CreateDirectoryFault -> Some AtomicPublishPhase.StageDirectory
    | FirstOpenFault -> Some AtomicPublishPhase.StageOpen
    | FirstWriteFault -> Some AtomicPublishPhase.StageWrite
    | FirstFlushFault -> Some AtomicPublishPhase.StageFlush
    | FirstVerifyFault -> Some AtomicPublishPhase.StageVerify
    | SecondOpenFault -> Some AtomicPublishPhase.StageOpen
    | SecondWriteFault -> Some AtomicPublishPhase.StageWrite
    | SecondFlushFault -> Some AtomicPublishPhase.StageFlush
    | SecondVerifyFault -> Some AtomicPublishPhase.StageVerify

let private faultDescription =
    function
    | NoneFault -> "none"
    | CreateDirectoryFault -> "create-directory"
    | FirstOpenFault -> "first-open"
    | FirstWriteFault -> "first-write"
    | FirstFlushFault -> "first-flush"
    | FirstVerifyFault -> "first-verify"
    | SecondOpenFault -> "second-open"
    | SecondWriteFault -> "second-write"
    | SecondFlushFault -> "second-flush"
    | SecondVerifyFault -> "second-verify"

/// Recording IAtomicWriteHandle.  Records every seam call, owns the
/// underlying FileStream, and disposes it on Dispose.  When the
/// configured fault phase matches the current operation, throws
/// IOException after recording.
type private RecordingWriteHandle
    (
        calls: ResizeArray<string>,
        fault: FaultPhase,
        label: string,
        stream: FileStream)
    =
    let mutable disposedFlag = false

    interface IAtomicWriteHandle with
        member _.WriteAll (bytes) =
            calls.Add(label + ":write")

            match fault with
            | FirstWriteFault when label = "a.json" ->
                raise (IOException("injected write fault for " + label))
            | SecondWriteFault when label = "b.json" ->
                raise (IOException("injected write fault for " + label))
            | _ -> stream.Write(bytes, 0, bytes.Length)

        member _.FlushToDisk () =
            calls.Add(label + ":flush")

            match fault with
            | FirstFlushFault when label = "a.json" ->
                raise (IOException("injected flush fault for " + label))
            | SecondFlushFault when label = "b.json" ->
                raise (IOException("injected flush fault for " + label))
            | _ -> stream.Flush(true)

        member _.Dispose () =
            calls.Add(label + ":dispose")

            if not disposedFlag then
                disposedFlag <- true
                stream.Dispose()

/// Build a recording AtomicPublishOps that fails at `fault`.  All
/// non-injected operations run against real System.IO.
let private buildRecordingOps (canonicalDir: string) (fault: FaultPhase) : AtomicPublishOps * ResizeArray<string> =
    let calls = ResizeArray<string>()

    let openWriteImpl (path: string) : IAtomicWriteHandle =
        let label =
            let pathStr : string = path
            try Path.GetFileName(pathStr)
            with _ -> pathStr
        calls.Add("open:" + label)

        match fault with
        | FirstOpenFault when label = "a.json" ->
            raise (IOException("injected open fault for " + label))
        | SecondOpenFault when label = "b.json" ->
            raise (IOException("injected open fault for " + label))
        | _ ->
            let fs =
                new FileStream(
                    path,
                    FileMode.Create,
                    FileAccess.Write,
                    FileShare.Read,
                    bufferSize = 4096,
                    useAsync = false)

            let handle = new RecordingWriteHandle(calls, fault, label, fs)
            handle :> IAtomicWriteHandle

    let readImpl (path: string) : byte[] =
        let label =
            let pathStr : string = path
            try Path.GetFileName(pathStr)
            with _ -> pathStr
        calls.Add("read:" + label)

        match fault with
        | FirstVerifyFault when label = "a.json" ->
            raise (IOException("injected read fault for " + label))
        | SecondVerifyFault when label = "b.json" ->
            raise (IOException("injected read fault for " + label))
        | _ -> File.ReadAllBytes path

    let createDirImpl (path: string) =
        calls.Add("create-directory:" + path)

        match fault with
        | CreateDirectoryFault -> raise (IOException("injected create-directory fault"))
        | _ -> Directory.CreateDirectory(path) |> ignore

    let ops =
        {
          CreateDirectory = createDirImpl
          OpenWrite = openWriteImpl
          ReadAllBytes = readImpl
        }
    ops, calls

// -----------------------------------------------------------------------------
// Test scaffold: canonical A/B pair + staged B pair (different bytes)
// -----------------------------------------------------------------------------

let private seedCanonicalA (repo: string) =
    let canonical = Path.Combine(repo, "canonical")
    Directory.CreateDirectory canonical |> ignore
    let canonicalA = "AAAA-canonical-A"
    let canonicalB = "BBBB-canonical-A"
    File.WriteAllText(Path.Combine(canonical, "a.json"), canonicalA)
    File.WriteAllText(Path.Combine(canonical, "b.json"), canonicalB)
    let stagedBodyA = "XXXX-staged-B"
    let stagedBodyB = "YYYY-staged-B"
    canonical, canonicalA, canonicalB, stagedBodyA, stagedBodyB

let private seedCanonicalAndFiles (repo: string) =
    let canonical, _, _, stagedBodyA, stagedBodyB = seedCanonicalA repo

    let files =
        [ { CanonicalFileName = "a.json"
            Body = stagedBodyA }
          { CanonicalFileName = "b.json"
            Body = stagedBodyB } ]

    canonical, files

let private stagedPendingFiles () =
    [ { CanonicalFileName = "a.json"
        Body = "XXXX-staged-B" }
      { CanonicalFileName = "b.json"
        Body = "YYYY-staged-B" } ]

// -----------------------------------------------------------------------------
// Per-fault failure-injection tests
// -----------------------------------------------------------------------------

let private faultTest (fault: FaultPhase) =
    testCase (sprintf "fault injection: %s preserves canonical bytes" (faultDescription fault))
    <| fun () ->
        let repo = newTempRepo ()

        try
            let canonical, files = seedCanonicalAndFiles repo
            let ops, calls = buildRecordingOps canonical fault

            let preA, preB = canonicalBytes canonical

            let result =
                publishWithDependencies ops canonical files

            // Typed outcome must be Failed with exact phase preserved.
            match result with
            | AtomicPublishResult.Published _ ->
                failwithf "expected Failed with phase %A, got Published" (faultToPhase fault)
            | AtomicPublishResult.Failed report ->
                let expectedPhase =
                    match faultToPhase fault with
                    | Some p -> p
                    | None -> failwithf "no phase mapped for fault %A" fault

                Expect.hasLength report.Failures 1 "exactly one typed failure"
                let failure = report.Failures.[0]
                Expect.equal failure.Phase expectedPhase "phase is preserved exactly"

                Expect.isFalse
                    (String.IsNullOrEmpty failure.Operation)
                    "operation string is non-empty"

                Expect.notEqual
                    failure.Operation
                    "publish"
                    "operation must not collapse to a generic 'publish'"

                Expect.notEqual
                    failure.ExceptionType
                    ""
                    "exception type is preserved"

                Expect.equal
                    report.CanonicalByteIdenticalAfterFailure
                    true
                    "canonical bytes identical after pre-commit failure"

            let postA, postB = canonicalBytes canonical

            Expect.equal postA preA "a.json unchanged"
            Expect.equal postB preB "b.json unchanged"

            // Operations after the fault: only disposal of the open
            // resource, plus best-effort staging directory cleanup.
            let opsCalls = List.ofSeq calls

            // Find the index of the first faulted call.  Each fault
            // throws inside the matching seam operation; we identify
            // the fault site as the first call that recorded a call
            // signature matching the fault type without a follow-on
            // success continuation.
            let findFaultCall () : int =
                match fault with
                | CreateDirectoryFault ->
                    opsCalls
                    |> List.findIndex (fun c -> c.StartsWith("create-directory:"))
                | FirstOpenFault ->
                    opsCalls
                    |> List.findIndex (fun c -> c = "open:a.json")
                | FirstWriteFault ->
                    opsCalls
                    |> List.findIndex (fun c -> c = "a.json:write")
                | FirstFlushFault ->
                    opsCalls
                    |> List.findIndex (fun c -> c = "a.json:flush")
                | FirstVerifyFault ->
                    opsCalls
                    |> List.findIndex (fun c -> c = "read:a.json")
                | SecondOpenFault ->
                    opsCalls
                    |> List.findIndex (fun c -> c = "open:b.json")
                | SecondWriteFault ->
                    opsCalls
                    |> List.findIndex (fun c -> c = "b.json:write")
                | SecondFlushFault ->
                    opsCalls
                    |> List.findIndex (fun c -> c = "b.json:flush")
                | SecondVerifyFault ->
                    opsCalls
                    |> List.findIndex (fun c -> c = "read:b.json")
                | NoneFault -> -1

            let faultCallIndex = findFaultCall ()

            if faultCallIndex >= 0 then
                // After the fault, only disposal of the already-open
                // handle may run.  No further seam call of any kind
                // (open / write / flush / read / create-directory /
                // canonical install) may be observed.
                let opsAfterFault =
                    opsCalls
                    |> List.skip (faultCallIndex + 1)
                    |> List.filter (fun c -> not (c.EndsWith(":dispose")))

                Expect.isEmpty
                    opsAfterFault
                    (sprintf
                        "operations after fault must be disposal only, got: %A"
                        opsAfterFault)
        finally
            cleanupDir repo

let private nineFaultTests : Test list =
    [ CreateDirectoryFault
      FirstOpenFault
      FirstWriteFault
      FirstFlushFault
      FirstVerifyFault
      SecondOpenFault
      SecondWriteFault
      SecondFlushFault
      SecondVerifyFault ]
    |> List.map faultTest

// -----------------------------------------------------------------------------
// Absent-canonical preservation
// -----------------------------------------------------------------------------

let private absentCanonicalTest =
    testCase "absent canonical pair stays absent on pre-commit failure"
    <| fun () ->
        let repo = newTempRepo ()

        try
            let canonical = Path.Combine(repo, "canonical")
            Directory.CreateDirectory canonical |> ignore

            Expect.isFalse
                (File.Exists(Path.Combine(canonical, "a.json")))
                "canonical a.json is absent"
            Expect.isFalse
                (File.Exists(Path.Combine(canonical, "b.json")))
                "canonical b.json is absent"

            let ops, _ = buildRecordingOps canonical FirstOpenFault

            let result =
                publishWithDependencies ops canonical (stagedPendingFiles ())

            match result with
            | AtomicPublishResult.Failed report ->
                Expect.equal
                    report.CanonicalByteIdenticalAfterFailure
                    true
                    "absent canonical pair is byte-identically absent"
            | AtomicPublishResult.Published _ ->
                failwithf "expected Failed for first-open fault"

            Expect.isFalse
                (File.Exists(Path.Combine(canonical, "a.json")))
                "canonical a.json still absent"
            Expect.isFalse
                (File.Exists(Path.Combine(canonical, "b.json")))
                "canonical b.json still absent"
        finally
            cleanupDir repo

// -----------------------------------------------------------------------------
// Success-path regression: real FileStream + Flush(true)
// -----------------------------------------------------------------------------

let private happyPathTest =
    testCase "success path: real FileStream + Flush(true) + SHA-256 verify"
    <| fun () ->
        let repo = newTempRepo ()

        try
            let canonical, files = seedCanonicalAndFiles repo
            let ops, calls = buildRecordingOps canonical NoneFault

            let result =
                publishWithDependencies ops canonical files

            match result with
            | AtomicPublishResult.Published success ->
                Expect.hasLength
                    success.OutputHashes
                    2
                    "two staged hashes reported"

                let onDiskA = File.ReadAllBytes(Path.Combine(canonical, "a.json"))
                let onDiskB = File.ReadAllBytes(Path.Combine(canonical, "b.json"))
                let expectedA = "XXXX-staged-B\n"B
                let expectedB = "YYYY-staged-B\n"B

                Expect.equal onDiskA expectedA "canonical a.json equals staged body"
                Expect.equal onDiskB expectedB "canonical b.json equals staged body"

                let opsCalls = List.ofSeq calls

                Expect.isTrue
                    (opsCalls |> List.contains "open:a.json")
                    "open:a.json observed"
                Expect.isTrue
                    (opsCalls |> List.contains "open:b.json")
                    "open:b.json observed"
                Expect.isTrue
                    (opsCalls |> List.exists (fun c -> c = "a.json:write"))
                    "handle a.json:write observed"
                Expect.isTrue
                    (opsCalls |> List.exists (fun c -> c = "a.json:flush"))
                    "handle a.json:flush observed"
                Expect.isTrue
                    (opsCalls |> List.exists (fun c -> c = "b.json:write"))
                    "handle b.json:write observed"
                Expect.isTrue
                    (opsCalls |> List.exists (fun c -> c = "b.json:flush"))
                    "handle b.json:flush observed"
                Expect.isTrue
                    (opsCalls |> List.contains "read:a.json")
                    "read:a.json observed"
                Expect.isTrue
                    (opsCalls |> List.contains "read:b.json")
                    "read:b.json observed"
                Expect.isTrue
                    (opsCalls |> List.exists (fun c -> c = "a.json:dispose"))
                    "handle a.json:dispose observed"
                Expect.isTrue
                    (opsCalls |> List.exists (fun c -> c = "b.json:dispose"))
                    "handle b.json:dispose observed"

            | AtomicPublishResult.Failed report ->
                failwithf "expected Published, got Failed: %A" report
        finally
            cleanupDir repo

// -----------------------------------------------------------------------------
// Staging location invariant
// -----------------------------------------------------------------------------

let private stagingParentTest =
    testCase "staging location invariant: parent(stagingDir) = parent(canonicalDir)"
    <| fun () ->
        let repo = newTempRepo ()

        try
            let canonical = Path.Combine(repo, "canonical")
            Directory.CreateDirectory canonical |> ignore

            let createPaths = ResizeArray<string>()
            let captureCreate (path: string) =
                createPaths.Add path
                Directory.CreateDirectory path |> ignore
            let noopOpen (_: string) : IAtomicWriteHandle =
                { new IAtomicWriteHandle with
                    member _.WriteAll _ = ()
                    member _.FlushToDisk () = ()
                    member _.Dispose () = () }
            let noopRead (_: string) = [||]

            let ops =
                { CreateDirectory = captureCreate
                  OpenWrite = noopOpen
                  ReadAllBytes = noopRead }

            let _ =
                publishWithDependencies ops canonical (stagedPendingFiles ())

            Expect.isTrue
                (createPaths.Count >= 1)
                "create-directory called at least once"

            let staging = createPaths.[0]
            let stagingParent = Path.GetDirectoryName staging
            let canonicalParent = Path.GetDirectoryName canonical

            Expect.equal
                stagingParent
                canonicalParent
                "staging parent matches canonical parent"

            let tmpPath = Path.GetFullPath(Path.GetTempPath())
            Expect.isFalse
                (staging.StartsWith(tmpPath, StringComparison.Ordinal))
                "staging is not under system temp"

            let canonicalName = Path.GetFileName canonical
            let stagingName = Path.GetFileName staging
            Expect.isTrue
                (stagingName.StartsWith(canonicalName + ".staging.", StringComparison.Ordinal))
                "staging directory name uses canonical sibling form"
        finally
            cleanupDir repo

// -----------------------------------------------------------------------------
// Operation-order test: confirm specific seam-call order
// -----------------------------------------------------------------------------

let private operationOrderTest =
    testCase "operation order: open:a.json -> write:a.json -> flush:a.json -> dispose:a.json -> read:a.json -> open:b.json"
    <| fun () ->
        let repo = newTempRepo ()

        try
            let canonical, files = seedCanonicalAndFiles repo
            let ops, calls = buildRecordingOps canonical NoneFault

            let _ =
                publishWithDependencies ops canonical files

            let opsCalls = List.ofSeq calls

            let iOpenA = opsCalls |> List.findIndex (fun x -> x = "open:a.json")
            let iWriteA = opsCalls |> List.findIndex (fun x -> x = "a.json:write")
            let iFlushA = opsCalls |> List.findIndex (fun x -> x = "a.json:flush")
            let iDisposeA = opsCalls |> List.findIndex (fun x -> x = "a.json:dispose")
            let iReadA = opsCalls |> List.findIndex (fun x -> x = "read:a.json")
            let iOpenB = opsCalls |> List.findIndex (fun x -> x = "open:b.json")

            Expect.isTrue (iOpenA < iWriteA) "open:a.json precedes write:a.json"
            Expect.isTrue (iWriteA < iFlushA) "write:a.json precedes flush:a.json"
            Expect.isTrue (iFlushA < iDisposeA) "flush:a.json precedes dispose:a.json"
            Expect.isTrue (iDisposeA < iReadA) "dispose:a.json precedes read:a.json"
            Expect.isTrue (iReadA < iOpenB) "read:a.json precedes open:b.json"
        finally
            cleanupDir repo

// -----------------------------------------------------------------------------
// Wire-up
// -----------------------------------------------------------------------------

[<Tests>]
let stagingWriteFlushSeamTests =
    testList
        "FSharpDiagnostics.AtomicPublish.StagingWriteFlushSeam"
        [ yield! nineFaultTests
          absentCanonicalTest
          happyPathTest
          stagingParentTest
          operationOrderTest ]
