module Circus.Tooling.Tests.CanonicalEvidence.CompatibilityStructuralEqualityTests

// =============================================================================
// Canonical evidence – compatibility structural equality tests
//
// ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER01-REAL-RECORD-PIPELINE01-CORRECTION07-CORRECTION04
//
// Tests for exact compatibility structural equality:
//   - Pure typed comparison authority covers every top-level and per-check field
//   - Check matching is by exact ID bijection, not list position
//   - Every required compatibility mutation is rejected
//   - Valid staged compatibility document compares exactly equal to provider projection
// =============================================================================

open System
open System.IO
open Expecto

open Circus.Tooling.CanonicalEvidence.Domain
open Circus.Tooling.CanonicalEvidence.Serialization
open Circus.Tooling.CanonicalEvidence.Validation
open Circus.Tooling.CanonicalEvidence.Publication
open Circus.Tooling.Tests.CanonicalEvidence.PublicationFixture

// -----------------------------------------------------------------------------
// CompatibilityDifference type (matching the specification)
// -----------------------------------------------------------------------------

[<RequireQualifiedAccess>]
type CompatibilityCheckDifference =
    | Id of expected: string * actual: string
    | CommandArgv of expected: string list * actual: string list
    | WorkingDirectory of expected: string * actual: string
    | DurationMilliseconds of expected: int64 * actual: int64
    | ExitCode of expected: int option * actual: int option
    | Status of expected: EvidenceStatus * actual: EvidenceStatus
    | StdoutSha256 of expected: string option * actual: string option
    | StderrSha256 of expected: string option * actual: string option
    | FailureKind of expected: string option * actual: string option

[<RequireQualifiedAccess>]
type CompatibilityDifference =
    | SchemaVersion of expected: int * actual: int
    | ProviderName of expected: string * actual: string
    | ProviderVersion of expected: string * actual: string
    | TestedCommitOid of expected: string * actual: string
    | TestedTreeOid of expected: string * actual: string
    | ObjectFormat of expected: string * actual: string
    | ActiveScopeActId of expected: string * actual: string
    | ActiveScopePointerBlobOid of expected: string * actual: string
    | ScopeDeclarationPath of expected: string * actual: string
    | DeclarationBlobOid of expected: string * actual: string
    | BaselineCommitOid of expected: string * actual: string
    | OverallStatus of expected: EvidenceStatus * actual: EvidenceStatus
    | SemanticSha256 of expected: string * actual: string
    | CheckCount of expected: int * actual: int
    | MissingCheck of checkId: string
    | UnknownCheck of checkId: string
    | CheckDifference of checkId: string * difference: CompatibilityCheckDifference

// -----------------------------------------------------------------------------
// Pure compatibility comparison authority
// -----------------------------------------------------------------------------

let compareCompatibilityCheck (expected: EvidenceCheckResult) (actual: EvidenceCheckResult) : CompatibilityCheckDifference list =
    let diffs = ResizeArray()
    if expected.Id <> actual.Id then diffs.Add(CompatibilityCheckDifference.Id(expected.Id, actual.Id))
    if expected.CommandArgv <> actual.CommandArgv then diffs.Add(CompatibilityCheckDifference.CommandArgv(expected.CommandArgv, actual.CommandArgv))
    if expected.WorkingDirectory <> actual.WorkingDirectory then diffs.Add(CompatibilityCheckDifference.WorkingDirectory(expected.WorkingDirectory, actual.WorkingDirectory))
    if expected.DurationMilliseconds <> actual.DurationMilliseconds then diffs.Add(CompatibilityCheckDifference.DurationMilliseconds(expected.DurationMilliseconds, actual.DurationMilliseconds))
    if expected.ExitCode <> actual.ExitCode then diffs.Add(CompatibilityCheckDifference.ExitCode(expected.ExitCode, actual.ExitCode))
    if expected.Status <> actual.Status then diffs.Add(CompatibilityCheckDifference.Status(expected.Status, actual.Status))
    if expected.StdoutSha256 <> actual.StdoutSha256 then diffs.Add(CompatibilityCheckDifference.StdoutSha256(expected.StdoutSha256, actual.StdoutSha256))
    if expected.StderrSha256 <> actual.StderrSha256 then diffs.Add(CompatibilityCheckDifference.StderrSha256(expected.StderrSha256, actual.StderrSha256))
    if expected.FailureKind <> actual.FailureKind then diffs.Add(CompatibilityCheckDifference.FailureKind(expected.FailureKind, actual.FailureKind))
    List.ofSeq diffs

let compareCompatibilityProjection
    (expected: CanonicalEvidence)
    (actual: CanonicalEvidence)
    : CompatibilityDifference list =
    let diffs = ResizeArray()
    
    // Top-level field comparisons
    if expected.SchemaVersion <> actual.SchemaVersion then
        diffs.Add(CompatibilityDifference.SchemaVersion(expected.SchemaVersion, actual.SchemaVersion))
    if expected.ProviderName <> actual.ProviderName then
        diffs.Add(CompatibilityDifference.ProviderName(expected.ProviderName, actual.ProviderName))
    if expected.ProviderVersion <> actual.ProviderVersion then
        diffs.Add(CompatibilityDifference.ProviderVersion(expected.ProviderVersion, actual.ProviderVersion))
    if expected.TestedCommitOid <> actual.TestedCommitOid then
        diffs.Add(CompatibilityDifference.TestedCommitOid(expected.TestedCommitOid, actual.TestedCommitOid))
    if expected.TestedTreeOid <> actual.TestedTreeOid then
        diffs.Add(CompatibilityDifference.TestedTreeOid(expected.TestedTreeOid, actual.TestedTreeOid))
    if expected.ObjectFormat <> actual.ObjectFormat then
        diffs.Add(CompatibilityDifference.ObjectFormat(expected.ObjectFormat, actual.ObjectFormat))
    if expected.ActiveScopeActId <> actual.ActiveScopeActId then
        diffs.Add(CompatibilityDifference.ActiveScopeActId(expected.ActiveScopeActId, actual.ActiveScopeActId))
    if expected.ActiveScopePointerBlobOid <> actual.ActiveScopePointerBlobOid then
        diffs.Add(CompatibilityDifference.ActiveScopePointerBlobOid(expected.ActiveScopePointerBlobOid, actual.ActiveScopePointerBlobOid))
    if expected.ScopeDeclarationPath <> actual.ScopeDeclarationPath then
        diffs.Add(CompatibilityDifference.ScopeDeclarationPath(expected.ScopeDeclarationPath, actual.ScopeDeclarationPath))
    if expected.DeclarationBlobOid <> actual.DeclarationBlobOid then
        diffs.Add(CompatibilityDifference.DeclarationBlobOid(expected.DeclarationBlobOid, actual.DeclarationBlobOid))
    if expected.BaselineCommitOid <> actual.BaselineCommitOid then
        diffs.Add(CompatibilityDifference.BaselineCommitOid(expected.BaselineCommitOid, actual.BaselineCommitOid))
    if expected.OverallStatus <> actual.OverallStatus then
        diffs.Add(CompatibilityDifference.OverallStatus(expected.OverallStatus, actual.OverallStatus))
    if expected.SemanticSha256 <> actual.SemanticSha256 then
        diffs.Add(CompatibilityDifference.SemanticSha256(expected.SemanticSha256, actual.SemanticSha256))
    
    // Check count comparison
    if expected.Checks.Length <> actual.Checks.Length then
        diffs.Add(CompatibilityDifference.CheckCount(expected.Checks.Length, actual.Checks.Length))
    else
        // Build ID sets for bijection check
        let expectedIds = expected.Checks |> List.map (fun c -> c.Id) |> Set.ofList
        let actualIds = actual.Checks |> List.map (fun c -> c.Id) |> Set.ofList
        
        // Find missing checks (in expected but not in actual)
        let missingChecks = expectedIds - actualIds
        for missingId in missingChecks do
            diffs.Add(CompatibilityDifference.MissingCheck(missingId))
        
        // Find unknown checks (in actual but not in expected)
        let unknownChecks = actualIds - expectedIds
        for unknownId in unknownChecks do
            diffs.Add(CompatibilityDifference.UnknownCheck(unknownId))
        
        // Compare matched checks by ID (bijection)
        let expectedById = expected.Checks |> List.map (fun c -> c.Id, c) |> Map.ofList
        let actualById = actual.Checks |> List.map (fun c -> c.Id, c) |> Map.ofList
        
        for expectedId in expectedIds do
            match Map.tryFind expectedId actualById with
            | None -> () // Already reported as missing/unknown
            | Some actualCheck ->
                match Map.tryFind expectedId expectedById with
                | None -> ()
                | Some expectedCheck ->
                    let checkDiffs = compareCompatibilityCheck expectedCheck actualCheck
                    for diff in checkDiffs do
                        diffs.Add(CompatibilityDifference.CheckDifference(expectedId, diff))
    
    List.ofSeq diffs

// -----------------------------------------------------------------------------
// Helper functions for checking difference types
// -----------------------------------------------------------------------------

let private hasSchemaVersionDiff (diffs: CompatibilityDifference list) : bool =
    diffs |> List.exists (function | CompatibilityDifference.SchemaVersion _ -> true | _ -> false)

let private hasProviderNameDiff (diffs: CompatibilityDifference list) : bool =
    diffs |> List.exists (function | CompatibilityDifference.ProviderName _ -> true | _ -> false)

let private hasMissingCheckDiff (diffs: CompatibilityDifference list) : bool =
    diffs |> List.exists (function | CompatibilityDifference.MissingCheck _ -> true | _ -> false)

let private hasUnknownCheckDiff (diffs: CompatibilityDifference list) (checkId: string) : bool =
    diffs |> List.exists (function | CompatibilityDifference.UnknownCheck id -> id = checkId | _ -> false)

let private hasCheckCountDiff (diffs: CompatibilityDifference list) : bool =
    diffs |> List.exists (function | CompatibilityDifference.CheckCount _ -> true | _ -> false)

let private hasCheckDifference (diffs: CompatibilityDifference list) (checkId: string) : bool =
    diffs |> List.exists (function | CompatibilityDifference.CheckDifference(id, _) -> id = checkId | _ -> false)

let private hasCheckIdDifference (diffs: CompatibilityDifference list) (checkId: string) : bool =
    diffs |> List.exists (function 
        | CompatibilityDifference.CheckDifference(id, CompatibilityCheckDifference.Id _) -> id = checkId 
        | _ -> false)

// -----------------------------------------------------------------------------
// Test group: ExactStructuralEquality
// -----------------------------------------------------------------------------

let exactStructuralEqualityTests =
    testList "ExactStructuralEquality" [
        testCase "identical documents produce empty difference list" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let diffs = compareCompatibilityProjection fixture.CompatibilityProjection fixture.CompatibilityProjection
            Expect.isEmpty diffs "identical documents should have no differences"

        testCase "published compatibility equals provider projection exactly" <| fun () ->
            let tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("n"))
            Directory.CreateDirectory tempDir |> ignore
            try
                let fixture = createValidPublicationFixture ()
                let outcome = stageAndPublishSnapshot tempDir fixture.Records fixture.Aggregate fixture.CompatibilityProjection None
                Expect.isTrue outcome.Success "publication should succeed"

                // Parse the published compatibility
                let compatPath = Path.Combine(tempDir, "canonical-evidence.json")
                let diskContent = File.ReadAllText compatPath
                match parseWireJson diskContent with
                | Error e -> failwithf "Failed to parse compatibility: %s" e
                | Ok parsedCompat ->
                    let diffs = compareCompatibilityProjection fixture.CompatibilityProjection parsedCompat
                    Expect.isEmpty diffs "published compatibility must equal provider projection exactly"
            finally
                if Directory.Exists tempDir then Directory.Delete(tempDir, true)

        testCase "semantic hash equality does not mask structural difference" <| fun () ->
            let fixture = createValidPublicationFixture ()
            // Create a mutated document with a valid (but different) semantic hash
            let mutated = { fixture.CompatibilityProjection with ProviderName = "different-name" }
            let withValidHash = withSemanticHash mutated
            // The semantic hash is now valid, but the structural comparison must still detect the difference
            let diffs = compareCompatibilityProjection fixture.CompatibilityProjection withValidHash
            Expect.isNonEmpty diffs "structural difference must be detected even with valid hash"
            Expect.isTrue (hasProviderNameDiff diffs) "ProviderName difference should be reported"
    ]

// -----------------------------------------------------------------------------
// Test group: TopLevelFieldMutations
// -----------------------------------------------------------------------------

let topLevelFieldMutationsTests =
    testList "TopLevelFieldMutations" [
        testCase "schema_version mutation is detected" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let mutated = { fixture.CompatibilityProjection with SchemaVersion = 999 }
            let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
            Expect.isNonEmpty diffs "schema_version mutation should be detected"
            Expect.isTrue (hasSchemaVersionDiff diffs) "SchemaVersion difference should be reported"

        testCase "provider_name mutation is detected" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let mutated = { fixture.CompatibilityProjection with ProviderName = "wrong-provider" }
            let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
            Expect.isNonEmpty diffs "provider_name mutation should be detected"
            Expect.isTrue (hasProviderNameDiff diffs) "ProviderName difference should be reported"

        testCase "provider_version mutation is detected" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let mutated = { fixture.CompatibilityProjection with ProviderVersion = "99.0.0" }
            let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
            Expect.isNonEmpty diffs "provider_version mutation should be detected"

        testCase "tested_commit_oid mutation is detected" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let mutated = { fixture.CompatibilityProjection with TestedCommitOid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }
            let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
            Expect.isNonEmpty diffs "tested_commit_oid mutation should be detected"

        testCase "tested_tree_oid mutation is detected" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let mutated = { fixture.CompatibilityProjection with TestedTreeOid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }
            let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
            Expect.isNonEmpty diffs "tested_tree_oid mutation should be detected"

        testCase "object_format mutation is detected" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let mutated = { fixture.CompatibilityProjection with ObjectFormat = "sha256" }
            let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
            Expect.isNonEmpty diffs "object_format mutation should be detected"

        testCase "active_scope_act_id mutation is detected" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let mutated = { fixture.CompatibilityProjection with ActiveScopeActId = "different-act-id" }
            let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
            Expect.isNonEmpty diffs "active_scope_act_id mutation should be detected"

        testCase "active_scope_pointer_blob_oid mutation is detected" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let mutated = { fixture.CompatibilityProjection with ActiveScopePointerBlobOid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }
            let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
            Expect.isNonEmpty diffs "active_scope_pointer_blob_oid mutation should be detected"

        testCase "scope_declaration_path mutation is detected" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let mutated = { fixture.CompatibilityProjection with ScopeDeclarationPath = "/different/path" }
            let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
            Expect.isNonEmpty diffs "scope_declaration_path mutation should be detected"

        testCase "declaration_blob_oid mutation is detected" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let mutated = { fixture.CompatibilityProjection with DeclarationBlobOid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }
            let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
            Expect.isNonEmpty diffs "declaration_blob_oid mutation should be detected"

        testCase "baseline_commit_oid mutation is detected" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let mutated = { fixture.CompatibilityProjection with BaselineCommitOid = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }
            let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
            Expect.isNonEmpty diffs "baseline_commit_oid mutation should be detected"

        testCase "overall_status mutation is detected" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let newStatus = if fixture.CompatibilityProjection.OverallStatus = Pass then Fail else Pass
            let mutated = { fixture.CompatibilityProjection with OverallStatus = newStatus }
            let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
            Expect.isNonEmpty diffs "overall_status mutation should be detected"

        testCase "semantic_sha256 mutation is detected" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let mutated = { fixture.CompatibilityProjection with SemanticSha256 = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" }
            let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
            Expect.isNonEmpty diffs "semantic_sha256 mutation should be detected"
    ]

// -----------------------------------------------------------------------------
// Test group: CheckCountMutations
// -----------------------------------------------------------------------------

let checkCountMutationsTests =
    testList "CheckCountMutations" [
        testCase "removing one check is detected as CheckCount" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let mutated = { fixture.CompatibilityProjection with Checks = List.tail fixture.CompatibilityProjection.Checks }
            let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
            Expect.isNonEmpty diffs "removing check should be detected"
            // When count differs, CheckCount is reported (MissingCheck is not redundant in this case)
            Expect.isTrue (hasCheckCountDiff diffs) 
                "CheckCount difference should be reported when check count changes"

        testCase "adding one check is detected as CheckCount" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let extraCheck = {
                Id = "extra-check"
                CommandArgv = [ "echo"; "extra" ]
                WorkingDirectory = "/tmp"
                DurationMilliseconds = 100L
                ExitCode = Some 0
                Status = Pass
                StdoutSha256 = Some "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
                StderrSha256 = Some "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
                FailureKind = None
            }
            let mutated = { fixture.CompatibilityProjection with Checks = extraCheck :: fixture.CompatibilityProjection.Checks }
            let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
            Expect.isNonEmpty diffs "adding check should be detected"
            // When count differs, CheckCount is reported
            Expect.isTrue (hasCheckCountDiff diffs) 
                "CheckCount difference should be reported when check count changes"

        testCase "check count mismatch is detected" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let mutated = { fixture.CompatibilityProjection with Checks = [] }
            let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
            Expect.isNonEmpty diffs "check count mismatch should be detected"
            Expect.isTrue (hasCheckCountDiff diffs) "CheckCount difference should be reported"
    ]

// -----------------------------------------------------------------------------
// Test group: PerCheckFieldMutations
// -----------------------------------------------------------------------------

let perCheckFieldMutationsTests =
    testList "PerCheckFieldMutations" [
        testCase "check ID change is detected" <| fun () ->
            let fixture = createValidPublicationFixture ()
            // Get the EvidenceId of the first check before mutation
            let firstCheckId = fixture.CompatibilityProjection.Checks.Head.Id
            let mutatedChecks = 
                fixture.CompatibilityProjection.Checks
                |> List.mapi (fun i c -> if i = 0 then { c with Id = "changed-id" } else c)
            let mutated = { fixture.CompatibilityProjection with Checks = mutatedChecks }
            let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
            Expect.isNonEmpty diffs "check ID change should be detected"
            // The first check's ID (the EvidenceId) should appear as MissingCheck since we changed it
            Expect.isTrue (hasMissingCheckDiff diffs) 
                "MissingCheck for original EvidenceId should be reported when ID is changed"

        testCase "command_argv change is detected" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let mutatedChecks = 
                fixture.CompatibilityProjection.Checks
                |> List.mapi (fun i c -> if i = 0 then { c with CommandArgv = [ "wrong"; "command" ] } else c)
            let mutated = { fixture.CompatibilityProjection with Checks = mutatedChecks }
            let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
            Expect.isNonEmpty diffs "command_argv change should be detected"

        testCase "working_directory change is detected" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let mutatedChecks = 
                fixture.CompatibilityProjection.Checks
                |> List.mapi (fun i c -> if i = 0 then { c with WorkingDirectory = "/different" } else c)
            let mutated = { fixture.CompatibilityProjection with Checks = mutatedChecks }
            let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
            Expect.isNonEmpty diffs "working_directory change should be detected"

        testCase "duration change is detected" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let mutatedChecks = 
                fixture.CompatibilityProjection.Checks
                |> List.mapi (fun i c -> if i = 0 then { c with DurationMilliseconds = 9999L } else c)
            let mutated = { fixture.CompatibilityProjection with Checks = mutatedChecks }
            let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
            Expect.isNonEmpty diffs "duration change should be detected"

        testCase "exit_code change is detected" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let mutatedChecks = 
                fixture.CompatibilityProjection.Checks
                |> List.mapi (fun i c -> if i = 0 then { c with ExitCode = Some 1 } else c)
            let mutated = { fixture.CompatibilityProjection with Checks = mutatedChecks }
            let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
            Expect.isNonEmpty diffs "exit_code change should be detected"

        testCase "status change is detected" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let mutatedChecks = 
                fixture.CompatibilityProjection.Checks
                |> List.mapi (fun i c -> 
                    if i = 0 then 
                        { c with Status = if c.Status = Pass then Fail else Pass } 
                    else c)
            let mutated = { fixture.CompatibilityProjection with Checks = mutatedChecks }
            let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
            Expect.isNonEmpty diffs "status change should be detected"

        testCase "stdout_sha256 change is detected" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let mutatedChecks = 
                fixture.CompatibilityProjection.Checks
                |> List.mapi (fun i c -> 
                    if i = 0 then 
                        { c with StdoutSha256 = Some "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" } 
                    else c)
            let mutated = { fixture.CompatibilityProjection with Checks = mutatedChecks }
            let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
            Expect.isNonEmpty diffs "stdout_sha256 change should be detected"

        testCase "stderr_sha256 change is detected" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let mutatedChecks = 
                fixture.CompatibilityProjection.Checks
                |> List.mapi (fun i c -> 
                    if i = 0 then 
                        { c with StderrSha256 = Some "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb" } 
                    else c)
            let mutated = { fixture.CompatibilityProjection with Checks = mutatedChecks }
            let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
            Expect.isNonEmpty diffs "stderr_sha256 change should be detected"

        testCase "failure_kind change is detected" <| fun () ->
            let fixture = createValidPublicationFixture ()
            let mutatedChecks = 
                fixture.CompatibilityProjection.Checks
                |> List.mapi (fun i c -> 
                    if i = 0 then 
                        { c with FailureKind = Some "different_failure" } 
                    else c)
            let mutated = { fixture.CompatibilityProjection with Checks = mutatedChecks }
            let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
            Expect.isNonEmpty diffs "failure_kind change should be detected"
    ]

// -----------------------------------------------------------------------------
// Test group: BijectionValidation
// -----------------------------------------------------------------------------

let bijectionValidationTests =
    testList "BijectionValidation" [
        testCase "checks matched by exact ID, not position" <| fun () ->
            let fixture = createValidPublicationFixture ()
            // Swap the order of checks - should still match since they're matched by ID
            let swappedChecks = List.rev fixture.CompatibilityProjection.Checks
            let mutated = { fixture.CompatibilityProjection with Checks = swappedChecks }
            let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
            Expect.isEmpty diffs "checks matched by ID should match regardless of position"

        testCase "position-only matching would miss ID swap" <| fun () ->
            let fixture = createValidPublicationFixture ()
            // If matching were by position only, this would match - but with ID matching, it should detect
            if List.length fixture.CompatibilityProjection.Checks >= 2 then
                let check1 = fixture.CompatibilityProjection.Checks.[0]
                let check2 = fixture.CompatibilityProjection.Checks.[1]
                // Swap the IDs but keep them in the same position
                let swappedChecks = 
                    [{ check2 with Id = check1.Id }; { check1 with Id = check2.Id }]
                let mutated = { fixture.CompatibilityProjection with Checks = swappedChecks }
                let diffs = compareCompatibilityProjection fixture.CompatibilityProjection mutated
                // Should detect that IDs are swapped (both have wrong CommandArgv for their ID)
                Expect.isNonEmpty diffs "ID swap should be detected"
    ]

// -----------------------------------------------------------------------------
// All compatibility structural equality tests
// -----------------------------------------------------------------------------

[<Tests>]
let compatibilityStructuralEqualityTests =
    testList "CompatibilityStructuralEquality" [
        exactStructuralEqualityTests
        topLevelFieldMutationsTests
        checkCountMutationsTests
        perCheckFieldMutationsTests
        bijectionValidationTests
    ]
