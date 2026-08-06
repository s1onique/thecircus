module Circus.Tooling.Tests.FSharpDiagnostics.RepairEpisodes.VerificationEvidenceStringAliasTests

// =============================================================================
// Verification Evidence String Alias Tests
//
// Tests for string-typed alias pairs:
//   - kind / verification_kind
//   - status / verification_result
//   - command / verification_command
//
// Restored for ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-EXTRACTION01-CORRECTION01.
// =============================================================================

open Expecto

open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Engine
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain
open VerificationEvidenceAliasFixture

// -----------------------------------------------------------------------------
// Test Case Builders
// -----------------------------------------------------------------------------

/// JSON for canonical-only (no alias fields at all).
let private evidence (kindVal: string) (statusVal: string) (cmdVal: string) (evId: string) (epId: string) =
    sprintf
        """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"%s","kind":"%s","command":"%s","status":"%s","exit_code":0,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId epId kindVal cmdVal statusVal validCommitOid validTreeOid

/// JSON with one alias field only; the canonical is omitted.
let private evidenceAliasOnly
    (aliasField: string)
    (aliasVal: string)
    (kindVal: string)
    (cmdVal: string)
    (statusVal: string)
    (evId: string)
    (epId: string)
    : string =
    let baseline =
        sprintf
            """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"%s","kind":"%s","command":"%s","status":"%s","exit_code":0,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
            evId epId kindVal cmdVal statusVal validCommitOid validTreeOid

    // Replace the closing '}' with the extra alias field followed by '}'.
    baseline.Substring(0, baseline.Length - 1)
    + sprintf ",\"%s\":\"%s\"}" aliasField aliasVal

/// JSON with canonical AND alias both present with DIFFERENT values.
let private evidenceBothDifferent
    (canonField: string)
    (canonVal: string)
    (aliasField: string)
    (aliasVal: string)
    (kindVal: string)
    (cmdVal: string)
    (statusVal: string)
    (evId: string)
    (epId: string)
    : string =
    let baseline =
        sprintf
            """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"%s","kind":"%s","command":"%s","status":"%s","exit_code":0,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
            evId epId kindVal cmdVal statusVal validCommitOid validTreeOid

    baseline.Substring(0, baseline.Length - 1)
    + sprintf ",\"%s\":\"%s\",\"%s\":\"%s\"}" canonField canonVal aliasField aliasVal

/// JSON with canonical AND alias both present with the SAME value
/// (DuplicateSemanticField).  Both fields are populated with `kindVal`
/// so the parser must reject them as duplicates.
let private evidenceBothSame
    (field: string)
    (alias: string)
    (kindVal: string)
    (cmdVal: string)
    (statusVal: string)
    (evId: string)
    (epId: string)
    : string =
    evidenceBothDifferent field kindVal alias kindVal kindVal cmdVal statusVal evId epId

// -----------------------------------------------------------------------------
// kind / verification_kind tests
// -----------------------------------------------------------------------------

[<Tests>]
let kindTests =
    testList "kind" [
        test "canonical only" {
            let dir = tempDir "kind-can"
            let evId = evidenceId "1a"
            try
                createMinimalStructure dir
                writeEvidence dir [ evidence "build" "pass" "dotnet build" evId "ep-001" ]
                let vr = runVerify dir
                let hasErr = vr.Issues |> List.exists(function VerificationIssue.VerificationEvidenceLoadFailed _ -> true | _ -> false)
                Expect.isFalse hasErr "canonical-only should parse"
            finally cleanup dir
        }
        test "alias only" {
            let dir = tempDir "kind-alias"
            let evId = evidenceId "1b"
            try
                createMinimalStructure dir
                // canonical kind is missing; only verification_kind is present.
                writeEvidence dir [ evidenceAliasOnly "verification_kind" "test" "build" "dotnet build" "pass" evId "ep-002" ]
                let vr = runVerify dir
                let hasErr = vr.Issues |> List.exists(function VerificationIssue.VerificationEvidenceLoadFailed _ -> true | _ -> false)
                Expect.isFalse hasErr "alias-only should parse"
            finally cleanup dir
        }
        test "both present equal => DuplicateSemanticField" {
            let dir = tempDir "kind-same"
            let evId = evidenceId "1c"
            try
                createMinimalStructure dir
                writeEvidence dir [ evidenceBothSame "kind" "verification_kind" "build" "dotnet build" "pass" evId "ep-003" ]
                match runVerify dir with
                | { Issues = [VerificationIssue.VerificationEvidenceLoadFailed [VerificationEvidenceLoadError.ParseError(VerificationEvidenceParseError.DuplicateSemanticField(_,_,can,alias))]] } ->
                    Expect.equal can "kind" "canonical"
                    Expect.equal alias "verification_kind" "alias"
                | r -> failwithf "expected DuplicateSemanticField, got %A" r
            finally cleanup dir
        }
        test "both present different => ConflictingSemanticFields" {
            let dir = tempDir "kind-diff"
            let evId = evidenceId "1d"
            try
                createMinimalStructure dir
                writeEvidence dir [ evidenceBothDifferent "kind" "build" "verification_kind" "test" "build" "dotnet build" "pass" evId "ep-004" ]
                match runVerify dir with
                | { Issues = [VerificationIssue.VerificationEvidenceLoadFailed [VerificationEvidenceLoadError.ParseError(VerificationEvidenceParseError.ConflictingSemanticFields(_,_,can,alias,_,_))]] } ->
                    Expect.equal can "kind" "canonical"
                    Expect.equal alias "verification_kind" "alias"
                | r -> failwithf "expected ConflictingSemanticFields, got %A" r
            finally cleanup dir
        }
    ]

// -----------------------------------------------------------------------------
// status / verification_result tests
// -----------------------------------------------------------------------------

[<Tests>]
let statusTests =
    testList "status" [
        test "canonical only" {
            let dir = tempDir "status-can"
            let evId = evidenceId "2a"
            try
                createMinimalStructure dir
                writeEvidence dir [ evidence "build" "pass" "dotnet build" evId "ep-001" ]
                let vr = runVerify dir
                let hasErr = vr.Issues |> List.exists(function VerificationIssue.VerificationEvidenceLoadFailed _ -> true | _ -> false)
                Expect.isFalse hasErr "canonical-only should parse"
            finally cleanup dir
        }
        test "alias only" {
            let dir = tempDir "status-alias"
            let evId = evidenceId "2b"
            try
                createMinimalStructure dir
                // canonical status is missing; only verification_result is present.
                writeEvidence dir [ evidenceAliasOnly "verification_result" "fail" "build" "dotnet build" "pass" evId "ep-002" ]
                let vr = runVerify dir
                let hasErr = vr.Issues |> List.exists(function VerificationIssue.VerificationEvidenceLoadFailed _ -> true | _ -> false)
                Expect.isFalse hasErr "alias-only should parse"
            finally cleanup dir
        }
        test "both present equal => DuplicateSemanticField" {
            let dir = tempDir "status-same"
            let evId = evidenceId "2c"
            try
                createMinimalStructure dir
                writeEvidence dir [ evidenceBothSame "status" "verification_result" "build" "dotnet build" "pass" evId "ep-003" ]
                match runVerify dir with
                | { Issues = [VerificationIssue.VerificationEvidenceLoadFailed [VerificationEvidenceLoadError.ParseError(VerificationEvidenceParseError.DuplicateSemanticField(_,_,can,alias))]] } ->
                    Expect.equal can "status" "canonical"
                    Expect.equal alias "verification_result" "alias"
                | r -> failwithf "expected DuplicateSemanticField, got %A" r
            finally cleanup dir
        }
        test "both present different => ConflictingSemanticFields" {
            let dir = tempDir "status-diff"
            let evId = evidenceId "2d"
            try
                createMinimalStructure dir
                writeEvidence dir [ evidenceBothDifferent "status" "pass" "verification_result" "fail" "build" "dotnet build" "pass" evId "ep-004" ]
                match runVerify dir with
                | { Issues = [VerificationIssue.VerificationEvidenceLoadFailed [VerificationEvidenceLoadError.ParseError(VerificationEvidenceParseError.ConflictingSemanticFields(_,_,can,alias,_,_))]] } ->
                    Expect.equal can "status" "canonical"
                    Expect.equal alias "verification_result" "alias"
                | r -> failwithf "expected ConflictingSemanticFields, got %A" r
            finally cleanup dir
        }
        test "both wrong type => WrongFieldType canonical's actual" {
            let dir = tempDir "status-both-wrong"
            let evId = evidenceId "2e"
            try
                createMinimalStructure dir
                let json = "{\"schema_version\":\"verification-evidence-v1\",\"evidence_id\":\"" + evId + "\",\"episode_id\":\"ep-001\",\"kind\":\"build\",\"command\":\"dotnet build\",\"status\":111,\"verification_result\":222,\"exit_code\":0,\"tested_commit_oid\":\"" + validCommitOid + "\",\"tested_tree_oid\":\"" + validTreeOid + "\"}"
                writeEvidence dir [ json ]
                match runVerify dir with
                | { Issues = [VerificationIssue.VerificationEvidenceLoadFailed [VerificationEvidenceLoadError.ParseError(VerificationEvidenceParseError.WrongFieldType(_,_,field,expected,actual))]] } ->
                    Expect.equal field "status" "canonical field"
                    Expect.equal expected "string" "expected"
                    Expect.equal actual "number" "actual"
                | r -> failwithf "expected WrongFieldType, got %A" r
            finally cleanup dir
        }
        test "canonical wrong, alias valid" {
            let dir = tempDir "status-can-wrong"
            let evId = evidenceId "2f"
            try
                createMinimalStructure dir
                let json = "{\"schema_version\":\"verification-evidence-v1\",\"evidence_id\":\"" + evId + "\",\"episode_id\":\"ep-001\",\"kind\":\"build\",\"command\":\"dotnet build\",\"status\":999,\"verification_result\":\"pass\",\"exit_code\":0,\"tested_commit_oid\":\"" + validCommitOid + "\",\"tested_tree_oid\":\"" + validTreeOid + "\"}"
                writeEvidence dir [ json ]
                match runVerify dir with
                | { Issues = [VerificationIssue.VerificationEvidenceLoadFailed [VerificationEvidenceLoadError.ParseError(VerificationEvidenceParseError.WrongFieldType(_,_,field,expected,actual))]] } ->
                    Expect.equal field "status" "canonical field"
                    Expect.equal expected "string" "expected"
                    Expect.equal actual "number" "actual"
                | r -> failwithf "expected WrongFieldType, got %A" r
            finally cleanup dir
        }
        test "canonical valid, alias wrong" {
            let dir = tempDir "status-alias-wrong"
            let evId = evidenceId "2g"
            try
                createMinimalStructure dir
                let json = "{\"schema_version\":\"verification-evidence-v1\",\"evidence_id\":\"" + evId + "\",\"episode_id\":\"ep-001\",\"kind\":\"build\",\"command\":\"dotnet build\",\"status\":\"pass\",\"verification_result\":456,\"exit_code\":0,\"tested_commit_oid\":\"" + validCommitOid + "\",\"tested_tree_oid\":\"" + validTreeOid + "\"}"
                writeEvidence dir [ json ]
                match runVerify dir with
                | { Issues = [VerificationIssue.VerificationEvidenceLoadFailed [VerificationEvidenceLoadError.ParseError(VerificationEvidenceParseError.WrongFieldType(_,_,field,expected,actual))]] } ->
                    Expect.equal field "verification_result" "alias field"
                    Expect.equal expected "string" "expected"
                    Expect.equal actual "number" "actual"
                | r -> failwithf "expected WrongFieldType, got %A" r
            finally cleanup dir
        }
    ]

// -----------------------------------------------------------------------------
// command / verification_command tests
// -----------------------------------------------------------------------------

[<Tests>]
let commandTests =
    testList "command" [
        test "canonical only" {
            let dir = tempDir "cmd-can"
            let evId = evidenceId "3a"
            try
                createMinimalStructure dir
                writeEvidence dir [ evidence "build" "pass" "dotnet build" evId "ep-001" ]
                let vr = runVerify dir
                let hasErr = vr.Issues |> List.exists(function VerificationIssue.VerificationEvidenceLoadFailed _ -> true | _ -> false)
                Expect.isFalse hasErr "canonical-only should parse"
            finally cleanup dir
        }
        test "alias only" {
            let dir = tempDir "cmd-alias"
            let evId = evidenceId "3b"
            try
                createMinimalStructure dir
                // canonical command is missing; only verification_command is present.
                writeEvidence dir [ evidenceAliasOnly "verification_command" "dotnet test" "build" "dotnet build" "pass" evId "ep-002" ]
                let vr = runVerify dir
                let hasErr = vr.Issues |> List.exists(function VerificationIssue.VerificationEvidenceLoadFailed _ -> true | _ -> false)
                Expect.isFalse hasErr "alias-only should parse"
            finally cleanup dir
        }
        test "both present equal => DuplicateSemanticField" {
            let dir = tempDir "cmd-same"
            let evId = evidenceId "3c"
            try
                createMinimalStructure dir
                writeEvidence dir [ evidenceBothSame "command" "verification_command" "build" "dotnet build" "pass" evId "ep-003" ]
                match runVerify dir with
                | { Issues = [VerificationIssue.VerificationEvidenceLoadFailed [VerificationEvidenceLoadError.ParseError(VerificationEvidenceParseError.DuplicateSemanticField(_,_,can,alias))]] } ->
                    Expect.equal can "command" "canonical"
                    Expect.equal alias "verification_command" "alias"
                | r -> failwithf "expected DuplicateSemanticField, got %A" r
            finally cleanup dir
        }
        test "both present different => ConflictingSemanticFields" {
            let dir = tempDir "cmd-diff"
            let evId = evidenceId "3d"
            try
                createMinimalStructure dir
                writeEvidence dir [ evidenceBothDifferent "command" "dotnet build" "verification_command" "dotnet test" "build" "dotnet build" "pass" evId "ep-004" ]
                match runVerify dir with
                | { Issues = [VerificationIssue.VerificationEvidenceLoadFailed [VerificationEvidenceLoadError.ParseError(VerificationEvidenceParseError.ConflictingSemanticFields(_,_,can,alias,_,_))]] } ->
                    Expect.equal can "command" "canonical"
                    Expect.equal alias "verification_command" "alias"
                | r -> failwithf "expected ConflictingSemanticFields, got %A" r
            finally cleanup dir
        }
    ]
