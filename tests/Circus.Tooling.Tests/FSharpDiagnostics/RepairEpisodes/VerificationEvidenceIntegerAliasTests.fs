module Circus.Tooling.Tests.FSharpDiagnostics.RepairEpisodes.VerificationEvidenceIntegerAliasTests

// =============================================================================
// Verification Evidence Integer Alias Tests
//
// Tests for integer-typed alias pair:
//   - exit_code / verification_exit_code
// =============================================================================

open Expecto

open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Engine
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain
open VerificationEvidenceAliasFixture

// -----------------------------------------------------------------------------
// Test Case Builders
// -----------------------------------------------------------------------------

let private evidence ec (evId: string) (epId: string) =
    sprintf
        """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"%s","kind":"build","command":"dotnet build","status":"pass","exit_code":%d,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId epId ec validCommitOid validTreeOid

let private evidenceWithAlias ec ecAlias (evId: string) (epId: string) =
    sprintf
        """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"%s","kind":"build","command":"dotnet build","status":"pass","exit_code":%d,"verification_exit_code":%d,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId epId ec ecAlias validCommitOid validTreeOid

let private evidenceWithWrongType (ecJson: string) (ecAliasJson: string) (evId: string) =
    sprintf
        """{"schema_version":"verification-evidence-v1","evidence_id":"%s","episode_id":"ep-001","kind":"build","command":"dotnet build","status":"pass","exit_code":%s,"verification_exit_code":%s,"tested_commit_oid":"%s","tested_tree_oid":"%s"}"""
        evId ecJson ecAliasJson validCommitOid validTreeOid

// -----------------------------------------------------------------------------
// exit_code / verification_exit_code tests
// -----------------------------------------------------------------------------

[<Tests>]
let tests =
    testList "exit_code" [
        test "canonical only" {
            let dir = tempDir "exit-can"
            let evId = evidenceId "4a"
            try
                createMinimalStructure dir
                writeEvidence dir [ evidence 0 evId "ep-001" ]
                let vr = runVerify dir
                let hasErr = vr.Issues |> List.exists(function VerificationIssue.VerificationEvidenceLoadFailed _ -> true | _ -> false)
                Expect.isFalse hasErr "canonical-only should parse"
            finally cleanup dir
        }
        test "alias only" {
            let dir = tempDir "exit-alias"
            let evId = evidenceId "4b"
            try
                createMinimalStructure dir
                writeEvidence dir [ evidenceWithAlias 0 1 evId "ep-002" ]
                let vr = runVerify dir
                let hasErr = vr.Issues |> List.exists(function VerificationIssue.VerificationEvidenceLoadFailed _ -> true | _ -> false)
                Expect.isFalse hasErr "alias-only should parse"
            finally cleanup dir
        }
        test "both same => DuplicateSemanticField" {
            let dir = tempDir "exit-same"
            let evId = evidenceId "4c"
            try
                createMinimalStructure dir
                writeEvidence dir [ evidenceWithAlias 0 0 evId "ep-003" ]
                match runVerify dir with
                | { Issues = [VerificationIssue.VerificationEvidenceLoadFailed [VerificationEvidenceLoadError.ParseError(VerificationEvidenceParseError.DuplicateSemanticField(_,_,can,alias))]] } ->
                    Expect.equal can "exit_code" "canonical"
                    Expect.equal alias "verification_exit_code" "alias"
                | r -> failwithf "expected DuplicateSemanticField, got %A" r
            finally cleanup dir
        }
        test "both different => ConflictingSemanticFields" {
            let dir = tempDir "exit-diff"
            let evId = evidenceId "4d"
            try
                createMinimalStructure dir
                writeEvidence dir [ evidenceWithAlias 0 1 evId "ep-004" ]
                match runVerify dir with
                | { Issues = [VerificationIssue.VerificationEvidenceLoadFailed [VerificationEvidenceLoadError.ParseError(VerificationEvidenceParseError.ConflictingSemanticFields(_,_,can,alias,_,_))]] } ->
                    Expect.equal can "exit_code" "canonical"
                    Expect.equal alias "verification_exit_code" "alias"
                | r -> failwithf "expected ConflictingSemanticFields, got %A" r
            finally cleanup dir
        }
        test "both wrong type => WrongFieldType" {
            let dir = tempDir "exit-both-wrong"
            let evId = evidenceId "4e"
            try
                createMinimalStructure dir
                writeEvidence dir [ evidenceWithWrongType "\"zero\"" "\"one\"" evId ]
                match runVerify dir with
                | { Issues = [VerificationIssue.VerificationEvidenceLoadFailed [VerificationEvidenceLoadError.ParseError(VerificationEvidenceParseError.WrongFieldType(_,_,field,expected,actual))]] } ->
                    Expect.equal field "exit_code" "canonical field"
                    Expect.equal expected "integer" "expected"
                    Expect.equal actual "string" "canonical's actual"
                | r -> failwithf "expected WrongFieldType, got %A" r
            finally cleanup dir
        }
        test "canonical wrong type, alias valid" {
            let dir = tempDir "exit-can-wrong"
            let evId = evidenceId "4f"
            try
                createMinimalStructure dir
                writeEvidence dir [ evidenceWithWrongType "\"bad\"" "1" evId ]
                match runVerify dir with
                | { Issues = [VerificationIssue.VerificationEvidenceLoadFailed [VerificationEvidenceLoadError.ParseError(VerificationEvidenceParseError.WrongFieldType(_,_,field,expected,actual))]] } ->
                    Expect.equal field "exit_code" "canonical field"
                    Expect.equal expected "integer" "expected"
                    Expect.equal actual "string" "canonical's actual"
                | r -> failwithf "expected WrongFieldType, got %A" r
            finally cleanup dir
        }
        test "canonical valid, alias wrong type" {
            let dir = tempDir "exit-alias-wrong"
            let evId = evidenceId "4g"
            try
                createMinimalStructure dir
                writeEvidence dir [ evidenceWithWrongType "0" "\"bad\"" evId ]
                match runVerify dir with
                | { Issues = [VerificationIssue.VerificationEvidenceLoadFailed [VerificationEvidenceLoadError.ParseError(VerificationEvidenceParseError.WrongFieldType(_,_,field,expected,actual))]] } ->
                    Expect.equal field "verification_exit_code" "alias field"
                    Expect.equal expected "integer" "expected"
                    Expect.equal actual "string" "alias's actual"
                | r -> failwithf "expected WrongFieldType, got %A" r
            finally cleanup dir
        }
        test "canonical valid, alias fractional => InvalidExitCode" {
            let dir = tempDir "exit-frac"
            let evId = evidenceId "4h"
            try
                createMinimalStructure dir
                writeEvidence dir [ evidenceWithAlias 0 1 evId "ep-005" ]
                    .Replace("\"verification_exit_code\":1", "\"verification_exit_code\":1.5")
                let json = evidenceWithAlias 0 1 evId "ep-005"
                writeEvidence dir [ json.Replace("\"verification_exit_code\":1", "\"verification_exit_code\":1.5") ]
                match runVerify dir with
                | { Issues = [VerificationIssue.VerificationEvidenceLoadFailed [VerificationEvidenceLoadError.ParseError(VerificationEvidenceParseError.InvalidExitCode _)]] } -> ()
                | r -> failwithf "expected InvalidExitCode, got %A" r
            finally cleanup dir
        }
        test "both fractional => InvalidExitCode" {
            let dir = tempDir "exit-both-frac"
            let evId = evidenceId "4i"
            try
                createMinimalStructure dir
                writeEvidence dir [ evidenceWithAlias 1 1 evId "ep-006" ]
                    .Replace("\"exit_code\":1,\"verification_exit_code\":1", "\"exit_code\":1.5,\"verification_exit_code\":2.5")
                let json = evidenceWithAlias 1 1 evId "ep-006"
                writeEvidence dir [ json.Replace("\"exit_code\":1,\"verification_exit_code\":1", "\"exit_code\":1.5,\"verification_exit_code\":2.5") ]
                match runVerify dir with
                | { Issues = [VerificationIssue.VerificationEvidenceLoadFailed [VerificationEvidenceLoadError.ParseError(VerificationEvidenceParseError.InvalidExitCode _)]] } -> ()
                | r -> failwithf "expected InvalidExitCode, got %A" r
            finally cleanup dir
        }
        test "out of Int32 range => InvalidExitCode" {
            let dir = tempDir "exit-range"
            let evId = evidenceId "4j"
            try
                createMinimalStructure dir
                writeEvidence dir [ evidence 9999999999 evId "ep-007" ]
                match runVerify dir with
                | { Issues = [VerificationIssue.VerificationEvidenceLoadFailed [VerificationEvidenceLoadError.ParseError(VerificationEvidenceParseError.InvalidExitCode _)]] } -> ()
                | r -> failwithf "expected InvalidExitCode, got %A" r
            finally cleanup dir
        }
        test "negative => InvalidExitCode" {
            let dir = tempDir "exit-neg"
            let evId = evidenceId "4k"
            try
                createMinimalStructure dir
                writeEvidence dir [ evidence -1 evId "ep-008" ]
                match runVerify dir with
                | { Issues = [VerificationIssue.VerificationEvidenceLoadFailed [VerificationEvidenceLoadError.ParseError(VerificationEvidenceParseError.InvalidExitCode _)]] } -> ()
                | r -> failwithf "expected InvalidExitCode, got %A" r
            finally cleanup dir
        }
    ]
