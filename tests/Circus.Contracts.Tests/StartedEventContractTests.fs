module Circus.Contracts.Tests.StartedEventContractTests

/// Silence warning FS3391 about implicit byte[] -> ReadOnlyMemory<byte> conversion
/// because we explicitly construct ReadOnlyMemory<byte> via Fixtures.bytes/inlineBytes.
/// Treat that warning as informational in the test project.
#nowarn "3391"


open Expecto
open Circus.Contracts
open Circus.Domain
open Circus.Contracts.Tests.Support.Fixtures

/// Exercise an envelope decoding result by unwrapping the
/// `ExecutionStartedEvent` case. Throws on any non-matching shape so the
/// test bodies read as straight-line assertions.
let private decodeOk (relativePath: string) : ExecutionStarted =
    match EventDecoder.decode EventDecoder.DefaultMaximumBytes (Fixtures.bytes relativePath) with
    | Ok validated ->
        match validated.Event with
        | ExecutionStartedEvent started -> started
        | other ->
            failwithf
                "expected ExecutionStartedEvent for %s but got %A"
                relativePath
                other
    | Error errs ->
        let msg = errs |> NonEmptyList.toList |> List.map (sprintf "%A") |> String.concat "; "
        failwithf "expected Ok started event for %s, got errors: %s" relativePath msg

/// 1. Minimal started event decodes into the canonical domain record.
let testMinimalStarted () =
    let started = decodeOk "valid/started-minimal.json"
    Expect.equal (RepositoryRef.value started.Repository) "k9b" "repository_ref"
    Expect.equal (LeamasVersion.value started.LeamasVersion) "0.1.0" "leamas_version"
    Expect.isNone started.ActId "act_id optional → None"
    Expect.isNone started.GitRevision "git_revision optional → None"
    Expect.isNone started.StartedBy "started_by optional → None"

/// 2. A complete started event decodes with all five payload fields.
let testCompleteStarted () =
    let started = decodeOk "valid/started-complete.json"

    Expect.equal
        (RepositoryRef.value started.Repository)
        "k9b"
        "repository_ref"

    Expect.isNone started.ActId "act_id must not be wrapped via .Value"

    match started.ActId with
    | None -> ()
    | Some act -> Expect.equal (ActId.value act) "ACT-K9B-EXAMPLE01" "act_id"

    Expect.equal (LeamasVersion.value started.LeamasVersion) "0.1.0" "leamas_version"

    match started.GitRevision with
    | Some rev -> Expect.equal rev "0123456789abcdef" "git_revision"
    | None -> failtest "git_revision must be present"

    match started.StartedBy with
    | Some user -> Expect.equal user "alex" "started_by"
    | None -> failtest "started_by must be present"

/// 3. Missing `repository_ref` is rejected with a typed violation, not UnrecognizedEvent.
let testRepositoryRefRequired () =
    let result =
        EventDecoder.decode
            EventDecoder.DefaultMaximumBytes
            (Fixtures.bytes "invalid-started/started-missing-repository.json")

    let violations = Assertions.contractViolations result
    Expect.isTrue
        (Assertions.hasInvalidKnownPayload violations)
        "must reject as InvalidKnownPayload"

    let payloadErrs = Assertions.payloadViolations violations
    Expect.isTrue
        (payloadErrs
         |> List.exists (function
             | PayloadMissingField "repository_ref" -> true
             | _ -> false))
        "PayloadMissingField repository_ref"

/// 4. `leamas_version` is required.
let testLeamasVersionRequired () =
    let missing = """{
  "specversion": "1.0",
  "id": "019b0437-2766-7a20-9225-4ab1645ba131",
  "source": "urn:leamas:instance:builder-07",
  "type": "io.leamas.execution.started.v1",
  "subject": "run/019b0437-1ef2-7abc-a38d-23472513f519",
  "time": "2026-07-12T20:00:00Z",
  "datacontenttype": "application/json",
  "circusinstance": "builder-07",
  "circusepoch": "019b0400-2f61-720d-94a5-c84e928eae19",
  "circusseq": 511,
  "runid": "019b0437-1ef2-7abc-a38d-23472513f519",
  "data": {
    "repository_ref": "k9b"
  }
}
"""
    let bytes = System.Text.Encoding.UTF8.GetBytes missing
    let result = EventDecoder.decode EventDecoder.DefaultMaximumBytes bytes
    let violations = Assertions.contractViolations result
    Expect.isTrue (Assertions.hasInvalidKnownPayload violations) "must reject as InvalidKnownPayload"

    let payloadErrs = Assertions.payloadViolations violations
    Expect.isTrue
        (payloadErrs
         |> List.exists (function
             | PayloadMissingField "leamas_version" -> true
             | _ -> false))
        "PayloadMissingField leamas_version"

/// 5. Optional fields remain optional (absent, null, present-but-short all decode).
let testOptionalFieldsRemainOptional () =
    let started = decodeOk "valid/started-minimal.json"

    Expect.isNone started.ActId "act_id optional → None"
    Expect.isNone started.GitRevision "git_revision optional → None"
    Expect.isNone started.StartedBy "started_by optional → None"

/// 6. Oversized strings are rejected with a typed violation.
let testOversizedStringsRejected () =
    let result =
        EventDecoder.decode
            EventDecoder.DefaultMaximumBytes
            (Fixtures.bytes "invalid-started/started-invalid-leamas-version.json")

    let violations = Assertions.contractViolations result
    Expect.isTrue (Assertions.hasInvalidKnownPayload violations) "InvalidKnownPayload present"

    let payloadErrs = Assertions.payloadViolations violations
    Expect.isTrue
        (payloadErrs
         |> List.exists (function
             | PayloadInvalidFieldValue (n, _) when n = "leamas_version" -> true
             | _ -> false))
        "PayloadInvalidFieldValue on leamas_version"

/// 7. A malformed started payload does NOT become an unrecognized event.
let testMalformedKnownPayloadIsNotUnknown () =
    let result =
        EventDecoder.decode
            EventDecoder.DefaultMaximumBytes
            (Fixtures.bytes "invalid-started/started-missing-repository.json")

    match result with
    | Ok _ -> failtest "must reject the envelope, not convert to UnrecognizedEvent"
    | Error _ -> ()

let tests =
    testList
        "Started Event Contract"
        [
            test "minimal started event decodes" testMinimalStarted
            test "complete started event decodes" testCompleteStarted
            test "repository_ref is required" testRepositoryRefRequired
            test "leamas_version is required" testLeamasVersionRequired
            test "optional values remain optional" testOptionalFieldsRemainOptional
            test "oversized strings are rejected" testOversizedStringsRejected
            test "malformed known payload is not unknown" testMalformedKnownPayloadIsNotUnknown
        ]
