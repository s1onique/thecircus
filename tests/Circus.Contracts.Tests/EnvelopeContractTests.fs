module Circus.Contracts.Tests.EnvelopeContractTests

/// Silence warning FS3391 about implicit byte[] -> ReadOnlyMemory<byte> conversion
/// because we explicitly construct ReadOnlyMemory<byte> via Fixtures.bytes/inlineBytes.
/// Treat that warning as informational in the test project.
#nowarn "3391"


open System
open System.Text
open Expecto
open Circus.Contracts
open Circus.Domain
open Circus.Contracts.Tests.Support.Fixtures

let private defaultMax = EventDecoder.DefaultMaximumBytes

/// 1. The minimal valid envelope decodes into a `ValidatedEvent`.
let testValidMinimalEnvelope () =
    let result =
        EventDecoder.decode defaultMax (Fixtures.bytes "valid/started-minimal.json")

    match result with
    | Ok validated ->
        Expect.equal
            (EventId.value validated.EventId)
            "019b0437-2766-7a20-9225-4ab1645ba115"
            "EventId should match"
    | Error errs ->
        let msgs = errs |> NonEmptyList.toList |> List.map (sprintf "%A")
        failtestf "expected Ok, got errors: %s" (String.concat "; " msgs)

/// 2. Property ordering is irrelevant — the decoded envelope is identical.
let testPropertyOrderingIsIrrelevant () =
    let reference = EventDecoder.decode defaultMax (Fixtures.bytes "valid/started-minimal.json")
    let reordered = EventDecoder.decode defaultMax (Fixtures.bytes "valid/properties-reordered.json")

    match reference, reordered with
    | Ok a, Ok b ->
        Expect.equal (EventId.value a.EventId) (EventId.value b.EventId) "EventId"
        Expect.equal (RunId.value a.RunId) (RunId.value b.RunId) "RunId"
        Expect.equal a.Subject b.Subject "Subject"
    | _ ->
        failtest "expected both fixture decodings to succeed"

/// 3. Malformed JSON returns `MalformedJson` without leaking the body.
let testMalformedJson () =
    let result =
        EventDecoder.decode defaultMax (Fixtures.bytes "invalid-envelope/malformed-json.json")

    let violations = Assertions.contractViolations result

    Expect.equal (List.length violations) 1 "exactly one violation"
    Expect.isTrue (Assertions.hasMalformedJson violations) "MalformedJson present"

    match List.head violations with
    | MalformedJson msg ->
        Expect.isLessThanOrEqual msg.Length Limits.MalformedJsonMessageLimit "diagnostic is bounded"
    | _ -> failtest "expected MalformedJson"

/// 4. An oversized body returns `BodyTooLarge` without parsing the content.
let testOversizedBodyRejected () =
    let baseline = Encoding.UTF8.GetBytes(Fixtures.readFixture "valid/started-minimal.json")
    let padded = Array.append baseline (Array.init 1024 (fun _ -> ' 'B))

    let result =
        EventDecoder.decode (baseline.Length + 16) (padded)

    let violations = Assertions.contractViolations result
    Expect.isTrue (Assertions.hasBodyTooLarge violations) "BodyTooLarge present"

    match List.tryFind (function BodyTooLarge _ -> true | _ -> false) violations with
    | Some (BodyTooLarge (maximum, actual)) ->
        Expect.equal maximum (baseline.Length + 16) "maximum reported"
        Expect.isGreaterThan actual baseline.Length "actual reported"
    | _ -> failtest "expected BodyTooLarge with reported bounds"

/// 5. Root array is rejected with `RootMustBeObject`.
let testRootArrayRejected () =
    let result =
        EventDecoder.decode defaultMax (Fixtures.bytes "invalid-envelope/body-root-array.json")

    let violations = Assertions.contractViolations result
    Expect.isTrue (Assertions.hasRootMustBeObject violations) "RootMustBeObject present"

/// 6. Multiple independent envelope violations are accumulated.
let testIndependentEnvelopeViolationsAccumulate () =
    let result =
        EventDecoder.decode defaultMax (Fixtures.bytes "invalid-envelope/missing-required-envelope-fields.json")

    let violations = Assertions.contractViolations result
    Expect.isGreaterThan (List.length violations) 1 "more than one violation accumulated"

    Expect.isTrue
        (Assertions.hasMissingField EnvelopeFieldNames.Type violations)
        "type missing"
    Expect.isTrue
        (Assertions.hasMissingField EnvelopeFieldNames.Subject violations)
        "subject missing"
    Expect.isTrue
        (Assertions.hasMissingField EnvelopeFieldNames.DataContentType violations)
        "datacontenttype missing"

/// 7. An unsupported `specversion` is rejected.
let testUnsupportedSpecVersion () =
    let result =
        EventDecoder.decode defaultMax (Fixtures.bytes "invalid-envelope/wrong-specversion.json")

    let violations = Assertions.contractViolations result
    Expect.isTrue (Assertions.hasUnsupportedSpecVersion violations) "UnsupportedSpecVersion present"

    match List.tryFind (function UnsupportedSpecVersion _ -> true | _ -> false) violations with
    | Some (UnsupportedSpecVersion reported) ->
        Expect.equal reported "1.0.3-wip" "spec version retained for diagnostics"
    | _ -> failtest "expected UnsupportedSpecVersion('1.0.3-wip')"

/// 8. A timestamp without an offset is rejected.
let testTimestampWithoutOffsetRejected () =
    let result =
        EventDecoder.decode defaultMax (Fixtures.bytes "invalid-envelope/time-without-offset.json")

    let violations = Assertions.contractViolations result
    Expect.isTrue
        (violations |> List.exists (function
            | InvalidFieldValue (n, _) when n = EnvelopeFieldNames.Time -> true
            | _ -> false))
        "InvalidFieldValue on time"

/// 9. A negative sequence number is rejected.
let testNegativeSequenceRejected () =
    let result =
        EventDecoder.decode defaultMax (Fixtures.bytes "invalid-envelope/negative-sequence.json")

    let violations = Assertions.contractViolations result

    Expect.isTrue
        (violations
         |> List.exists (function
             | InvalidFieldValue (n, _) when n = EnvelopeFieldNames.CircusSeq -> true
             | _ -> false))
        "InvalidFieldValue on circusseq"

/// 10. Subject/runid disagreement is rejected with `SubjectRunIdMismatch`.
let testSubjectRunIdMismatch () =
    let result =
        EventDecoder.decode defaultMax (Fixtures.bytes "invalid-envelope/subject-runid-mismatch.json")

    let violations = Assertions.contractViolations result
    Expect.isTrue (Assertions.hasSubjectRunIdMismatch violations) "SubjectRunIdMismatch present"

/// 11. Valid unknown extensions are preserved through decode.
let testUnknownExtensionPreserved () =
    let result = EventDecoder.decode defaultMax (Fixtures.bytes "valid/unknown-extension.json")

    match result with
    | Ok validated ->
        Expect.isTrue (validated.Extensions.ContainsKey "tenant") "tenant extension preserved"
        Expect.isTrue (validated.Extensions.ContainsKey "pipeline") "pipeline extension preserved"
        Expect.isTrue (validated.Extensions.ContainsKey "review_required") "review_required extension preserved"
        Expect.isTrue (validated.Extensions.ContainsKey "trace_id") "trace_id extension preserved"
    | Error _ -> failtest "unknown-extension.json must decode"

/// 12. Invalid extension names are rejected.
let testInvalidExtensionRejected () =
    let bad = """{
  "specversion": "1.0",
  "id": "019b0437-2766-7a20-9225-4ab1645ba130",
  "source": "urn:leamas:instance:builder-07",
  "type": "io.leamas.execution.started.v1",
  "subject": "run/019b0437-1ef2-7abc-a38d-23472513f518",
  "time": "2026-07-12T20:00:00Z",
  "datacontenttype": "application/json",
  "circusinstance": "builder-07",
  "circusepoch": "019b0400-2f61-720d-94a5-c84e928eae19",
  "circusseq": 510,
  "runid": "019b0437-1ef2-7abc-a38d-23472513f518",
  "UPPERCASE_EXTENSION": "x",
  "data": {
    "repository_ref": "k9b",
    "leamas_version": "0.1.0"
  }
}
"""
    let bytes = Encoding.UTF8.GetBytes bad
    let result = EventDecoder.decode defaultMax bytes
    let violations = Assertions.contractViolations result
    Expect.isTrue (Assertions.hasInvalidExtensionName violations) "InvalidExtensionName present"

let tests =
    testList
        "Envelope Contract"
        [
            test "valid minimal envelope decodes" testValidMinimalEnvelope
            test "property ordering is irrelevant" testPropertyOrderingIsIrrelevant
            test "malformed JSON returns MalformedJson" testMalformedJson
            test "oversized body returns BodyTooLarge" testOversizedBodyRejected
            test "root array is rejected" testRootArrayRejected
            test "independent envelope violations accumulate" testIndependentEnvelopeViolationsAccumulate
            test "unsupported specversion is rejected" testUnsupportedSpecVersion
            test "timestamp without offset is rejected" testTimestampWithoutOffsetRejected
            test "negative sequence is rejected" testNegativeSequenceRejected
            test "subject/runid mismatch is rejected" testSubjectRunIdMismatch
            test "unknown extension is preserved" testUnknownExtensionPreserved
            test "invalid extension name is rejected" testInvalidExtensionRejected
        ]
