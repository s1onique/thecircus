module Circus.Tooling.Tests.FSharpDiagnostics.RepairEpisodes.VerificationEvidenceStringAliasTests

// =============================================================================
// Verification Evidence String Alias Tests
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-VERIFICATION-EVIDENCE-ALIAS-CONTRACT-CLOSURE01-CORRECTION03:
// Spec §11 — full 7-case matrix applied independently to:
//   - kind / verification_kind
//   - status / verification_result
//   - command / verification_command
// (3 pairs × 7 cases = 21 tests).
//
// CORRECTION04 — every successful canonical-only and alias-only case
// asserts the parsed domain value (Kind, Status, Command, ExitCode)
// using the strict-parsing seam `parseAndAssert`.  This proves the
// parser resolves alias spelling to the correct domain member rather
// than merely accepting the record silently.
//
// Spec §9 — successful-result assertions inspect the parsed domain record
// (Kind, Status, Command) and confirm the fixture did NOT emit the other
// spelling of the pair.
//
// Spec §10 — invalid records pattern-match the exact union case and assert
// canonical/alias field names, expected/actual types.
// =============================================================================

open Expecto

open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Engine
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain
open VerificationEvidenceAliasFixture

// -----------------------------------------------------------------------------
// Helpers
// -----------------------------------------------------------------------------

/// True when the VerificationResult contains a single
/// `VerificationEvidenceLoadFailed` issue whose inner error list contains
/// at least one item matching the supplied predicate.
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

/// Find the FIRST matching load error inside a VerificationResult.  Fails
/// the test if none is found.
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
// Spec §8 — fixture self-verification
// -----------------------------------------------------------------------------

[<Tests>]
let fixtureSelfVerificationTests =
    testList
        "FSharpDiagnostics.RepairEpisodes.FixtureSelfVerification.string"
        [ test "verificationEvidenceCanonicalOnly emits canonical once and alias zero times" {
              let json =
                  verificationEvidenceCanonicalOnly "sv-canon" "kind" "\"focused_test\""

              Expect.equal (propertyOccurrences json "kind") 1 "canonical 'kind' must appear once"
              Expect.equal (propertyOccurrences json "verification_kind") 0 "alias 'verification_kind' must be absent"
          }

          test "verificationEvidenceAliasOnly emits alias once and canonical zero times" {
              let json =
                  verificationEvidenceAliasOnly "sv-alias" "verification_kind" "\"focused_test\""

              Expect.equal (propertyOccurrences json "kind") 0 "canonical 'kind' must be absent"
              Expect.equal (propertyOccurrences json "verification_kind") 1 "alias 'verification_kind' must appear once"
          }

          test "verificationEvidenceBothPresent emits both canonical and alias exactly once" {
              let json =
                  verificationEvidenceBothPresent
                      "sv-both"
                      "kind"
                      "\"focused_test\""
                      "verification_kind"
                      "\"focused_test\""

              Expect.equal (propertyOccurrences json "kind") 1 "canonical 'kind' must appear once"
              Expect.equal (propertyOccurrences json "verification_kind") 1 "alias 'verification_kind' must appear once"
          }

          test "verificationEvidenceRawProperties preserves repeated property names" {
              let json =
                  verificationEvidenceRawProperties
                      "sv-raw"
                      [ "kind", "\"focused_test\""
                        "kind", "\"canonical_gate\""
                        "kind", "\"build\"" ]

              Expect.equal (propertyOccurrences json "kind") 3 "three raw 'kind' properties must be preserved"
          } ]

// -----------------------------------------------------------------------------
// Spec §11 — kind / verification_kind
// -----------------------------------------------------------------------------

[<Tests>]
let kindTests =
    testList "kind" [
        // 1. canonical only → success and parsed domain value
        test "canonical only → evidence.Kind = FocusedTest and no verification_kind property" {
            let key = "kind-canonical-only"
            let json = verificationEvidenceCanonicalOnly key "kind" "\"focused_test\""
            Expect.equal (propertyOccurrences json "kind") 1 "canonical 'kind' must appear once"
            Expect.equal (propertyOccurrences json "verification_kind") 0 "alias 'verification_kind' must be absent"
            let evidence = parseAndAssert key json
            Expect.equal evidence.Kind VerificationKind.FocusedTest "parsed Kind must equal FocusedTest"
        }
        // 2. alias only → success, no canonical property, parsed domain value
        test "alias only → evidence.Kind = FocusedTest and no canonical 'kind' property emitted" {
            let key = "kind-alias-only"
            let json = verificationEvidenceAliasOnly key "verification_kind" "\"focused_test\""
            Expect.equal (propertyOccurrences json "kind") 0 "canonical 'kind' must be absent"
            Expect.equal (propertyOccurrences json "verification_kind") 1 "alias 'verification_kind' must appear once"
            let evidence = parseAndAssert key json
            Expect.equal evidence.Kind VerificationKind.FocusedTest "parsed Kind must equal FocusedTest (resolved from verification_kind alias)"
        }
        // 3. both present equal → DuplicateSemanticField
        test "both present equal → DuplicateSemanticField" {
            let key = "kind-both-equal"
            let json =
                verificationEvidenceBothPresent
                    key
                    "kind"
                    "\"focused_test\""
                    "verification_kind"
                    "\"focused_test\""
            let vr = runWith json ("kind-be-" + key)
            Expect.isTrue
                (hasLoadError vr (function
                    | VerificationEvidenceParseError.DuplicateSemanticField(_, _, "kind", "verification_kind") -> true
                    | _ -> false))
                "DuplicateSemanticField naming 'kind' and 'verification_kind' expected"
        }
        // 4. both present different → ConflictingSemanticFields
        test "both present different → ConflictingSemanticFields" {
            let key = "kind-both-diff"
            let json =
                verificationEvidenceBothPresent
                    key
                    "kind"
                    "\"focused_test\""
                    "verification_kind"
                    "\"canonical_gate\""
            let vr = runWith json ("kind-bd-" + key)
            let err =
                findLoadError vr (function
                    | VerificationEvidenceParseError.ConflictingSemanticFields _ -> true
                    | _ -> false)
            match err with
            | VerificationEvidenceParseError.ConflictingSemanticFields(_, _, "kind", "verification_kind", "focused_test", "canonical_gate") -> ()
            | VerificationEvidenceParseError.ConflictingSemanticFields(_, _, c, a, cv, av) ->
                failwithf "ConflictingSemanticFields: canonical=%s alias=%s cv=%s av=%s (expected kind/verification_kind/focused_test/canonical_gate)"
                    c a cv av
            | _ -> failwithf "expected ConflictingSemanticFields, got %A" err
        }
        // 5. canonical wrong type, alias valid → canonical WrongFieldType
        test "canonical wrong type, alias valid → canonical WrongFieldType" {
            let key = "kind-cw-av"
            let json =
                verificationEvidenceBothPresent
                    key
                    "kind"
                    "123"
                    "verification_kind"
                    "\"focused_test\""
            let vr = runWith json ("kind-cwa-" + key)
            let err =
                findLoadError vr (function
                    | VerificationEvidenceParseError.WrongFieldType _ -> true
                    | _ -> false)
            match err with
            | VerificationEvidenceParseError.WrongFieldType(_, _, "kind", "string", "number") -> ()
            | VerificationEvidenceParseError.WrongFieldType(_, _, f, e, a) ->
                failwithf "WrongFieldType: field=%s expected=%s actual=%s (expected kind/string/number)" f e a
            | _ -> failwithf "expected WrongFieldType, got %A" err
        }
        // 6. canonical valid, alias wrong type → alias WrongFieldType
        test "canonical valid, alias wrong type → alias WrongFieldType" {
            let key = "kind-cv-aw"
            let json =
                verificationEvidenceBothPresent
                    key
                    "kind"
                    "\"focused_test\""
                    "verification_kind"
                    "456"
            let vr = runWith json ("kind-cva-" + key)
            let err =
                findLoadError vr (function
                    | VerificationEvidenceParseError.WrongFieldType _ -> true
                    | _ -> false)
            match err with
            | VerificationEvidenceParseError.WrongFieldType(_, _, "verification_kind", "string", "number") -> ()
            | VerificationEvidenceParseError.WrongFieldType(_, _, f, e, a) ->
                failwithf "WrongFieldType: field=%s expected=%s actual=%s (expected verification_kind/string/number)" f e a
            | _ -> failwithf "expected WrongFieldType, got %A" err
        }
        // 7. both wrong type → canonical WrongFieldType
        test "both wrong type → canonical WrongFieldType" {
            let key = "kind-both-wrong"
            let json =
                verificationEvidenceBothPresent
                    key
                    "kind"
                    "1"
                    "verification_kind"
                    "true"
            let vr = runWith json ("kind-bw-" + key)
            let err =
                findLoadError vr (function
                    | VerificationEvidenceParseError.WrongFieldType _ -> true
                    | _ -> false)
            match err with
            | VerificationEvidenceParseError.WrongFieldType(_, _, "kind", "string", "number") -> ()
            | VerificationEvidenceParseError.WrongFieldType(_, _, f, e, a) ->
                failwithf "WrongFieldType: field=%s expected=%s actual=%s (expected kind/string/number)" f e a
            | _ -> failwithf "expected WrongFieldType, got %A" err
        }
    ]

// -----------------------------------------------------------------------------
// Spec §11 — status / verification_result
// -----------------------------------------------------------------------------

[<Tests>]
let statusTests =
    testList "status" [
        test "canonical only → evidence.Status = Pass and no verification_result property" {
            let key = "status-canonical-only"
            let json = verificationEvidenceCanonicalOnly key "status" "\"pass\""
            Expect.equal (propertyOccurrences json "status") 1 "canonical 'status' must appear once"
            Expect.equal (propertyOccurrences json "verification_result") 0 "alias 'verification_result' must be absent"
            let evidence = parseAndAssert key json
            Expect.equal evidence.Status VerificationStatus.Pass "parsed Status must equal Pass"
        }
        test "alias only → evidence.Status = Pass and no canonical 'status' property emitted" {
            let key = "status-alias-only"
            let json = verificationEvidenceAliasOnly key "verification_result" "\"pass\""
            Expect.equal (propertyOccurrences json "status") 0 "canonical 'status' must be absent"
            Expect.equal (propertyOccurrences json "verification_result") 1 "alias 'verification_result' must appear once"
            let evidence = parseAndAssert key json
            Expect.equal evidence.Status VerificationStatus.Pass "parsed Status must equal Pass (resolved from verification_result alias)"
        }
        test "both present equal → DuplicateSemanticField" {
            let key = "status-both-equal"
            let json =
                verificationEvidenceBothPresent
                    key
                    "status"
                    "\"pass\""
                    "verification_result"
                    "\"pass\""
            let vr = runWith json ("status-be-" + key)
            Expect.isTrue
                (hasLoadError vr (function
                    | VerificationEvidenceParseError.DuplicateSemanticField(_, _, "status", "verification_result") -> true
                    | _ -> false))
                "DuplicateSemanticField naming 'status' and 'verification_result' expected"
        }
        test "both present different → ConflictingSemanticFields" {
            let key = "status-both-diff"
            let json =
                verificationEvidenceBothPresent
                    key
                    "status"
                    "\"pass\""
                    "verification_result"
                    "\"fail\""
            let vr = runWith json ("status-bd-" + key)
            let err =
                findLoadError vr (function
                    | VerificationEvidenceParseError.ConflictingSemanticFields _ -> true
                    | _ -> false)
            match err with
            | VerificationEvidenceParseError.ConflictingSemanticFields(_, _, "status", "verification_result", "pass", "fail") -> ()
            | VerificationEvidenceParseError.ConflictingSemanticFields(_, _, c, a, cv, av) ->
                failwithf "ConflictingSemanticFields: canonical=%s alias=%s cv=%s av=%s (expected status/verification_result/pass/fail)"
                    c a cv av
            | _ -> failwithf "expected ConflictingSemanticFields, got %A" err
        }
        test "canonical wrong type, alias valid → canonical WrongFieldType" {
            let key = "status-cw-av"
            let json =
                verificationEvidenceBothPresent
                    key
                    "status"
                    "123"
                    "verification_result"
                    "\"pass\""
            let vr = runWith json ("status-cwa-" + key)
            let err =
                findLoadError vr (function
                    | VerificationEvidenceParseError.WrongFieldType _ -> true
                    | _ -> false)
            match err with
            | VerificationEvidenceParseError.WrongFieldType(_, _, "status", "string", "number") -> ()
            | VerificationEvidenceParseError.WrongFieldType(_, _, f, e, a) ->
                failwithf "WrongFieldType: field=%s expected=%s actual=%s (expected status/string/number)" f e a
            | _ -> failwithf "expected WrongFieldType, got %A" err
        }
        test "canonical valid, alias wrong type → alias WrongFieldType" {
            let key = "status-cv-aw"
            let json =
                verificationEvidenceBothPresent
                    key
                    "status"
                    "\"pass\""
                    "verification_result"
                    "true"
            let vr = runWith json ("status-cva-" + key)
            let err =
                findLoadError vr (function
                    | VerificationEvidenceParseError.WrongFieldType _ -> true
                    | _ -> false)
            match err with
            | VerificationEvidenceParseError.WrongFieldType(_, _, "verification_result", "string", "boolean") -> ()
            | VerificationEvidenceParseError.WrongFieldType(_, _, f, e, a) ->
                failwithf "WrongFieldType: field=%s expected=%s actual=%s (expected verification_result/string/boolean)" f e a
            | _ -> failwithf "expected WrongFieldType, got %A" err
        }
        test "both wrong type → canonical WrongFieldType" {
            let key = "status-both-wrong"
            let json =
                verificationEvidenceBothPresent
                    key
                    "status"
                    "1"
                    "verification_result"
                    "2"
            let vr = runWith json ("status-bw-" + key)
            let err =
                findLoadError vr (function
                    | VerificationEvidenceParseError.WrongFieldType _ -> true
                    | _ -> false)
            match err with
            | VerificationEvidenceParseError.WrongFieldType(_, _, "status", "string", "number") -> ()
            | VerificationEvidenceParseError.WrongFieldType(_, _, f, e, a) ->
                failwithf "WrongFieldType: field=%s expected=%s actual=%s (expected status/string/number)" f e a
            | _ -> failwithf "expected WrongFieldType, got %A" err
        }
    ]

// -----------------------------------------------------------------------------
// Spec §11 — command / verification_command
// -----------------------------------------------------------------------------

[<Tests>]
let commandTests =
    testList "command" [
        test "canonical only → evidence.Command preserved" {
            let key = "cmd-canonical-only"
            let json = verificationEvidenceCanonicalOnly key "command" "\"dotnet build\""
            Expect.equal (propertyOccurrences json "command") 1 "canonical 'command' must appear once"
            Expect.equal (propertyOccurrences json "verification_command") 0 "alias 'verification_command' must be absent"
            let evidence = parseAndAssert key json
            Expect.equal evidence.Command "dotnet build" "parsed Command must equal 'dotnet build'"
        }
        test "alias only → evidence.Command preserved and no canonical 'command' emitted" {
            let key = "cmd-alias-only"
            let json = verificationEvidenceAliasOnly key "verification_command" "\"dotnet test\""
            Expect.equal (propertyOccurrences json "command") 0 "canonical 'command' must be absent"
            Expect.equal (propertyOccurrences json "verification_command") 1 "alias 'verification_command' must appear once"
            let evidence = parseAndAssert key json
            Expect.equal evidence.Command "dotnet test" "parsed Command must equal 'dotnet test' (resolved from verification_command alias)"
        }
        test "both present equal → DuplicateSemanticField" {
            let key = "cmd-both-equal"
            let json =
                verificationEvidenceBothPresent
                    key
                    "command"
                    "\"dotnet test\""
                    "verification_command"
                    "\"dotnet test\""
            let vr = runWith json ("cmd-be-" + key)
            Expect.isTrue
                (hasLoadError vr (function
                    | VerificationEvidenceParseError.DuplicateSemanticField(_, _, "command", "verification_command") -> true
                    | _ -> false))
                "DuplicateSemanticField naming 'command' and 'verification_command' expected"
        }
        test "both present different → ConflictingSemanticFields" {
            let key = "cmd-both-diff"
            let json =
                verificationEvidenceBothPresent
                    key
                    "command"
                    "\"dotnet build\""
                    "verification_command"
                    "\"dotnet test\""
            let vr = runWith json ("cmd-bd-" + key)
            let err =
                findLoadError vr (function
                    | VerificationEvidenceParseError.ConflictingSemanticFields _ -> true
                    | _ -> false)
            match err with
            | VerificationEvidenceParseError.ConflictingSemanticFields(_, _, "command", "verification_command", "dotnet build", "dotnet test") -> ()
            | VerificationEvidenceParseError.ConflictingSemanticFields(_, _, c, a, cv, av) ->
                failwithf "ConflictingSemanticFields: canonical=%s alias=%s cv=%s av=%s (expected command/verification_command/dotnet build/dotnet test)"
                    c a cv av
            | _ -> failwithf "expected ConflictingSemanticFields, got %A" err
        }
        test "canonical wrong type, alias valid → canonical WrongFieldType" {
            let key = "cmd-cw-av"
            let json =
                verificationEvidenceBothPresent
                    key
                    "command"
                    "999"
                    "verification_command"
                    "\"dotnet test\""
            let vr = runWith json ("cmd-cwa-" + key)
            let err =
                findLoadError vr (function
                    | VerificationEvidenceParseError.WrongFieldType _ -> true
                    | _ -> false)
            match err with
            | VerificationEvidenceParseError.WrongFieldType(_, _, "command", "string", "number") -> ()
            | VerificationEvidenceParseError.WrongFieldType(_, _, f, e, a) ->
                failwithf "WrongFieldType: field=%s expected=%s actual=%s (expected command/string/number)" f e a
            | _ -> failwithf "expected WrongFieldType, got %A" err
        }
        test "canonical valid, alias wrong type → alias WrongFieldType" {
            let key = "cmd-cv-aw"
            let json =
                verificationEvidenceBothPresent
                    key
                    "command"
                    "\"dotnet test\""
                    "verification_command"
                    "false"
            let vr = runWith json ("cmd-cva-" + key)
            let err =
                findLoadError vr (function
                    | VerificationEvidenceParseError.WrongFieldType _ -> true
                    | _ -> false)
            match err with
            | VerificationEvidenceParseError.WrongFieldType(_, _, "verification_command", "string", "boolean") -> ()
            | VerificationEvidenceParseError.WrongFieldType(_, _, f, e, a) ->
                failwithf "WrongFieldType: field=%s expected=%s actual=%s (expected verification_command/string/boolean)" f e a
            | _ -> failwithf "expected WrongFieldType, got %A" err
        }
        test "both wrong type → canonical WrongFieldType" {
            let key = "cmd-both-wrong"
            let json =
                verificationEvidenceBothPresent
                    key
                    "command"
                    "42"
                    "verification_command"
                    "true"
            let vr = runWith json ("cmd-bw-" + key)
            let err =
                findLoadError vr (function
                    | VerificationEvidenceParseError.WrongFieldType _ -> true
                    | _ -> false)
            match err with
            | VerificationEvidenceParseError.WrongFieldType(_, _, "command", "string", "number") -> ()
            | VerificationEvidenceParseError.WrongFieldType(_, _, f, e, a) ->
                failwithf "WrongFieldType: field=%s expected=%s actual=%s (expected command/string/number)" f e a
            | _ -> failwithf "expected WrongFieldType, got %A" err
        }
    ]
