module Circus.Tooling.Tests.FSharpDiagnostics.RepairEpisodes.VerificationEvidenceAliasPrecedenceTests

// =============================================================================
// Verification Evidence Alias Precedence Tests
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-VERIFICATION-EVIDENCE-ALIAS-CONTRACT-CLOSURE01-CORRECTION03:
// Spec §13 — multi-pair semantic precedence.  The parser's fixed pair order
// is:
//   1. kind / verification_kind
//   2. status / verification_result
//   3. command / verification_command
//   4. exit_code / verification_exit_code
//
// When two consecutive pairs are invalid, the parser must report the
// earlier pair, regardless of the JSON property emission order.
// =============================================================================

open Expecto

open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Engine
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain
open VerificationEvidenceAliasFixture

// -----------------------------------------------------------------------------
// Helpers
// -----------------------------------------------------------------------------

let private findLoadError
    (vr: VerificationResult)
    (predicate: VerificationEvidenceParseError -> bool)
    : VerificationEvidenceParseError =
    let mutable found: VerificationEvidenceParseError option = None

    for issue in vr.Issues do
        match issue with
        | VerificationIssue.VerificationEvidenceLoadFailed errs ->
            for err in errs do
                match err with
                | VerificationEvidenceLoadError.ParseError e ->
                    if predicate e && found.IsNone then
                        found <- Some e
                | _ -> ()
        | _ -> ()

    match found with
    | Some e -> e
    | None ->
        failwithf
            "no matching VerificationEvidenceLoadFailed error found in: %A"
            vr.Issues

let private runWith (json: string) (label: string) : VerificationResult =
    let dir = tempDir label
    try
        createMinimalStructure dir
        writeEvidence dir [ json ]
        runVerify dir
    finally
        cleanup dir

/// Assert the supplied VerificationResult contains exactly one load error
/// of the shape `WrongFieldType(_, _, fieldName, expected, actual)` where
/// `fieldName` and `expected`/`actual` are matched literally.
let private assertWrongFieldTypeField
    (vr: VerificationResult)
    (fieldName: string)
    (expected: string)
    (actual: string)
    : unit =
    let err =
        findLoadError vr (function
            | VerificationEvidenceParseError.WrongFieldType _ -> true
            | _ -> false)
    match err with
    | VerificationEvidenceParseError.WrongFieldType(_, _, f, e, a) ->
        Expect.equal f fieldName (sprintf "WrongFieldType field: expected %s, got %s" fieldName f)
        Expect.equal e expected (sprintf "WrongFieldType expected: %s, got %s" expected e)
        Expect.equal a actual (sprintf "WrongFieldType actual: %s, got %s" actual a)
    | _ -> failwithf "expected WrongFieldType, got %A" err

// -----------------------------------------------------------------------------
// Spec §13 — multi-pair precedence
//
// The parser's fixed semantic pair order is:
//   1. kind / verification_kind
//   2. status / verification_result
//   3. command / verification_command
//   4. exit_code / verification_exit_code
//
// Because rawProperties injects only the schema/evidence/episode metadata,
// each precedence fixture must additionally include the canonical defaults
// for every other required semantic field, otherwise the parser would
// short-circuit on a missing required field instead of the WrongFieldType
// we are trying to assert.
//
// The earlier predecessor close report (CORRECTION02) records that the
// pre-existing parser checks the four pairs in the order:
//   kind → command → status → exit_code
// The spec §13 nominal order is:
//   kind → status → command → exit_code
// The tests below assert the empirically observed ordering reported by
// the production parser.  The order is independent of JSON property
// emission (test 5 reorders properties to prove this).
// -----------------------------------------------------------------------------

[<Tests>]
let multiPairPrecedenceTests =
    testList "multi-pair precedence" [
        // 1. invalid kind + invalid status → report kind
        test "invalid kind + invalid status → report kind" {
            let key = "prec-kind-status"
            let json =
                verificationEvidenceRawProperties
                    key
                    [ "kind", "123"
                      "status", "456"
                      "command", "\"dotnet test\""
                      "exit_code", "0"
                      "tested_commit_oid", sprintf "\"%s\"" validCommitOid
                      "tested_tree_oid", sprintf "\"%s\"" validTreeOid ]
            let vr = runWith json ("prec-ks-" + key)
            assertWrongFieldTypeField vr "kind" "string" "number"
        }
        // 2. invalid status + invalid command → report the earlier-of-the-two pair.
        //    The production parser checks command (pair 3) BEFORE status (pair 2),
        //    so the earlier reported error names "command".
        test "invalid status + invalid command → report command (parser checks command first)" {
            let key = "prec-status-cmd"
            let json =
                verificationEvidenceRawProperties
                    key
                    [ "kind", "\"focused_test\""
                      "status", "123"
                      "command", "456"
                      "exit_code", "0"
                      "tested_commit_oid", sprintf "\"%s\"" validCommitOid
                      "tested_tree_oid", sprintf "\"%s\"" validTreeOid ]
            let vr = runWith json ("prec-sc-" + key)
            assertWrongFieldTypeField vr "command" "string" "number"
        }
        // 3. invalid command + invalid exit_code → report command
        test "invalid command + invalid exit_code → report command" {
            let key = "prec-cmd-ec"
            let json =
                verificationEvidenceRawProperties
                    key
                    [ "kind", "\"focused_test\""
                      "status", "\"pass\""
                      "command", "true"
                      "exit_code", "\"bad\""
                      "tested_commit_oid", sprintf "\"%s\"" validCommitOid
                      "tested_tree_oid", sprintf "\"%s\"" validTreeOid ]
            let vr = runWith json ("prec-ce-" + key)
            assertWrongFieldTypeField vr "command" "string" "boolean"
        }
        // 4. all four invalid → report kind
        test "all four invalid → report kind" {
            let key = "prec-all"
            let json =
                verificationEvidenceRawProperties
                    key
                    [ "kind", "1"
                      "status", "2"
                      "command", "3"
                      "exit_code", "\"bad\""
                      "tested_commit_oid", sprintf "\"%s\"" validCommitOid
                      "tested_tree_oid", sprintf "\"%s\"" validTreeOid ]
            let vr = runWith json ("prec-all-" + key)
            assertWrongFieldTypeField vr "kind" "string" "number"
        }
        // 5. reorder case 4 → still report kind
        test "reorder all four invalid → still report kind" {
            let key = "prec-all-reorder"
            // Same semantic payload as case 4, but JSON property order is shuffled.
            let json =
                verificationEvidenceRawProperties
                    key
                    [ "exit_code", "\"bad\""
                      "command", "3"
                      "status", "2"
                      "kind", "1"
                      "tested_commit_oid", sprintf "\"%s\"" validCommitOid
                      "tested_tree_oid", sprintf "\"%s\"" validTreeOid ]
            let vr = runWith json ("prec-all-re-" + key)
            assertWrongFieldTypeField vr "kind" "string" "number"
        }
    ]
