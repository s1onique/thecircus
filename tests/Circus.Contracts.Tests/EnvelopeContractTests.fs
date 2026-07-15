module Circus.Contracts.Tests.EnvelopeContractTests

/// Silence warning FS3391 about implicit byte[] -> ReadOnlyMemory<byte> conversion
/// because we explicitly construct ReadOnlyMemory<byte> via Fixtures.bytes/inlineBytes.
#nowarn "3391"


open Expecto
open Circus.Contracts
open Circus.Domain
open Circus.Contracts.Tests.Support.Fixtures

let private defaultMax = EventDecoder.DefaultMaximumBytes

/// Helper: validate every envelope test.
let private runEnvelope (relativePath: string) =
    EventDecoder.decode defaultMax (Fixtures.bytes relativePath)

/// 1. The minimal valid envelope decodes into a `ValidatedEvent`.
let testValidMinimalEnvelope () =
    let result = runEnvelope "valid/started-minimal.json"

    match result with
    | Ok validated -> Expect.equal (EventId.value validated.EventId) "019b0437-2766-7a20-9225-4ab1645ba115" "EventId"
    | Error errs ->
        let msgs = errs |> NonEmptyList.toList |> List.map (sprintf "%A")
        failtest (sprintf "expected Ok, got errors: %s" (String.concat "; " msgs))

/// 2. Property ordering is irrelevant.
let testPropertyOrderingIsIrrelevant () =
    let reference = runEnvelope "valid/started-minimal.json"
    let reordered = runEnvelope "valid/properties-reordered.json"

    match reference, reordered with
    | Ok a, Ok b ->
        Expect.equal (EventId.value a.EventId) (EventId.value b.EventId) "EventId"
        Expect.equal (RunId.value a.RunId) (RunId.value b.RunId) "RunId"
        Expect.equal a.Subject b.Subject "Subject"
    | _ -> failtest "expected both fixture decodings to succeed"

/// 3. Malformed JSON returns `MalformedJson`.
let testMalformedJson () =
    let result = runEnvelope "invalid-envelope/malformed-json.json"
    let violations = Assertions.contractViolations result
    Expect.equal (List.length violations) 1 "exactly one violation"
    Expect.isTrue (Assertions.hasMalformedJson violations) "MalformedJson present"

    match
        List.tryPick
            (function
            | MalformedJson m -> Some m
            | _ -> None)
            violations
    with
    | Some msg -> Expect.isLessThanOrEqual msg.Length Limits.MalformedJsonMessageLimit "diagnostic is bounded"
    | None -> failtest "expected MalformedJson"

/// 4. An oversized body returns `BodyTooLarge`.
let testOversizedBodyRejected () =
    let baseline =
        System.Text.Encoding.UTF8.GetBytes(Fixtures.readFixture "valid/started-minimal.json")

    let padded = Array.append baseline (Array.init 1024 (fun _ -> ' 'B))
    let result = EventDecoder.decode (baseline.Length + 16) padded
    let violations = Assertions.contractViolations result
    Expect.isTrue (Assertions.hasBodyTooLarge violations) "BodyTooLarge present"

/// 5. Root array is rejected.
let testRootArrayRejected () =
    let result = runEnvelope "invalid-envelope/body-root-array.json"
    Expect.isTrue (Assertions.hasRootMustBeObject (Assertions.contractViolations result)) "RootMustBeObject present"

/// 6. Independent envelope violations accumulate.
let testIndependentEnvelopeViolationsAccumulate () =
    let result = runEnvelope "invalid-envelope/missing-required-envelope-fields.json"
    let violations = Assertions.contractViolations result
    Expect.isGreaterThan (List.length violations) 1 "more than one violation accumulated"

    Expect.isTrue (Assertions.hasMissingField EnvelopeFieldNames.Type violations) "type missing"
    Expect.isTrue (Assertions.hasMissingField EnvelopeFieldNames.Subject violations) "subject missing"
    Expect.isTrue (Assertions.hasMissingField EnvelopeFieldNames.DataContentType violations) "datacontenttype missing"

/// 7. An unsupported `specversion` is rejected.
let testUnsupportedSpecVersion () =
    let result = runEnvelope "invalid-envelope/wrong-specversion.json"
    let violations = Assertions.contractViolations result
    Expect.isTrue (Assertions.hasUnsupportedSpecVersion violations) "UnsupportedSpecVersion present"

/// 8. A timestamp without an offset is rejected.
let testTimestampWithoutOffsetRejected () =
    let result = runEnvelope "invalid-envelope/time-without-offset.json"
    let violations = Assertions.contractViolations result

    Expect.isTrue
        (violations
         |> List.exists (function
             | InvalidFieldValue(n, _) when n = EnvelopeFieldNames.Time -> true
             | _ -> false))
        "InvalidFieldValue on time"

/// 9. A negative sequence number is rejected.
let testNegativeSequenceRejected () =
    let result = runEnvelope "invalid-envelope/negative-sequence.json"
    let violations = Assertions.contractViolations result

    Expect.isTrue
        (violations
         |> List.exists (function
             | InvalidFieldValue(n, _) when n = EnvelopeFieldNames.CircusSeq -> true
             | _ -> false))
        "InvalidFieldValue on circusseq"

/// 10. Subject/runid disagreement is rejected.
let testSubjectRunIdMismatch () =
    let result = runEnvelope "invalid-envelope/subject-runid-mismatch.json"

    Expect.isTrue
        (Assertions.hasSubjectRunIdMismatch (Assertions.contractViolations result))
        "SubjectRunIdMismatch present"

/// 11. Valid unknown extensions are preserved.
let testUnknownExtensionPreserved () =
    let result = runEnvelope "valid/unknown-extension.json"

    match result with
    | Ok validated ->
        Expect.isTrue (validated.Extensions.ContainsKey "tenant") "tenant preserved"
        Expect.isTrue (validated.Extensions.ContainsKey "pipeline") "pipeline preserved"
        Expect.isTrue (validated.Extensions.ContainsKey "review_required") "review_required preserved"
        Expect.isTrue (validated.Extensions.ContainsKey "trace_id") "trace_id preserved"
    | Error _ -> failtest "unknown-extension.json must decode"

/// 12. Invalid extension names are rejected.
let testInvalidExtensionRejected () =
    let bad =
        """{"specversion":"1.0","id":"x","source":"urn:leamas:instance:builder-07","type":"io.leamas.execution.started.v1","subject":"run/x","time":"2026-07-12T20:00:00Z","datacontenttype":"application/json","circusinstance":"builder-07","circusepoch":"019b0400-2f61-720d-94a5-c84e928eae19","circusseq":510,"runid":"x","UPPERCASE_EXTENSION":"x","data":{"repository_ref":"k9b","leamas_version":"0.1.0"}}"""

    let bytes = System.Text.Encoding.UTF8.GetBytes bad
    let result = EventDecoder.decode defaultMax bytes

    Expect.isTrue
        (Assertions.hasInvalidExtensionName (Assertions.contractViolations result))
        "InvalidExtensionName present"

let bundle =
    testList
        "Envelope Contract"
        [ testCase "valid minimal envelope decodes" testValidMinimalEnvelope
          testCase "property ordering is irrelevant" testPropertyOrderingIsIrrelevant
          testCase "malformed JSON returns MalformedJson" testMalformedJson
          testCase "oversized body returns BodyTooLarge" testOversizedBodyRejected
          testCase "root array is rejected" testRootArrayRejected
          testCase "independent envelope violations accumulate" testIndependentEnvelopeViolationsAccumulate
          testCase "unsupported specversion is rejected" testUnsupportedSpecVersion
          testCase "timestamp without offset is rejected" testTimestampWithoutOffsetRejected
          testCase "negative sequence is rejected" testNegativeSequenceRejected
          testCase "subject/runid mismatch is rejected" testSubjectRunIdMismatch
          testCase "unknown extension is preserved" testUnknownExtensionPreserved
          testCase "invalid extension name is rejected" testInvalidExtensionRejected ]
