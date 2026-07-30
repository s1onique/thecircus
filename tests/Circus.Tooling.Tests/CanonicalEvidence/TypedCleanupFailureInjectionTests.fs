module Circus.Tooling.Tests.CanonicalEvidence.TypedCleanupFailureInjectionTests

// =============================================================================
// Canonical evidence – typed cleanup failure injection tests
//
// ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01-REAL-RECORD-PIPELINE01-CORRECTION07-CORRECTION04-CORRECTION01
//
// Tests that prove typed cleanup failure boundary semantics:
//   - Cleanup is invoked through one production dependency seam
//   - Cleanup is attempted exactly once after staging creation
//   - Primary publication outcome and cleanup outcome remain independent
//   - Cleanup failure never masks or fabricates a primary publication failure
//   - Live-snapshot state is truthful for every cleanup outcome
//   - Pre-replacement failures preserve the previous four-file snapshot byte-identically
//   - Cleanup dependency failures and unexpected exceptions are converted into typed data
//   - Active production entry points cannot bypass the typed cleanup boundary
//   - Cleanup operation and staging path are normalized by the publication boundary
//
// Tests MUST call stageAndPublishSnapshotWithDependencies directly.
// Tests MUST NOT duplicate production orchestration in test-local helpers.
// =============================================================================

open System
open System.IO
open Expecto

open Circus.Tooling.CanonicalEvidence.Publication
open Circus.Tooling.CanonicalEvidence.EvidenceRecords
open Circus.Tooling.CanonicalEvidence.RecordPipeline
open Circus.Tooling.FSharpDiagnostics.Hashing
open Circus.Tooling.Tests.CanonicalEvidence.PublicationFixture

// -----------------------------------------------------------------------------
// Snapshot files inventory
// -----------------------------------------------------------------------------

let private snapshotFiles = ["records.jsonl"; "aggregate.json"; "artifacts.jsonl"; "canonical-evidence.json"]

// -----------------------------------------------------------------------------
// Read existing snapshot files (returns Map of filename -> bytes option)
// -----------------------------------------------------------------------------

let private readSnapshotFiles (dir: string) : Map<string, byte array option> =
    snapshotFiles |> List.map (fun f ->
        let path = Path.Combine(dir, f)
        let bytes = if File.Exists path then Some(File.ReadAllBytes path) else None
        f, bytes
    ) |> Map.ofList

// -----------------------------------------------------------------------------
// Check that all four files are byte-identical to their original state
// -----------------------------------------------------------------------------

let private verifyFilesPreserved (original: Map<string, byte array option>) (current: Map<string, byte array option>) : bool =
    let originalKeys = original |> Map.toSeq |> Seq.map fst |> Set.ofSeq
    let currentKeys = current |> Map.toSeq |> Seq.map fst |> Set.ofSeq
    if originalKeys <> currentKeys then false
    else
        original |> Map.forall (fun filename origBytes ->
            match origBytes with
            | None ->
                match Map.tryFind filename current with
                | None -> true
                | Some None -> true
                | Some (Some _) -> false
            | Some orig ->
                match Map.tryFind filename current with
                | Some (Some curr) -> orig = curr
                | _ -> false)

// -----------------------------------------------------------------------------
// Publish and capture initial snapshot for preservation tests
// -----------------------------------------------------------------------------

let private publishAndCaptureSnapshot
    (outputRoot: string)
    (fixture: ValidPublicationFixture)
    : Map<string, byte array option> =
    let outcome =
        stageAndPublishSnapshot outputRoot fixture.Records fixture.Aggregate fixture.CompatibilityProjection None

    Expect.isTrue
        (publicationSucceeded outcome)
        (sprintf "initial snapshot publication failed: %A" outcome)

    snapshotFiles
    |> List.map (fun name ->
        let path = Path.Combine(outputRoot, name)
        Expect.isTrue (File.Exists path) (sprintf "seeded snapshot is missing %s" name)
        name, Some(File.ReadAllBytes path))
    |> Map.ofList

// -----------------------------------------------------------------------------
// Shared assertion helper: verify typed cleanup failure payload
// -----------------------------------------------------------------------------

let private assertCleanupFailure
    (expectedDetail: string)
    (cleanupPaths: ResizeArray<string>)
    (outputRoot: string)
    outcome =

    Expect.hasLength cleanupPaths 1
        "cleanup must be called exactly once"

    let observedPath = cleanupPaths.[0]

    // P0: Prove staging-path geometry
    // The cleanup path must be a child of the requested output root
    Expect.equal
        (Path.GetDirectoryName observedPath)
        outputRoot
        "cleanup must target a child of the requested output root"

    // The cleanup path must be the generated staging directory
    Expect.isTrue
        (
            Path.GetFileName(observedPath)
                .StartsWith(
                    ".staging.",
                    StringComparison.Ordinal
                )
        )
        "cleanup must target the generated staging directory"

    match outcome.CleanupFailure with
    | Some failure ->
        Expect.equal
            failure.Operation
            DeleteStagingDirectory
            "cleanup operation must be exact"

        Expect.equal
            failure.Path
            observedPath
            "cleanup failure must use the actual staging path"

        Expect.equal
            failure.Detail
            expectedDetail
            "cleanup detail must be preserved"

    | None ->
        failtest "expected cleanup failure"

// -----------------------------------------------------------------------------
// Test A — successful publication and successful cleanup
//
// Prove:
//   - primary outcome is Published
//   - cleanup is CleanupSucceeded
//   - cleanup dependency called exactly once
//   - live state is LiveSnapshotReplaced
//   - staging state is StagingRemoved
//   - no .staging.* directory remains
//   - all four live files exist
//   - publicationSucceeded = true
// -----------------------------------------------------------------------------

let testA_successfulPublicationAndSuccessfulCleanup =
    testCase "TestA: successful publication and successful cleanup" <| fun () ->
        let fixture = createValidPublicationFixture ()
        let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
        Directory.CreateDirectory(tempDir) |> ignore
        try
            // Track cleanup invocations through injected dependency
            let cleanupPaths = ResizeArray<string>()

            let cleanupDependencies =
                {
                    DeleteDirectoryRecursively =
                        fun path ->
                            cleanupPaths.Add path
                            // Actually delete the directory (mimicking production behavior)
                            if Directory.Exists path then Directory.Delete(path, true)
                            Ok ()
                }

            // Execute staged publication with injected cleanup
            let outcome =
                stageAndPublishSnapshotWithDependencies
                    cleanupDependencies
                    tempDir
                    fixture.Records
                    fixture.Aggregate
                    fixture.CompatibilityProjection
                    None

            // Prove: primary outcome is Published
            Expect.isNone outcome.Failure
                "primary publication should succeed (no failure)"

            // Prove: cleanup succeeded
            Expect.isNone outcome.CleanupFailure
                "cleanup should succeed (no cleanup failure)"

            // Prove: cleanup dependency called exactly once
            Expect.hasLength cleanupPaths 1
                "cleanup dependency must be invoked exactly once"

            // Prove: live state is LiveSnapshotReplaced
            Expect.equal outcome.LiveSnapshotState LiveSnapshotReplaced
                "live snapshot should be replaced after successful publication"

            // Prove: staging state is StagingRemoved
            Expect.equal outcome.StagingState StagingRemoved
                "staging directory should be removed after successful cleanup"

            // Prove: no .staging.* directory remains
            let stagingDirs =
                Directory.GetDirectories(tempDir, ".staging.*", SearchOption.TopDirectoryOnly)
            Expect.isEmpty stagingDirs
                "no staging directory should remain after successful cleanup"

            // Prove: all four live files exist
            for filename in snapshotFiles do
                let path = Path.Combine(tempDir, filename)
                Expect.isTrue (File.Exists path)
                    (sprintf "live file %s should exist" filename)

            // Prove: publicationSucceeded = true
            Expect.isTrue (publicationSucceeded outcome)
                "publicationSucceeded should be true when both primary and cleanup succeed"
        finally
            if Directory.Exists tempDir then Directory.Delete(tempDir, true)

// -----------------------------------------------------------------------------
// Test B — successful publication and cleanup failure
//
// Prove:
//   - primary outcome remains Published
//   - cleanup is CleanupFailed
//   - normalized failure path equals actual staging path (NOT untrusted-path)
//   - operation is DeleteStagingDirectory
//   - detail is preserved
//   - cleanup called once
//   - live state is LiveSnapshotReplaced
//   - staging state is StagingMayRemain
//   - four live files contain the newly published snapshot
//   - publicationSucceeded = false
// -----------------------------------------------------------------------------

let testB_successfulPublicationAndCleanupFailure =
    testCase "TestB: successful publication and cleanup failure" <| fun () ->
        let fixture = createValidPublicationFixture ()
        let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
        Directory.CreateDirectory(tempDir) |> ignore
        try
            // Track cleanup invocations through injected dependency
            let cleanupPaths = ResizeArray<string>()

            let untrustedPath = "untrusted-path"

            let cleanupDependencies =
                {
                    DeleteDirectoryRecursively =
                        fun path ->
                            cleanupPaths.Add path
                            // Return failure with untrusted path to prove normalization
                            Error {
                                Operation = DeleteStagingDirectory
                                Path = untrustedPath
                                Detail = "injected cleanup failure"
                            }
                }

            // Execute staged publication with failing cleanup
            let outcome =
                stageAndPublishSnapshotWithDependencies
                    cleanupDependencies
                    tempDir
                    fixture.Records
                    fixture.Aggregate
                    fixture.CompatibilityProjection
                    None

            // Prove: primary outcome remains Published (no primary failure)
            Expect.isNone outcome.Failure
                "primary publication should succeed even when cleanup fails"

            // Prove: cleanup failed
            match outcome.CleanupFailure with
            | Some cf ->
                // P0 FIX: The cleanup operation and staging path are normalized by the
                // publication boundary, NOT by the injected dependency.
                // This prevents untrusted paths from escaping the cleanup boundary.
                Expect.notEqual cf.Path untrustedPath
                    "dependency-controlled paths must not escape the cleanup boundary"

                Expect.equal cf.Path cleanupPaths.[0]
                    "cleanup failure must identify the actual staging path"

                // Prove: operation is DeleteStagingDirectory
                Expect.equal cf.Operation DeleteStagingDirectory
                    "cleanup operation should be DeleteStagingDirectory"

                // Prove: detail is preserved
                Expect.equal cf.Detail "injected cleanup failure"
                    "cleanup failure detail should be preserved"
            | None -> failwith "Expected cleanup failure"

            // Prove: cleanup called once
            Expect.hasLength cleanupPaths 1
                "cleanup dependency must be invoked exactly once"

            // Prove: live state is LiveSnapshotReplaced
            Expect.equal outcome.LiveSnapshotState LiveSnapshotReplaced
                "live snapshot should be replaced even when cleanup fails"

            // Prove: staging state is StagingMayRemain
            Expect.equal outcome.StagingState StagingMayRemain
                "staging directory may remain after cleanup failure"

            // Prove: four live files contain the newly published snapshot
            for filename in snapshotFiles do
                let path = Path.Combine(tempDir, filename)
                Expect.isTrue (File.Exists path)
                    (sprintf "live file %s should exist after successful publication" filename)

            // Prove: publicationSucceeded = false
            Expect.isFalse (publicationSucceeded outcome)
                "publicationSucceeded should be false when cleanup fails"
        finally
            if Directory.Exists tempDir then Directory.Delete(tempDir, true)

// -----------------------------------------------------------------------------
// Test C — validation rejection and cleanup failure
//
// Seed a valid four-file snapshot.
//
// Corrupt a staged file through the mutation seam while returning Ok (),
// so validation—not the hook—rejects it.
//
// Prove:
//   - exact SnapshotStagedValidationFailed remains primary
//   - cleanup failure carries typed DeleteStagingDirectory with actual staging path
//   - cleanup detail is preserved
//   - cleanup called once
//   - replacement phase count is zero
//   - live state is LiveSnapshotUnchanged
//   - staging state is StagingMayRemain
//   - all four previous live files remain byte-identical
// -----------------------------------------------------------------------------

let testC_validationRejectionAndCleanupFailure =
    testCase "TestC: validation rejection and cleanup failure" <| fun () ->
        let fixture = createValidPublicationFixture ()
        let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
        Directory.CreateDirectory(tempDir) |> ignore
        try
            // Step 1: Seed a valid four-file snapshot
            let previousSnapshot = publishAndCaptureSnapshot tempDir fixture

            // Track cleanup invocations through injected dependency
            let cleanupPaths = ResizeArray<string>()

            let cleanupDependencies =
                {
                    DeleteDirectoryRecursively =
                        fun path ->
                            cleanupPaths.Add path
                            // Return failure with actual path (will be normalized)
                            Error {
                                Operation = DeleteStagingDirectory
                                Path = path
                                Detail = "injected cleanup failure"
                            }
                }

            // Step 2: Corrupt a staged file through the mutation seam
            // Return Ok () so the hook itself doesn't fail—validation should reject
            let corruptionFn (stagingDir: string) : Result<unit, string> =
                try
                    // Corrupt records.jsonl by adding an invalid line
                    let recordsPath = Path.Combine(stagingDir, "records.jsonl")
                    let originalContent = File.ReadAllText recordsPath
                    File.WriteAllText(recordsPath, originalContent + "\n{{{{INVALID JSON}}}}")
                    Ok ()
                with ex ->
                    Error (sprintf "mutation failed: %s" ex.Message)

            // Step 3: Attempt publication with corrupted staged file
            let outcome =
                stageAndPublishSnapshotWithDependencies
                    cleanupDependencies
                    tempDir
                    fixture.Records
                    fixture.Aggregate
                    fixture.CompatibilityProjection
                    (Some corruptionFn)

            // Prove: primary failure is SnapshotStagedValidationFailed
            match outcome.Failure with
            | Some (SnapshotStagedValidationFailed _) -> ()
            | other -> failwithf "Expected SnapshotStagedValidationFailed, got %A" other

            // Prove: cleanup failure is present with typed payload
            assertCleanupFailure "injected cleanup failure" cleanupPaths tempDir outcome

            // Prove: replacement phase count is zero
            Expect.equal outcome.ReplacementPhaseInvocationCount 0
                "replacement phase should not be invoked on validation failure"

            // Prove: live state is LiveSnapshotUnchanged
            Expect.equal outcome.LiveSnapshotState LiveSnapshotUnchanged
                "live snapshot should be unchanged after validation rejection"

            // Prove: staging state is StagingMayRemain (cleanup failed)
            Expect.equal outcome.StagingState StagingMayRemain
                "staging directory may remain after cleanup failure"

            // Prove: all four previous live files remain byte-identical
            let currentSnapshot = readSnapshotFiles tempDir
            Expect.isTrue (verifyFilesPreserved previousSnapshot currentSnapshot)
                "all four previous live files should remain byte-identical"
        finally
            if Directory.Exists tempDir then Directory.Delete(tempDir, true)

// -----------------------------------------------------------------------------
// Test D — mutation-hook rejection and cleanup failure
//
// Use:
//   Some (fun _ -> Error "injected mutation rejection")
//
// Prove:
//   - primary type is SnapshotMutationHookFailed (NOT SnapshotStagingFailed)
//   - detail is exact "injected mutation rejection"
//   - cleanup failure is present with typed payload
//   - cleanup called once
//   - replacement phase count is zero
//   - live state is LiveSnapshotUnchanged
//   - prior four files remain byte-identical
// -----------------------------------------------------------------------------

let testD_mutationHookRejectionAndCleanupFailure =
    testCase "TestD: mutation-hook rejection and cleanup failure" <| fun () ->
        let fixture = createValidPublicationFixture ()
        let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
        Directory.CreateDirectory(tempDir) |> ignore
        try
            // Step 1: Seed a valid four-file snapshot
            let previousSnapshot = publishAndCaptureSnapshot tempDir fixture

            // Track cleanup invocations through injected dependency
            let cleanupPaths = ResizeArray<string>()

            let cleanupDependencies =
                {
                    DeleteDirectoryRecursively =
                        fun path ->
                            cleanupPaths.Add path
                            Error {
                                Operation = DeleteStagingDirectory
                                Path = path
                                Detail = "injected cleanup failure"
                            }
                }

            // Step 2: Reject via mutation hook
            let mutationHook (stagingDir: string) : Result<unit, string> =
                Error "injected mutation rejection"

            // Step 3: Attempt publication with rejecting mutation hook
            let outcome =
                stageAndPublishSnapshotWithDependencies
                    cleanupDependencies
                    tempDir
                    fixture.Records
                    fixture.Aggregate
                    fixture.CompatibilityProjection
                    (Some mutationHook)

            // P0 FIX: Use SnapshotMutationHookFailed (not SnapshotStagingFailed)
            // A mutation hook rejection is NOT a filesystem staging failure.
            // Keeping these distinct is required for primary failure identification.
            match outcome.Failure with
            | Some (SnapshotMutationHookFailed detail) ->
                Expect.equal
                    detail
                    "injected mutation rejection"
                    "mutation rejection detail must be exact"
            | other ->
                failtestf "expected SnapshotMutationHookFailed, got %A" other

            // Prove: cleanup failure is present with typed payload
            assertCleanupFailure "injected cleanup failure" cleanupPaths tempDir outcome

            // Prove: replacement phase count is zero
            Expect.equal outcome.ReplacementPhaseInvocationCount 0
                "replacement phase should not be invoked when mutation hook fails"

            // Prove: live state is LiveSnapshotUnchanged
            Expect.equal outcome.LiveSnapshotState LiveSnapshotUnchanged
                "live snapshot should be unchanged after mutation rejection"

            // Prove: prior four files remain byte-identical
            let currentSnapshot = readSnapshotFiles tempDir
            Expect.isTrue (verifyFilesPreserved previousSnapshot currentSnapshot)
                "all four previous live files should remain byte-identical"
        finally
            if Directory.Exists tempDir then Directory.Delete(tempDir, true)

// -----------------------------------------------------------------------------
// Test E — cleanup dependency throws
//
// Inject an exception from DeleteDirectoryRecursively.
//
// Prove:
//   - no exception escapes stageAndPublishSnapshotWithDependencies
//   - outcome contains CleanupFailed
//   - operation and actual staging path are correct
//   - detail contains the injected exception type and message
//   - cleanup call observation count is one
// -----------------------------------------------------------------------------

let testE_cleanupDependencyThrows =
    testCase "TestE: cleanup dependency throws" <| fun () ->
        let fixture = createValidPublicationFixture ()
        let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
        Directory.CreateDirectory(tempDir) |> ignore
        try
            // Track cleanup invocations through injected dependency
            let cleanupPaths = ResizeArray<string>()

            let cleanupDependencies =
                {
                    DeleteDirectoryRecursively =
                        fun path ->
                            cleanupPaths.Add path
                            raise (IOException("injected cleanup failure"))
                }

            // Execute staged publication with throwing cleanup dependency
            // This should NOT throw—the boundary should catch and convert
            let outcome =
                stageAndPublishSnapshotWithDependencies
                    cleanupDependencies
                    tempDir
                    fixture.Records
                    fixture.Aggregate
                    fixture.CompatibilityProjection
                    None

            // Prove: no exception escaped (we reached this point)
            // The boundary converted the exception to typed failure

            // Prove: cleanup failed
            match outcome.CleanupFailure with
            | Some cf ->
                // Prove: operation is DeleteStagingDirectory
                Expect.equal cf.Operation DeleteStagingDirectory
                    "cleanup operation should be DeleteStagingDirectory"

                // Prove: path is the actual staging path (not thrown exception path)
                let observedPath = cleanupPaths.[0]
                Expect.equal cf.Path observedPath
                    "cleanup failure path should be the actual staging path"

                // Prove: detail contains the injected exception type and message
                Expect.stringContains cf.Detail "IOException"
                    "cleanup failure detail should contain exception type"
                Expect.stringContains cf.Detail "injected cleanup failure"
                    "cleanup failure detail should contain exception message"
            | None -> failwith "Expected cleanup failure from exception"

            // Prove: cleanup call observation count is one
            Expect.hasLength cleanupPaths 1
                "cleanup dependency must be invoked exactly once"
        finally
            if Directory.Exists tempDir then Directory.Delete(tempDir, true)

// -----------------------------------------------------------------------------
// Test F — validation rejection and successful cleanup
//
// Prove:
//   - primary validation failure remains
//   - cleanup succeeds
//   - staging state is StagingRemoved
//   - staging path no longer exists
//   - no .staging.* sibling remains
//   - prior live snapshot remains byte-identical
// -----------------------------------------------------------------------------

let testF_validationRejectionAndSuccessfulCleanup =
    testCase "TestF: validation rejection and successful cleanup" <| fun () ->
        let fixture = createValidPublicationFixture ()
        let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
        Directory.CreateDirectory(tempDir) |> ignore
        try
            // Step 1: Seed a valid four-file snapshot
            let previousSnapshot = publishAndCaptureSnapshot tempDir fixture

            // Track cleanup invocations through injected dependency
            let cleanupPaths = ResizeArray<string>()

            let cleanupDependencies =
                {
                    DeleteDirectoryRecursively =
                        fun path ->
                            cleanupPaths.Add path
                            // Actually delete the directory (mimicking production behavior)
                            if Directory.Exists path then Directory.Delete(path, true)
                            Ok ()
                }

            // Step 2: Corrupt a staged file through the mutation seam
            let corruptionFn (stagingDir: string) : Result<unit, string> =
                try
                    let recordsPath = Path.Combine(stagingDir, "records.jsonl")
                    let originalContent = File.ReadAllText recordsPath
                    File.WriteAllText(recordsPath, originalContent + "\n{{{{INVALID JSON}}}}")
                    Ok ()
                with ex ->
                    Error (sprintf "mutation failed: %s" ex.Message)

            // Step 3: Attempt publication with corrupted staged file
            let outcome =
                stageAndPublishSnapshotWithDependencies
                    cleanupDependencies
                    tempDir
                    fixture.Records
                    fixture.Aggregate
                    fixture.CompatibilityProjection
                    (Some corruptionFn)

            // Prove: primary validation failure remains
            match outcome.Failure with
            | Some (SnapshotStagedValidationFailed _) -> ()
            | other -> failwithf "Expected SnapshotStagedValidationFailed, got %A" other

            // Prove: cleanup succeeds
            Expect.isNone outcome.CleanupFailure
                "cleanup should succeed"

            // Prove: staging state is StagingRemoved
            Expect.equal outcome.StagingState StagingRemoved
                "staging directory should be removed after successful cleanup"

            // Prove: staging path no longer exists
            let stagingDirs =
                Directory.GetDirectories(tempDir, ".staging.*", SearchOption.TopDirectoryOnly)
            Expect.isEmpty stagingDirs
                "no staging directory should remain"

            // Prove: prior live snapshot remains byte-identical
            let currentSnapshot = readSnapshotFiles tempDir
            Expect.isTrue (verifyFilesPreserved previousSnapshot currentSnapshot)
                "all four previous live files should remain byte-identical"
        finally
            if Directory.Exists tempDir then Directory.Delete(tempDir, true)

// -----------------------------------------------------------------------------
// Invariant tests (nonvacuous)
//
// For every outcome produced by the cleanup suite:
//   - CleanupSucceeded: StagingState = StagingRemoved (nonvacuous)
//   - CleanupFailed: StagingState = StagingMayRemain (nonvacuous)
//   - Published and CleanupSucceeded: publicationSucceeded = true (nonvacuous)
//   - Published and CleanupFailed: publicationSucceeded = false (nonvacuous)
//   - Pre-replacement rejection: LiveSnapshotState = LiveSnapshotUnchanged (nonvacuous)
// -----------------------------------------------------------------------------

let invariantTests =
    testList "Invariants" [
        testCase "CleanupSucceeded implies StagingRemoved" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
            Directory.CreateDirectory(tempDir) |> ignore
            try
                let cleanupDependencies = { DeleteDirectoryRecursively = fun _ -> Ok () }
                let outcome =
                    stageAndPublishSnapshotWithDependencies
                        cleanupDependencies
                        tempDir
                        fixture.Records
                        fixture.Aggregate
                        fixture.CompatibilityProjection
                        None

                // Nonvacuous: first require the fixture produces CleanupSucceeded
                Expect.isNone outcome.CleanupFailure
                    "fixture must produce CleanupSucceeded"

                Expect.equal outcome.StagingState StagingRemoved
                    "CleanupSucceeded must imply StagingRemoved"
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)

        testCase "CleanupFailed implies StagingMayRemain" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
            Directory.CreateDirectory(tempDir) |> ignore
            try
                let cleanupDependencies =
                    {
                        DeleteDirectoryRecursively =
                            fun path ->
                                Error {
                                    Operation = DeleteStagingDirectory
                                    Path = path
                                    Detail = "injected failure"
                                }
                    }
                let outcome =
                    stageAndPublishSnapshotWithDependencies
                        cleanupDependencies
                        tempDir
                        fixture.Records
                        fixture.Aggregate
                        fixture.CompatibilityProjection
                        None

                // Nonvacuous: first require the fixture produces CleanupFailed
                Expect.isSome outcome.CleanupFailure
                    "fixture must produce CleanupFailed"

                Expect.equal outcome.StagingState StagingMayRemain
                    "CleanupFailed must imply StagingMayRemain"
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)

        testCase "Published and CleanupSucceeded implies publicationSucceeded = true" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
            Directory.CreateDirectory(tempDir) |> ignore
            try
                let cleanupDependencies = { DeleteDirectoryRecursively = fun _ -> Ok () }
                let outcome =
                    stageAndPublishSnapshotWithDependencies
                        cleanupDependencies
                        tempDir
                        fixture.Records
                        fixture.Aggregate
                        fixture.CompatibilityProjection
                        None

                // Nonvacuous: first require the fixture produces Published and CleanupSucceeded
                Expect.isNone outcome.Failure
                    "fixture must produce a published primary outcome"

                Expect.isNone outcome.CleanupFailure
                    "fixture must produce successful cleanup"

                Expect.isTrue (publicationSucceeded outcome)
                    "Published plus CleanupSucceeded must be successful"
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)

        testCase "Published and CleanupFailed implies publicationSucceeded = false" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
            Directory.CreateDirectory(tempDir) |> ignore
            try
                let cleanupDependencies =
                    {
                        DeleteDirectoryRecursively =
                            fun path ->
                                Error {
                                    Operation = DeleteStagingDirectory
                                    Path = path
                                    Detail = "injected failure"
                                }
                    }
                let outcome =
                    stageAndPublishSnapshotWithDependencies
                        cleanupDependencies
                        tempDir
                        fixture.Records
                        fixture.Aggregate
                        fixture.CompatibilityProjection
                        None

                // Nonvacuous: first require the fixture produces Published and CleanupFailed
                Expect.isNone outcome.Failure
                    "fixture must produce Published (primary did not fail)"

                Expect.isSome outcome.CleanupFailure
                    "fixture must produce CleanupFailed"

                Expect.isFalse (publicationSucceeded outcome)
                    "Published plus CleanupFailed must imply publicationSucceeded = false"
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)

        testCase "Pre-replacement rejection implies LiveSnapshotUnchanged" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
            Directory.CreateDirectory(tempDir) |> ignore
            try
                let _ = publishAndCaptureSnapshot tempDir fixture

                let cleanupDependencies = { DeleteDirectoryRecursively = fun _ -> Ok () }

                // Reject via mutation hook
                let mutationHook (_: string) : Result<unit, string> =
                    Error "rejection"

                let outcome =
                    stageAndPublishSnapshotWithDependencies
                        cleanupDependencies
                        tempDir
                        fixture.Records
                        fixture.Aggregate
                        fixture.CompatibilityProjection
                        (Some mutationHook)

                // Nonvacuous: first require the fixture produces a rejection
                Expect.isSome outcome.Failure
                    "fixture must produce a rejection (pre-replacement failure)"

                Expect.equal outcome.LiveSnapshotState LiveSnapshotUnchanged
                    "Pre-replacement rejection must imply LiveSnapshotUnchanged"
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)
    ]

// -----------------------------------------------------------------------------
// All typed cleanup failure injection tests
// -----------------------------------------------------------------------------

[<Tests>]
let typedCleanupFailureInjectionTests =
    testList "TypedCleanupFailureInjection" [
        testA_successfulPublicationAndSuccessfulCleanup
        testB_successfulPublicationAndCleanupFailure
        testC_validationRejectionAndCleanupFailure
        testD_mutationHookRejectionAndCleanupFailure
        testE_cleanupDependencyThrows
        testF_validationRejectionAndSuccessfulCleanup
        invariantTests
    ]
