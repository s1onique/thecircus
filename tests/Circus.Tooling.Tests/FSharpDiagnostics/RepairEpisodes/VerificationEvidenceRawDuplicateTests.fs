module Circus.Tooling.Tests.FSharpDiagnostics.RepairEpisodes.VerificationEvidenceRawDuplicateTests

// =============================================================================
// Verification Evidence Raw Duplicate Property Tests
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-VERIFICATION-EVIDENCE-ALIAS-CONTRACT-CLOSURE01-CORRECTION03:
// Spec §14 — raw duplicate-property detection.  The parser must
// unconditionally reject records whose top-level JSON object repeats a
// property name.  Duplicate detection happens before semantic alias
// resolution and stops the parser immediately.
//
// Selection of which duplicated name is reported uses `String.CompareOrdinal`
// (lexicographic, case-sensitive).  When multiple distinct names are
// duplicated, the lexicographically smallest wins.
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

let private assertDuplicateRawProperty
    (vr: VerificationResult)
    (expectedPropertyName: string)
    (expectedOccurrenceCount: int)
    : unit =
    let err =
        findLoadError vr (function
            | VerificationEvidenceParseError.DuplicateRawProperty _ -> true
            | _ -> false)
    match err with
    | VerificationEvidenceParseError.DuplicateRawProperty(_, _, name, count) ->
        Expect.equal name expectedPropertyName (sprintf "DuplicateRawProperty name: expected %s, got %s" expectedPropertyName name)
        Expect.equal count expectedOccurrenceCount (sprintf "DuplicateRawProperty count: expected %d, got %d" expectedOccurrenceCount count)
    | _ -> failwithf "expected DuplicateRawProperty, got %A" err

// -----------------------------------------------------------------------------
// Spec §14 — raw duplicate property matrix
// -----------------------------------------------------------------------------

[<Tests>]
let rawDuplicateTests =
    testList "raw duplicate properties" [
        // 1. canonical property repeated twice
        test "canonical 'kind' repeated twice → DuplicateRawProperty" {
            let key = "dup-canon-2"
            let json =
                verificationEvidenceRawProperties
                    key
                    [ "kind", "\"focused_test\""
                      "kind", "\"build\"" ]
            let vr = runWith json ("dup-c2-" + key)
            assertDuplicateRawProperty vr "kind" 2
        }
        // 2. alias property repeated twice
        test "alias 'verification_kind' repeated twice → DuplicateRawProperty" {
            let key = "dup-alias-2"
            let json =
                verificationEvidenceRawProperties
                    key
                    [ "verification_kind", "\"focused_test\""
                      "verification_kind", "\"build\"" ]
            let vr = runWith json ("dup-a2-" + key)
            assertDuplicateRawProperty vr "verification_kind" 2
        }
        // 3. one property repeated three times
        test "canonical 'command' repeated three times → DuplicateRawProperty with count 3" {
            let key = "dup-canon-3"
            let json =
                verificationEvidenceRawProperties
                    key
                    [ "command", "\"dotnet build\""
                      "command", "\"dotnet test\""
                      "command", "\"dotnet publish\"" ]
            let vr = runWith json ("dup-c3-" + key)
            assertDuplicateRawProperty vr "command" 3
        }
        // 4. several different names duplicated → ordinal lexicographic first wins
        //    'command' < 'kind' < 'status', so the parser must report 'command'
        test "multiple distinct duplicated names → ordinal lex first selected" {
            let key = "dup-multi"
            let json =
                verificationEvidenceRawProperties
                    key
                    [ "kind", "\"focused_test\""
                      "kind", "\"build\""
                      "command", "\"dotnet build\""
                      "command", "\"dotnet test\""
                      "status", "\"pass\""
                      "status", "\"fail\"" ]
            let vr = runWith json ("dup-multi-" + key)
            // command < kind < status, so command is reported
            assertDuplicateRawProperty vr "command" 2
        }
        // 5. same semantic input as case 4 with shuffled property order
        //    Shuffling property order must NOT change the selected duplicate name
        test "shuffled property order → same duplicate selected" {
            let key = "dup-multi-shuffled"
            let json =
                verificationEvidenceRawProperties
                    key
                    [ "status", "\"pass\""
                      "command", "\"dotnet build\""
                      "kind", "\"focused_test\""
                      "kind", "\"build\""
                      "status", "\"fail\""
                      "command", "\"dotnet test\"" ]
            let vr = runWith json ("dup-multi-sh-" + key)
            // command < kind < status, so command is reported regardless of order
            assertDuplicateRawProperty vr "command" 2
        }
        // 6. case-sensitive names: `status` and `Status` are NOT raw duplicates.
        //    Duplicate detection is case-sensitive (String.CompareOrdinal).
        //    The parser MUST NOT report DuplicateRawProperty for case-
        //    distinct names.  It MAY reject the record for any other
        //    semantic reason (e.g. unknown field 'Status'); the contract
        //    being asserted here is specifically that they are not raw
        //    duplicates of each other.
        test "case-sensitive: 'status' and 'Status' are NOT raw duplicates" {
            let key = "dup-case-sensitive"
            let json =
                verificationEvidenceRawProperties
                    key
                    [ "kind", "\"focused_test\""
                      "command", "\"dotnet test\""
                      "status", "\"pass\""
                      "exit_code", "0"
                      "tested_commit_oid", sprintf "\"%s\"" validCommitOid
                      "tested_tree_oid", sprintf "\"%s\"" validTreeOid
                      "Status", "\"anything\"" ]
            Expect.equal (propertyOccurrences json "status") 1 "'status' must appear once"
            Expect.equal (propertyOccurrences json "Status") 1 "'Status' must appear once"
            let vr = runWith json ("dup-cs-" + key)
            let hasDup =
                vr.Issues
                |> List.exists (function
                    | VerificationIssue.VerificationEvidenceLoadFailed errs ->
                        errs
                        |> List.exists (function
                            | VerificationEvidenceLoadError.ParseError
                                (VerificationEvidenceParseError.DuplicateRawProperty _) -> true
                            | _ -> false)
                    | _ -> false)
            Expect.isFalse hasDup "'status' and 'Status' are case-distinct and must NOT trigger DuplicateRawProperty"
        }
    ]
