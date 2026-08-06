module Circus.Tooling.Tests.FSharpDiagnostics.RepairEpisodes.VerificationEvidenceIntegerAliasTests

// =============================================================================
// Verification Evidence Integer Alias Tests
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-VERIFICATION-EVIDENCE-ALIAS-CONTRACT-CLOSURE01-CORRECTION03:
// Spec §12 — full 12-case matrix applied to:
//   - exit_code / verification_exit_code
//
// CORRECTION04 — every successful canonical-only and alias-only case
// asserts the parsed domain value `evidence.ExitCode` using the
// strict-parsing seam `parseAndAssert`.  This proves the parser
// resolves alias spelling to the correct integer domain member rather
// than merely accepting the record silently.
//
// Required cases:
//   1.  canonical only
//   2.  alias only
//   3.  both present equal
//   4.  both present different
//   5.  canonical wrong type, alias valid
//   6.  canonical valid, alias wrong type
//   7.  both wrong type
//   8.  canonical fractional (1.5)
//   9.  alias fractional (2.5)
//   10. both fractional (1.5, 2.5)
//   11. value > Int32.MaxValue
//   12. negative value
//
// A valid non-negative non-zero integer must parse successfully.
// =============================================================================

open Expecto

open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Engine
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain
open VerificationEvidenceAliasFixture

// -----------------------------------------------------------------------------
// Helpers
// -----------------------------------------------------------------------------

let private hasLoadError
    (vr: VerificationResult)
    (predicate: VerificationEvidenceParseError -> bool)
    : bool =
    vr.Issues
    |> List.exists (function
        | VerificationIssue.VerificationEvidenceLoadFailed errs ->
            errs |> List.exists (function
                | VerificationEvidenceLoadError.ParseError e -> predicate e
                | _ -> false)
        | _ -> false)

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

// -----------------------------------------------------------------------------
// exit_code / verification_exit_code — 12 cases
// -----------------------------------------------------------------------------

[<Tests>]
let exitCodeTests =
    testList "exit_code" [
        // 1. canonical only → success, parsed ExitCode = 3
        test "canonical only → evidence.ExitCode = 3 and no verification_exit_code property" {
            let key = "ec-canonical-only"
            let json = verificationEvidenceCanonicalOnly key "exit_code" "3"
            Expect.equal (propertyOccurrences json "exit_code") 1 "canonical 'exit_code' must appear once"
            Expect.equal (propertyOccurrences json "verification_exit_code") 0 "alias 'verification_exit_code' must be absent"
            let evidence = parseAndAssert key json
            Expect.equal evidence.ExitCode 3 "parsed ExitCode must equal 3"
        }
        // 2. alias only → success, parsed ExitCode = 7 (resolved from alias), no canonical
        test "alias only → evidence.ExitCode = 7 and no canonical 'exit_code' property emitted" {
            let key = "ec-alias-only"
            let json = verificationEvidenceAliasOnly key "verification_exit_code" "7"
            Expect.equal (propertyOccurrences json "exit_code") 0 "canonical 'exit_code' must be absent"
            Expect.equal (propertyOccurrences json "verification_exit_code") 1 "alias 'verification_exit_code' must appear once"
            let evidence = parseAndAssert key json
            Expect.equal evidence.ExitCode 7 "parsed ExitCode must equal 7 (resolved from verification_exit_code alias)"
        }
        // 3. both present equal → DuplicateSemanticField
        test "both present equal → DuplicateSemanticField" {
            let key = "ec-both-equal"
            let json =
                verificationEvidenceBothPresent
                    key
                    "exit_code"
                    "5"
                    "verification_exit_code"
                    "5"
            let vr = runWith json ("ec-be-" + key)
            Expect.isTrue
                (hasLoadError vr (function
                    | VerificationEvidenceParseError.DuplicateSemanticField(_, _, "exit_code", "verification_exit_code") -> true
                    | _ -> false))
                "DuplicateSemanticField naming 'exit_code' and 'verification_exit_code' expected"
        }
        // 4. both present different → ConflictingSemanticFields
        test "both present different → ConflictingSemanticFields" {
            let key = "ec-both-diff"
            let json =
                verificationEvidenceBothPresent
                    key
                    "exit_code"
                    "0"
                    "verification_exit_code"
                    "1"
            let vr = runWith json ("ec-bd-" + key)
            let err =
                findLoadError vr (function
                    | VerificationEvidenceParseError.ConflictingSemanticFields _ -> true
                    | _ -> false)
            match err with
            | VerificationEvidenceParseError.ConflictingSemanticFields(_, _, "exit_code", "verification_exit_code", "0", "1") -> ()
            | VerificationEvidenceParseError.ConflictingSemanticFields(_, _, c, a, cv, av) ->
                failwithf "ConflictingSemanticFields: canonical=%s alias=%s cv=%s av=%s (expected exit_code/verification_exit_code/0/1)"
                    c a cv av
            | _ -> failwithf "expected ConflictingSemanticFields, got %A" err
        }
        // 5. canonical wrong type, alias valid → canonical WrongFieldType
        test "canonical wrong type (\"zero\"), alias valid → canonical WrongFieldType" {
            let key = "ec-cw-av"
            let json =
                verificationEvidenceBothPresent
                    key
                    "exit_code"
                    "\"zero\""
                    "verification_exit_code"
                    "1"
            let vr = runWith json ("ec-cwa-" + key)
            let err =
                findLoadError vr (function
                    | VerificationEvidenceParseError.WrongFieldType _ -> true
                    | _ -> false)
            match err with
            | VerificationEvidenceParseError.WrongFieldType(_, _, "exit_code", "integer", "string") -> ()
            | VerificationEvidenceParseError.WrongFieldType(_, _, f, e, a) ->
                failwithf "WrongFieldType: field=%s expected=%s actual=%s (expected exit_code/integer/string)" f e a
            | _ -> failwithf "expected WrongFieldType, got %A" err
        }
        // 6. canonical valid, alias wrong type → alias WrongFieldType
        test "canonical valid, alias wrong type → alias WrongFieldType" {
            let key = "ec-cv-aw"
            let json =
                verificationEvidenceBothPresent
                    key
                    "exit_code"
                    "1"
                    "verification_exit_code"
                    "\"bad\""
            let vr = runWith json ("ec-cva-" + key)
            let err =
                findLoadError vr (function
                    | VerificationEvidenceParseError.WrongFieldType _ -> true
                    | _ -> false)
            match err with
            | VerificationEvidenceParseError.WrongFieldType(_, _, "verification_exit_code", "integer", "string") -> ()
            | VerificationEvidenceParseError.WrongFieldType(_, _, f, e, a) ->
                failwithf "WrongFieldType: field=%s expected=%s actual=%s (expected verification_exit_code/integer/string)" f e a
            | _ -> failwithf "expected WrongFieldType, got %A" err
        }
        // 7. both wrong type → canonical WrongFieldType
        test "both wrong type → canonical WrongFieldType" {
            let key = "ec-both-wrong"
            let json =
                verificationEvidenceBothPresent
                    key
                    "exit_code"
                    "\"zero\""
                    "verification_exit_code"
                    "\"one\""
            let vr = runWith json ("ec-bw-" + key)
            let err =
                findLoadError vr (function
                    | VerificationEvidenceParseError.WrongFieldType _ -> true
                    | _ -> false)
            match err with
            | VerificationEvidenceParseError.WrongFieldType(_, _, "exit_code", "integer", "string") -> ()
            | VerificationEvidenceParseError.WrongFieldType(_, _, f, e, a) ->
                failwithf "WrongFieldType: field=%s expected=%s actual=%s (expected exit_code/integer/string)" f e a
            | _ -> failwithf "expected WrongFieldType, got %A" err
        }
        // 8. canonical fractional → InvalidExitCode
        test "canonical fractional (1.5) → InvalidExitCode" {
            let key = "ec-canon-frac"
            let json = verificationEvidenceCanonicalOnly key "exit_code" "1.5"
            let vr = runWith json ("ec-cf-" + key)
            Expect.isTrue
                (hasLoadError vr (function
                    | VerificationEvidenceParseError.InvalidExitCode _ -> true
                    | _ -> false))
                "InvalidExitCode expected for canonical fractional exit_code"
        }
        // 9. alias fractional → InvalidExitCode
        test "alias fractional (2.5) → InvalidExitCode" {
            let key = "ec-alias-frac"
            let json = verificationEvidenceAliasOnly key "verification_exit_code" "2.5"
            let vr = runWith json ("ec-af-" + key)
            Expect.isTrue
                (hasLoadError vr (function
                    | VerificationEvidenceParseError.InvalidExitCode _ -> true
                    | _ -> false))
                "InvalidExitCode expected for alias fractional exit_code"
        }
        // 10. both fractional → InvalidExitCode
        test "both fractional (1.5, 2.5) → InvalidExitCode" {
            let key = "ec-both-frac"
            let json =
                verificationEvidenceBothPresent
                    key
                    "exit_code"
                    "1.5"
                    "verification_exit_code"
                    "2.5"
            let vr = runWith json ("ec-bf-" + key)
            Expect.isTrue
                (hasLoadError vr (function
                    | VerificationEvidenceParseError.InvalidExitCode _ -> true
                    | _ -> false))
                "InvalidExitCode expected for both fractional exit_code"
        }
        // 11. value > Int32.MaxValue → InvalidExitCode
        test "value > Int32.MaxValue (9999999999) → InvalidExitCode" {
            let key = "ec-overrange"
            let json = verificationEvidenceCanonicalOnly key "exit_code" "9999999999"
            let vr = runWith json ("ec-or-" + key)
            Expect.isTrue
                (hasLoadError vr (function
                    | VerificationEvidenceParseError.InvalidExitCode _ -> true
                    | _ -> false))
                "InvalidExitCode expected for over-range exit_code"
        }
        // 12. negative value → InvalidExitCode
        test "negative value (-1) → InvalidExitCode" {
            let key = "ec-neg"
            let json = verificationEvidenceCanonicalOnly key "exit_code" "-1"
            let vr = runWith json ("ec-neg-" + key)
            Expect.isTrue
                (hasLoadError vr (function
                    | VerificationEvidenceParseError.InvalidExitCode _ -> true
                    | _ -> false))
                "InvalidExitCode expected for negative exit_code"
        }
    ]
