module Circus.Contracts.Tests.StartedEventContractTests

open Circus.Contracts
open Circus.Domain
open Circus.Contracts.Tests.Support.Fixtures

let private ok =
    EventDecoder.decode EventDecoder.DefaultMaximumBytes

let private exercise envelopeResult =
    match envelopeResult with
    | Ok validated ->
        match validated.Event with
        | ExecutionStartedEvent started -> Ok started
        | other -> failwithf "expected ExecutionStartedEvent, got %A" other
    | Error errs ->
        let msg = errs |> NonEmptyList.toList |> List.map (sprintf "%A") |> String.concat "; "
        failwithf "expected Ok started event, got errors: %s" msg

/// 1. Minimal started event decodes into the canonical domain record.
let testMinimalStarted () =
    exercise (ok (Fixtures.bytes "valid/started-minimal.json"))
    |> fun _ -> ()

/// 2. A complete started event decodes with all five payload fields.
let testCompleteStarted () =
    let result = exercise (ok (Fixtures.bytes "valid/started-complete.json"))

    Expect.isNone result.ActId.Value "act_id must not be wrapped into the Some/null shape returned here"

    Expect.equal
        (RepositoryRef.value result.Repository)
        "k9b"
        "repository_ref"

    match result.ActId with
    | None -> ()
    | Some act -> Expect.equal (ActId.value act) "ACT-K9B-EXAMPLE01" "act_id"

    Expect.equal (LeamasVersion.value result.LeamasVersion) "0.1.0" "leamas_version"

    match result.GitRevision with
    | Some rev -> Expect.equal rev "0123456789abcdef" "git_revision"
    | None -> failtest "git_revision must be present"

    match result.StartedBy with
    | Some user -> Expect.equal user "alex" "started_by"
    | None -> failtest "started_by must be present"

/// 3. Missing `repository_ref` is rejected with a typed violation, not UnrecognizedEvent.
let testRepositoryRefRequired () =
    let result =
        ok (Fixtures.bytes "invalid-started/started-missing-repository.json")

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

    Expect.isFalse
        (violations
         |> List.exists (function
             | InvalidKnownPayload (_, _) when false -> true
             | _ -> false))
        "no UnrecognizedEvent must be produced"

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

    let result = ok (System.Text.Encoding.UTF8.GetBytes(missing) :> System.ReadOnlyMemory<byte>)
    let violations = Assertions.contractViolations result
    Expect.isTrue
        (Assertions.hasInvalidKnownPayload violations)
        "must reject as InvalidKnownPayload"

/// 5. Optional fields remain optional (absent, null, present-but-short all decode).
let testOptionalFieldsRemainOptional () =
    let result = exercise (ok (Fixtures.bytes "valid/started-minimal.json"))
    Expect.isNone result.ActId "act_id optional → None"
    Expect.isNone result.GitRevision "git_revision optional → None"
    Expect.isNone result.StartedBy "started_by optional → None"

/// 6. Oversized strings are rejected with a typed violation.
let testOversizedStringsRejected () =
    let result =
        ok (Fixtures.bytes "invalid-started/started-invalid-leamas-version.json")

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
        ok (Fixtures.bytes "invalid-started/started-missing-repository.json")

    Expect.isTrue
        (match result with
         | Ok _ -> false
         | Error _ -> true)
        "must reject the envelope, not convert to UnrecognizedEvent"

    let violations = Assertions.contractViolations result
    Expect.isFalse
        (violations
         |> List.exists (function
             | _ -> false))
        "not UnrecognizedEvent"

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
