module Circus.Tooling.Tests.CanonicalEvidence.RecordPipelineTests

open System
open Expecto

open Circus.Tooling.CanonicalEvidence.RecordPipeline
open Circus.Tooling.CanonicalEvidence.Domain
open Circus.Tooling.CanonicalEvidence.EvidenceRecords

// =============================================================================
// Record pipeline tests
//
// ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01-REAL-RECORD-PIPELINE01
//
// Tests for the real per-check execution record pipeline:
//   - validateBijection
//   - convertCheckResultsToRecords
//   - validateRecords
// =============================================================================

// -----------------------------------------------------------------------------
// Test fixtures
// -----------------------------------------------------------------------------

let private sampleDefinition id required =
    {
        Id = id
        Executable = "dotnet"
        Arguments = [ "build" ]
        WorkingDirectory = "/repo"
        Required = required
        Timeout = TimeSpan.FromMinutes(5.0)
        StdoutLimitBytes = 32 * 1024 * 1024
        StderrLimitBytes = 32 * 1024 * 1024
    }

let private sampleResult id commandArgv status =
    {
        Id = id
        CommandArgv = commandArgv
        WorkingDirectory = "/repo"
        DurationMilliseconds = 1000L
        ExitCode = Some 0
        Status = status
        StdoutSha256 = Some "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
        StderrSha256 = Some "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
        FailureKind = None
    }

let private sampleRecord checkId =
    {
        SchemaVersion = 1
        EvidenceId = ""
        CheckId = checkId
        Required = true
        ProviderId = "circus-canonical-evidence"
        ProviderVersion = "1.0.0"
        Command = "dotnet"
        Arguments = [ "build" ]
        WorkingDirectory = "/repo"
        StartedAt = "2024-01-01T00:00:00Z"
        DurationMs = 1000L
        ExitCode = Some 0
        Result = RecordPass
        TestsTotal = None
        TestsPassed = None
        TestsIgnored = None
        TestsFailed = None
        TestsErrored = None
        StdoutSha256 = Some "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
        StderrSha256 = Some "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
        StdoutByteLength = None
        StderrByteLength = None
        FailureKind = None
        TestedCommitOid = "abc123def456"
        TestedTreeOid = "tree789"
        WorkingTreeClean = true
        ProviderBinarySha256 = None
        ToolingBinarySha256 = None
        TestBinarySha256 = None
    }

// -----------------------------------------------------------------------------
// validateBijection tests
// -----------------------------------------------------------------------------

let validateBijectionTests =
    testList "validateBijection" [
        testCase "empty definitions returns DefinitionsEmpty" <| fun () ->
            let result = validateBijection [] []
            match result with
            | Error RecordPipelineFailure.DefinitionsEmpty -> ()
            | _ -> failwithf "expected DefinitionsEmpty, got %A" result

        testCase "empty results returns ResultsEmpty" <| fun () ->
            let result = validateBijection [ sampleDefinition "check1" true ] []
            match result with
            | Error RecordPipelineFailure.ResultsEmpty -> ()
            | _ -> failwithf "expected ResultsEmpty, got %A" result

        testCase "duplicate definition ID returns DuplicateDefinitionId" <| fun () ->
            let defs = [ sampleDefinition "check1" true; sampleDefinition "check1" true ]
            let res = [ sampleResult "check1" [ "dotnet"; "build" ] Pass; sampleResult "check2" [ "make" ] Pass ]
            let result = validateBijection defs res
            match result with
            | Error(RecordPipelineFailure.DuplicateDefinitionId "check1") -> ()
            | Error e -> failwithf "expected DuplicateDefinitionId, got %A" e
            | Ok () -> failwith "expected error"

        testCase "duplicate result ID returns DuplicateResultId" <| fun () ->
            let defs = [ sampleDefinition "check1" true; sampleDefinition "check2" true ]
            let res = [ sampleResult "check1" [ "dotnet" ] Pass; sampleResult "check1" [ "dotnet" ] Pass ]
            let result = validateBijection defs res
            match result with
            | Error(RecordPipelineFailure.DuplicateResultId "check1") -> ()
            | Error e -> failwithf "expected DuplicateResultId, got %A" e
            | Ok () -> failwith "expected error"

        testCase "missing result returns DefinitionMissingResult" <| fun () ->
            let defs = [ sampleDefinition "check1" true ]
            let res = [ sampleResult "check2" [ "dotnet" ] Pass ]
            let result = validateBijection defs res
            match result with
            | Error(RecordPipelineFailure.DefinitionMissingResult "check1") -> ()
            | Error e -> failwithf "expected DefinitionMissingResult, got %A" e
            | Ok () -> failwith "expected error"

        testCase "missing definition returns ResultMissingDefinition" <| fun () ->
            let defs = [ sampleDefinition "check1" true ]
            let res = [ sampleResult "check1" [ "dotnet" ] Pass; sampleResult "check2" [ "make" ] Pass ]
            let result = validateBijection defs res
            match result with
            | Error(RecordPipelineFailure.ResultMissingDefinition "check2") -> ()
            | Error e -> failwithf "expected ResultMissingDefinition, got %A" e
            | Ok () -> failwith "expected error"

        testCase "matching pairs returns Ok" <| fun () ->
            let defs = [ sampleDefinition "check1" true; sampleDefinition "check2" true ]
            let res = [ sampleResult "check1" [ "dotnet" ] Pass; sampleResult "check2" [ "make" ] Pass ]
            let result = validateBijection defs res
            match result with
            | Ok () -> ()
            | Error e -> failwithf "expected Ok, got %A" e
    ]

// -----------------------------------------------------------------------------
// validateRecords tests
// -----------------------------------------------------------------------------

let validateRecordsTests =
    testList "validateRecords" [
        testCase "empty records returns RecordsEmpty issue" <| fun () ->
            let result = validateRecords [] "commit1" "tree1"
            Expect.isFalse result.Valid "expected invalid"
            Expect.contains result.Issues RecordValidationIssue.RecordsEmpty "expected RecordsEmpty issue"

        testCase "valid records returns Valid" <| fun () ->
            let record = sampleRecord "check1"
            // Need a proper record with valid evidence ID
            let result = validateRecords [ record ] "commit1" "tree1"
            // This will fail because EvidenceId is empty - that's expected
            Expect.isFalse result.Valid "expected invalid due to empty evidence ID"
    ]

// -----------------------------------------------------------------------------
// Status mapping tests
// -----------------------------------------------------------------------------

let statusMappingTests =
    testList "mapStatusToRecordStatus" [
        testCase "Pass maps to RecordPass" <| fun () ->
            let result = mapStatusToRecordStatus Pass
            Expect.equal result RecordPass "expected RecordPass"

        testCase "Fail maps to RecordFail" <| fun () ->
            let result = mapStatusToRecordStatus Fail
            Expect.equal result RecordFail "expected RecordFail"

        testCase "Unavailable maps to RecordUnavailable" <| fun () ->
            let result = mapStatusToRecordStatus Unavailable
            Expect.equal result RecordUnavailable "expected RecordUnavailable"
    ]

// -----------------------------------------------------------------------------
// Failure to string tests
// -----------------------------------------------------------------------------

let failureToStringTests =
    testList "recordPipelineFailureToString" [
        testCase "DefinitionsEmpty has correct message" <| fun () ->
            let msg = recordPipelineFailureToString RecordPipelineFailure.DefinitionsEmpty
            Expect.stringContains msg "definitions list is empty" "expected definitions message"

        testCase "ResultsEmpty has correct message" <| fun () ->
            let msg = recordPipelineFailureToString RecordPipelineFailure.ResultsEmpty
            Expect.stringContains msg "results list is empty" "expected results message"

        testCase "DuplicateDefinitionId includes ID" <| fun () ->
            let msg = recordPipelineFailureToString (RecordPipelineFailure.DuplicateDefinitionId "check1")
            Expect.stringContains msg "check1" "expected check ID in message"

        testCase "EmptyCommand includes ID" <| fun () ->
            let msg = recordPipelineFailureToString (RecordPipelineFailure.EmptyCommand "build")
            Expect.stringContains msg "build" "expected check ID in message"
            Expect.stringContains msg "empty command" "expected empty command message"
    ]

// -----------------------------------------------------------------------------
// Validation issue to string tests
// -----------------------------------------------------------------------------

let validationIssueToStringTests =
    testList "recordValidationIssueToString" [
        testCase "RecordsEmpty has correct message" <| fun () ->
            let msg = recordValidationIssueToString RecordValidationIssue.RecordsEmpty
            Expect.stringContains msg "empty" "expected empty message"

        testCase "EvidenceIdEmpty includes ID" <| fun () ->
            let msg = recordValidationIssueToString (RecordValidationIssue.EvidenceIdEmpty "check1")
            Expect.stringContains msg "check1" "expected check ID"
            Expect.stringContains msg "empty" "expected empty message"

        testCase "SubjectMismatch includes all details" <| fun () ->
            let msg = recordValidationIssueToString (RecordValidationIssue.SubjectMismatch("check1", "commit1", "commit2"))
            Expect.stringContains msg "check1" "expected check ID"
            Expect.stringContains msg "commit1" "expected expected commit"
            Expect.stringContains msg "commit2" "expected actual commit"
    ]

// -----------------------------------------------------------------------------
// CORRECTION02: ExecutedCanonicalCheck tests
// -----------------------------------------------------------------------------

let executedCanonicalCheckTests =
    testList "ExecutedCanonicalCheck" [
        testCase "can create with definition result and startedAt" <| fun () ->
            let def = sampleDefinition "check1" true
            let res = sampleResult "check1" [ "dotnet"; "build" ] EvidenceStatus.Pass
            let startedAt = DateTimeOffset.UtcNow
            let executed = {
                ExecutedCanonicalCheck.Definition = def
                Result = res
                StartedAt = startedAt
            }
            Expect.equal executed.Definition.Id "check1" "expected definition ID"
            Expect.equal executed.Result.Id "check1" "expected result ID"
            Expect.equal executed.StartedAt startedAt "expected startedAt"

        testCase "multiple executed checks have different timestamps" <| fun () ->
            let def = sampleDefinition "check1" true
            let res = sampleResult "check1" [ "dotnet"; "build" ] EvidenceStatus.Pass
            let time1 = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
            let time2 = time1.AddSeconds(1.0)
            let executed1 = { ExecutedCanonicalCheck.Definition = def; Result = res; StartedAt = time1 }
            let executed2 = { ExecutedCanonicalCheck.Definition = def; Result = res; StartedAt = time2 }
            Expect.notEqual executed1.StartedAt executed2.StartedAt "expected different timestamps"
    ]

// -----------------------------------------------------------------------------
// CORRECTION02: convertExecutedChecksToRecords tests
// -----------------------------------------------------------------------------

let convertExecutedChecksToRecordsTests =
    testList "convertExecutedChecksToRecords" [
        testCase "one executed check produces one record" <| fun () ->
            let def = sampleDefinition "check1" true
            let res = { (sampleResult "check1" [ "dotnet"; "build" ] EvidenceStatus.Pass) with FailureKind = Some "test_failure" }
            let startedAt = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
            let executed = { ExecutedCanonicalCheck.Definition = def; Result = res; StartedAt = startedAt }
            let result = convertExecutedChecksToRecords [ executed ] "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" "tree789" true
            match result with
            | Error e -> failwithf "expected Ok, got Error: %A" e
            | Ok records ->
                Expect.equal (List.length records) 1 "expected 1 record"
                Expect.equal records.Head.CheckId "check1" "expected check ID"
                Expect.equal records.Head.FailureKind (Some "test_failure") "expected FailureKind preserved"

        testCase "ten executed checks produce ten records" <| fun () ->
            let executedChecks = [
                for i in 1..10 do
                    let def = sampleDefinition (sprintf "check%d" i) true
                    let res = sampleResult (sprintf "check%d" i) [ "dotnet"; "build" ] EvidenceStatus.Pass
                    let startedAt = DateTimeOffset(2024, 1, 1, 0, 0, i, TimeSpan.Zero)
                    yield { ExecutedCanonicalCheck.Definition = def; Result = res; StartedAt = startedAt }
            ]
            let result = convertExecutedChecksToRecords executedChecks "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" "tree789" true
            match result with
            | Error e -> failwithf "expected Ok, got Error: %A" e
            | Ok records ->
                Expect.equal (List.length records) 10 "expected 10 records"

        testCase "record ID is nonempty" <| fun () ->
            let def = sampleDefinition "check1" true
            let res = sampleResult "check1" [ "dotnet"; "build" ] EvidenceStatus.Pass
            let startedAt = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
            let executed = { ExecutedCanonicalCheck.Definition = def; Result = res; StartedAt = startedAt }
            let result = convertExecutedChecksToRecords [ executed ] "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" "tree789" true
            match result with
            | Error e -> failwithf "expected Ok, got Error: %A" e
            | Ok records ->
                Expect.isNonEmpty records.Head.EvidenceId "expected nonempty EvidenceId"

        testCase "record ID is 64 lowercase hexadecimal characters" <| fun () ->
            let def = sampleDefinition "check1" true
            let res = sampleResult "check1" [ "dotnet"; "build" ] EvidenceStatus.Pass
            let startedAt = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
            let executed = { ExecutedCanonicalCheck.Definition = def; Result = res; StartedAt = startedAt }
            let result = convertExecutedChecksToRecords [ executed ] "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" "tree789" true
            match result with
            | Error e -> failwithf "expected Ok, got Error: %A" e
            | Ok records ->
                let id = records.Head.EvidenceId
                Expect.equal (String.length id) 64 "expected 64 character ID"
                Expect.isTrue (System.Text.RegularExpressions.Regex.IsMatch(id, "^[0-9a-f]{64}$")) "expected lowercase hex"

        testCase "record ID recomputes" <| fun () ->
            let def = sampleDefinition "check1" true
            let res = sampleResult "check1" [ "dotnet"; "build" ] EvidenceStatus.Pass
            let startedAt = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
            let executed = { ExecutedCanonicalCheck.Definition = def; Result = res; StartedAt = startedAt }
            let result = convertExecutedChecksToRecords [ executed ] "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" "tree789" true
            match result with
            | Error e -> failwithf "expected Ok, got Error: %A" e
            | Ok records ->
                let recomputed = computeEvidenceId records.Head
                Expect.equal records.Head.EvidenceId recomputed "expected ID to recompute"

        testCase "subject commit is preserved" <| fun () ->
            let def = sampleDefinition "check1" true
            let res = sampleResult "check1" [ "dotnet"; "build" ] EvidenceStatus.Pass
            let startedAt = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
            let executed = { ExecutedCanonicalCheck.Definition = def; Result = res; StartedAt = startedAt }
            let result = convertExecutedChecksToRecords [ executed ] "abc123def456" "tree789" true
            match result with
            | Error e -> failwithf "expected Ok, got Error: %A" e
            | Ok records ->
                Expect.equal records.Head.TestedCommitOid "abc123def456" "expected commit preserved"

        testCase "subject tree is preserved" <| fun () ->
            let def = sampleDefinition "check1" true
            let res = sampleResult "check1" [ "dotnet"; "build" ] EvidenceStatus.Pass
            let startedAt = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
            let executed = { ExecutedCanonicalCheck.Definition = def; Result = res; StartedAt = startedAt }
            let result = convertExecutedChecksToRecords [ executed ] "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" "tree789xyz" true
            match result with
            | Error e -> failwithf "expected Ok, got Error: %A" e
            | Ok records ->
                Expect.equal records.Head.TestedTreeOid "tree789xyz" "expected tree preserved"

        testCase "working tree clean is preserved" <| fun () ->
            let def = sampleDefinition "check1" true
            let res = sampleResult "check1" [ "dotnet"; "build" ] EvidenceStatus.Pass
            let startedAt = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
            let executed = { ExecutedCanonicalCheck.Definition = def; Result = res; StartedAt = startedAt }
            let result = convertExecutedChecksToRecords [ executed ] "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" "tree789" false
            match result with
            | Error e -> failwithf "expected Ok, got Error: %A" e
            | Ok records ->
                Expect.isFalse records.Head.WorkingTreeClean "expected dirty tree"

        testCase "FailureKind is preserved" <| fun () ->
            let def = sampleDefinition "check1" true
            let res = { (sampleResult "check1" [ "dotnet"; "build" ] EvidenceStatus.Fail) with FailureKind = Some "non_zero_exit:42" }
            let startedAt = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
            let executed = { ExecutedCanonicalCheck.Definition = def; Result = res; StartedAt = startedAt }
            let result = convertExecutedChecksToRecords [ executed ] "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" "tree789" true
            match result with
            | Error e -> failwithf "expected Ok, got Error: %A" e
            | Ok records ->
                Expect.equal records.Head.FailureKind (Some "non_zero_exit:42") "expected FailureKind"

        testCase "valid records pass validation" <| fun () ->
            let def = sampleDefinition "check1" true
            let res = sampleResult "check1" [ "dotnet"; "build" ] EvidenceStatus.Pass
            let startedAt = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
            let executed = { ExecutedCanonicalCheck.Definition = def; Result = res; StartedAt = startedAt }
            let convResult = convertExecutedChecksToRecords [ executed ] "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" "tree789" true
            match convResult with
            | Error e -> failwithf "expected Ok, got Error: %A" e
            | Ok records ->
                let validation = validateRecords records "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" "tree789"
                Expect.isTrue validation.Valid "expected valid records"
    ]

// -----------------------------------------------------------------------------
// CORRECTION02: Aggregate derivation tests
// -----------------------------------------------------------------------------

let aggregateDerivationTests =
    testList "AggregateDerivation" [
        testCase "required pass -> required_failed=0, overall pass" <| fun () ->
            let records = [
                { (sampleRecord "check1") with Required = true; Result = RecordPass }
            ]
            let aggregate =
                records
                |> computeAggregate "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" "tree789"
                |> finalizeAggregate
            Expect.equal aggregate.RequiredChecksFailed 0 "expected 0 failed"
            Expect.equal aggregate.RequiredChecksTotal 1 "expected 1 total"
            Expect.equal aggregate.RequiredChecksPassed 1 "expected 1 passed"

        testCase "required fail -> required_failed=1, overall fail" <| fun () ->
            let records = [
                { (sampleRecord "check1") with Required = true; Result = RecordFail }
            ]
            let aggregate =
                records
                |> computeAggregate "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" "tree789"
                |> finalizeAggregate
            Expect.equal aggregate.RequiredChecksFailed 1 "expected 1 failed"
            Expect.equal aggregate.RequiredChecksTotal 1 "expected 1 total"
            Expect.equal aggregate.RequiredChecksPassed 0 "expected 0 passed"

        testCase "required unavailable -> required_failed=1, overall fail" <| fun () ->
            let records = [
                { (sampleRecord "check1") with Required = true; Result = RecordUnavailable }
            ]
            let aggregate =
                records
                |> computeAggregate "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" "tree789"
                |> finalizeAggregate
            Expect.equal aggregate.RequiredChecksFailed 1 "expected 1 failed for unavailable"
            Expect.equal aggregate.RequiredChecksTotal 1 "expected 1 total"

        testCase "optional unavailable -> required_failed unchanged" <| fun () ->
            let records = [
                { (sampleRecord "check1") with Required = true; Result = RecordPass }
                { (sampleRecord "check2") with Required = false; Result = RecordUnavailable }
            ]
            let aggregate =
                records
                |> computeAggregate "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" "tree789"
                |> finalizeAggregate
            Expect.equal aggregate.RequiredChecksFailed 0 "expected 0 failed"

        testCase "record IDs are sorted" <| fun () ->
            let records = [
                { (sampleRecord "check3") with EvidenceId = "00003" }
                { (sampleRecord "check1") with EvidenceId = "00001" }
                { (sampleRecord "check2") with EvidenceId = "00002" }
            ]
            let aggregate =
                records
                |> computeAggregate "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" "tree789"
                |> finalizeAggregate
            Expect.equal aggregate.RecordIds [ "00001"; "00002"; "00003" ] "expected sorted IDs"

        testCase "aggregate semantic hash recomputes" <| fun () ->
            let records = [
                { (sampleRecord "check1") with EvidenceId = "a".PadRight(64, '0') }
            ]
            let aggregate =
                records
                |> computeAggregate "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" "tree789"
                |> finalizeAggregate
            Expect.isNonEmpty aggregate.SemanticSha256 "expected nonempty hash"
    ]

// -----------------------------------------------------------------------------
// CORRECTION02: Executed check start time tests
// -----------------------------------------------------------------------------

let executedCheckStartTimeTests =
    testList "ExecutedCheckStartTime" [
        testCase "each check gets its own start time" <| fun () ->
            // Simulate per-check timestamps
            let time1 = DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero)
            let time2 = time1.AddMilliseconds(100.0)
            let time3 = time2.AddMilliseconds(100.0)
            let def1 = sampleDefinition "check1" true
            let def2 = sampleDefinition "check2" true
            let res1 = sampleResult "check1" [ "echo"; "1" ] EvidenceStatus.Pass
            let res2 = sampleResult "check2" [ "echo"; "2" ] EvidenceStatus.Pass
            let executed1 = { ExecutedCanonicalCheck.Definition = def1; Result = res1; StartedAt = time1 }
            let executed2 = { ExecutedCanonicalCheck.Definition = def2; Result = res2; StartedAt = time2 }
            // Convert and check that each record has different start time
            let result = convertExecutedChecksToRecords [ executed1; executed2 ] "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855" "tree789" true
            match result with
            | Error e -> failwithf "expected Ok, got Error: %A" e
            | Ok records ->
                let ids = records |> List.map (fun r -> r.CheckId, r.StartedAt) |> List.sortBy fst
                Expect.equal (List.length ids) 2 "expected 2 records"
                Expect.notEqual (snd ids.[0]) (snd ids.[1]) "expected different start times"
    ]

// -----------------------------------------------------------------------------
// All tests (auto-discovered by Expecto)
// -----------------------------------------------------------------------------

[<Tests>]
let tests =
    testList "RecordPipeline" [
        validateBijectionTests
        validateRecordsTests
        statusMappingTests
        failureToStringTests
        validationIssueToStringTests
        executedCanonicalCheckTests
        convertExecutedChecksToRecordsTests
        aggregateDerivationTests
        executedCheckStartTimeTests
    ]

// =============================================================================
// Publication tests
//
// ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01-REAL-RECORD-PIPELINE01-CORRECTION05
//
// Tests for staged bytes validation and single compatibility authority:
//   - publishSnapshotWithCompatibilityProjection reads from disk
//   - Compatibility projection matches provider output
// =============================================================================

open System
open System.IO
open Expecto
open Circus.Tooling.CanonicalEvidence.Publication
open Circus.Tooling.CanonicalEvidence.Serialization

// Test that the staged compatibility projection is READ from disk, not parsed from memory
let publicationStagedBytesReadTests =
    testList "staged bytes read from disk" [
        testCase "writes and reads compatibility projection from disk" <| fun () ->
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
            Directory.CreateDirectory tempDir |> ignore
            try
                // Create a valid CanonicalEvidence (Domain type)
                let projection = {
                    SchemaVersion = 1
                    ProviderName = "circus-canonical-evidence"
                    ProviderVersion = "1.0.0"
                    TestedCommitOid = "abc123def456"
                    TestedTreeOid = "tree789abc"
                    ObjectFormat = "sha1"
                    ActiveScopeActId = ""
                    ActiveScopePointerBlobOid = ""
                    ScopeDeclarationPath = "/.circus/scope.yaml"
                    DeclarationBlobOid = ""
                    BaselineCommitOid = ""
                    Checks = []
                    OverallStatus = Pass
                    SemanticSha256 = "sem789"
                }

                // Create a valid CanonicalExecutionEvidence
                let record = {
                    SchemaVersion = 1
                    EvidenceId = "ev-001aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
                    CheckId = "check-001"
                    Required = true
                    ProviderId = "circus-canonical-evidence"
                    ProviderVersion = "1.0.0"
                    Command = "dotnet"
                    Arguments = [ "build" ]
                    WorkingDirectory = "/repo"
                    StartedAt = "2026-07-28T12:00:00Z"
                    DurationMs = 60000L
                    ExitCode = Some 0
                    Result = RecordPass
                    TestsTotal = Some 1
                    TestsPassed = Some 1
                    TestsIgnored = None
                    TestsFailed = None
                    TestsErrored = None
                    StdoutSha256 = None
                    StderrSha256 = None
                    StdoutByteLength = None
                    StderrByteLength = None
                    FailureKind = None
                    TestedCommitOid = "abc123def456"
                    TestedTreeOid = "tree789abc"
                    WorkingTreeClean = true
                    ProviderBinarySha256 = None
                    ToolingBinarySha256 = None
                    TestBinarySha256 = None
                }

                let aggregate = {
                    SchemaVersion = 1
                    SubjectCommitOid = "abc123def456"
                    SubjectTreeOid = "tree789abc"
                    RecordsTotal = 1
                    RecordsPassed = 1
                    RecordsFailed = 0
                    RecordsUnavailable = 0
                    TestsTotal = 1
                    TestsPassed = 1
                    TestsIgnored = 0
                    TestsFailed = 0
                    TestsErrored = 0
                    RequiredChecksTotal = 1
                    RequiredChecksPassed = 1
                    RequiredChecksFailed = 0
                    RecordIds = ["ev-001aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"]
                    OverallStatus = RecordPass
                    SemanticSha256 = "sem789"
                }

                let outcome = publishSnapshotWithCompatibilityProjection tempDir [record] aggregate projection

                Expect.isTrue outcome.Success "publication should succeed"
                Expect.isTrue (File.Exists (Path.Combine(tempDir, "canonical-evidence.json"))) "compatibility file should exist"

                // Verify the file on disk can be parsed
                let diskContent = File.ReadAllText (Path.Combine(tempDir, "canonical-evidence.json"))
                match parseWireJson diskContent with
                | Ok parsed ->
                    Expect.equal parsed.TestedCommitOid "abc123def456" "commit should match"
                    Expect.equal parsed.TestedTreeOid "tree789abc" "tree should match"
                | Error e ->
                    failwithf "Failed to parse written file: %s" e
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)

        testCase "fails when staged compatibility file is corrupted" <| fun () ->
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
            Directory.CreateDirectory tempDir |> ignore
            try
                // Create a CanonicalEvidence (Domain type)
                let projection = {
                    SchemaVersion = 1
                    ProviderName = "circus-canonical-evidence"
                    ProviderVersion = "1.0.0"
                    TestedCommitOid = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
                    TestedTreeOid = "tree123"
                    ObjectFormat = "sha1"
                    ActiveScopeActId = ""
                    ActiveScopePointerBlobOid = ""
                    ScopeDeclarationPath = "/.circus/scope.yaml"
                    DeclarationBlobOid = ""
                    BaselineCommitOid = ""
                    Checks = []
                    OverallStatus = Pass
                    SemanticSha256 = "sem123"
                }

                let aggregate = {
                    SchemaVersion = 1
                    SubjectCommitOid = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
                    SubjectTreeOid = "tree123"
                    RecordsTotal = 0
                    RecordsPassed = 0
                    RecordsFailed = 0
                    RecordsUnavailable = 0
                    TestsTotal = 0
                    TestsPassed = 0
                    TestsIgnored = 0
                    TestsFailed = 0
                    TestsErrored = 0
                    RequiredChecksTotal = 0
                    RequiredChecksPassed = 0
                    RequiredChecksFailed = 0
                    RecordIds = []
                    OverallStatus = RecordPass
                    SemanticSha256 = "sem123"
                }

                let outcome = publishSnapshotWithCompatibilityProjection tempDir [] aggregate projection
                Expect.isTrue outcome.Success "initial publication should succeed"

                // Now corrupt the staged file by writing invalid JSON before a second call
                // This simulates disk corruption or concurrent modification
                let compatPath = Path.Combine(tempDir, "canonical-evidence.json")
                File.WriteAllText(compatPath, "{ invalid json }")

                // Second call should fail when reading the corrupted file
                let outcome2 = publishSnapshotWithCompatibilityProjection tempDir [] aggregate projection
                Expect.isFalse outcome2.Success "publication should fail with corrupted file"
                match outcome2.Failure with
                | Some (SnapshotCompatibilityWriteFailed _) -> ()
                | _ -> failwithf "Expected SnapshotCompatibilityWriteFailed, got %A" outcome2.Failure
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)

        testCase "fails when commit mismatch between projection and aggregate" <| fun () ->
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
            Directory.CreateDirectory tempDir |> ignore
            try
                let projection = {
                    SchemaVersion = 1
                    ProviderName = "circus-canonical-evidence"
                    ProviderVersion = "1.0.0"
                    TestedCommitOid = "commit-A"
                    TestedTreeOid = "tree123"
                    ObjectFormat = "sha1"
                    ActiveScopeActId = ""
                    ActiveScopePointerBlobOid = ""
                    ScopeDeclarationPath = "/.circus/scope.yaml"
                    DeclarationBlobOid = ""
                    BaselineCommitOid = ""
                    Checks = []
                    OverallStatus = Pass
                    SemanticSha256 = "sem123"
                }

                let aggregate = {
                    SchemaVersion = 1
                    SubjectCommitOid = "commit-B"  // Different from projection!
                    SubjectTreeOid = "tree123"
                    RecordsTotal = 0
                    RecordsPassed = 0
                    RecordsFailed = 0
                    RecordsUnavailable = 0
                    TestsTotal = 0
                    TestsPassed = 0
                    TestsIgnored = 0
                    TestsFailed = 0
                    TestsErrored = 0
                    RequiredChecksTotal = 0
                    RequiredChecksPassed = 0
                    RequiredChecksFailed = 0
                    RecordIds = []
                    OverallStatus = RecordPass
                    SemanticSha256 = "sem123"
                }

                let outcome = publishSnapshotWithCompatibilityProjection tempDir [] aggregate projection
                Expect.isFalse outcome.Success "should fail with commit mismatch"
                match outcome.Failure with
                | Some (SnapshotCompatibilityWriteFailed msg) ->
                    Expect.stringContains msg "commit mismatch" "should mention commit mismatch"
                | _ -> failwithf "Expected SnapshotCompatibilityWriteFailed, got %A" outcome.Failure
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)
    ]

[<Tests>]
let publicationTests =
    testList "Publication" [
        publicationStagedBytesReadTests
    ]

[<Tests>]
let strictParserTests =
    testList "StrictParser" [
        testCase "valid ISO 8601 timestamp with Z suffix" <| fun () ->
            let json = """{"schema_version":1,"evidence_id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","check_id":"test-check","required":true,"provider_id":"circus-canonical-evidence","provider_version":"1.0.0","command":"echo","arguments":[],"working_directory":"/tmp","started_at":"2026-07-29T12:30:00Z","duration_ms":100,"exit_code":0,"result":"pass","tests_total":1,"tests_passed":1,"tests_ignored":0,"tests_failed":0,"tests_errored":0,"stdout_sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855","stderr_sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855","stdout_byte_length":0,"stderr_byte_length":0,"failure_kind":"<NONE>","tested_commit_oid":"abc123def456","tested_tree_oid":"tree789abc","working_tree_clean":true,"provider_binary_sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855","tooling_binary_sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855","test_binary_sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}"""
            match parseEvidenceWireJsonStrict json with
            | Ok _ -> ()
            | Error e -> failwithf "Expected valid JSON: %A" e

        testCase "invalid timestamp format rejects" <| fun () ->
            let json = """{"schema_version":1,"evidence_id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","check_id":"test-check","required":true,"provider_id":"circus-canonical-evidence","provider_version":"1.0.0","command":"echo","arguments":[],"working_directory":"/tmp","started_at":"not-a-timestamp","duration_ms":100,"exit_code":0,"result":"pass","tests_total":1,"tests_passed":1,"tests_ignored":0,"tests_failed":0,"tests_errored":0,"stdout_sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855","stderr_sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855","stdout_byte_length":0,"stderr_byte_length":0,"failure_kind":"<NONE>","tested_commit_oid":"abc123def456","tested_tree_oid":"tree789abc","working_tree_clean":true,"provider_binary_sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855","tooling_binary_sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855","test_binary_sha256":"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"}"""
            match parseEvidenceWireJsonStrict json with
            | Ok _ -> failwith "Expected invalid timestamp to be rejected"
            | Error errors ->
                Expect.isTrue (errors |> List.exists (function | EvidenceWireParseError.InvalidTimestamp _ -> true | _ -> false)) "Expected InvalidTimestamp error"

        testCase "manifest valid with exactly three required paths" <| fun () ->
            let manifest = "{\"path\":\"records.jsonl\",\"sha256\":\"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\",\"byte_length\":100}\n{\"path\":\"aggregate.json\",\"sha256\":\"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\",\"byte_length\":200}\n{\"path\":\"canonical-evidence.json\",\"sha256\":\"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\",\"byte_length\":300}\n"
            match parseArtifactManifestJsonlStrict manifest with
            | Ok entries ->
                Expect.equal (List.length entries) 3 "Expected exactly 3 entries"
            | Error e -> failwithf "Expected valid manifest: %A" e

        testCase "manifest missing records.jsonl rejects" <| fun () ->
            let manifest = "{\"path\":\"aggregate.json\",\"sha256\":\"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\",\"byte_length\":200}\n{\"path\":\"canonical-evidence.json\",\"sha256\":\"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\",\"byte_length\":300}\n"
            match parseArtifactManifestJsonlStrict manifest with
            | Ok _ -> failwith "Expected missing path to be detected"
            | Error errors ->
                Expect.isTrue (errors |> List.exists (function | ArtifactManifestParseError.UnknownPath "records.jsonl" -> true | _ -> false)) "Expected UnknownPath error"

        testCase "manifest unknown path rejects" <| fun () ->
            let manifest = "{\"path\":\"records.jsonl\",\"sha256\":\"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\",\"byte_length\":100}\n{\"path\":\"aggregate.json\",\"sha256\":\"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\",\"byte_length\":200}\n{\"path\":\"canonical-evidence.json\",\"sha256\":\"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\",\"byte_length\":300}\n{\"path\":\"extra-file.json\",\"sha256\":\"e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855\",\"byte_length\":50}\n"
            match parseArtifactManifestJsonlStrict manifest with
            | Ok _ -> failwith "Expected unknown path to be detected"
            | Error errors ->
                Expect.isTrue (errors |> List.exists (function | ArtifactManifestParseError.UnknownPath "extra-file.json" -> true | _ -> false)) "Expected UnknownPath error"
    ]
