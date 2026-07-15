module Circus.Contracts.Tests.StartedEventContractTests

/// Silence FS3391 — we explicitly construct ReadOnlyMemory<byte> via Fixtures.bytes.
#nowarn "3391"

open Expecto
open Circus.Contracts
open Circus.Domain
open Circus.Contracts.Tests.Support.Fixtures

let private maxBytes = EventDecoder.DefaultMaximumBytes

let private decodeStarted relativePath : ExecutionStarted =
    match EventDecoder.decode maxBytes (Fixtures.bytes relativePath) with
    | Ok validated ->
        match validated.Event with
        | ExecutionStartedEvent started -> started
        | other -> failwithf "expected ExecutionStartedEvent, got %A" other
    | Error errs ->
        let msg =
            errs |> NonEmptyList.toList |> List.map (sprintf "%A") |> String.concat "; "

        failwithf "expected Ok started event, got errors: %s" msg

/// 1. Minimal started event decodes into the canonical domain record.
let testMinimalStarted () =
    let started = decodeStarted "valid/started-minimal.json"
    Expect.equal (RepositoryRef.value started.Repository) "k9b" "repository_ref"
    Expect.equal (LeamasVersion.value started.LeamasVersion) "0.1.0" "leamas_version"
    Expect.isNone started.ActId "act_id optional → None"
    Expect.isNone started.GitRevision "git_revision optional → None"
    Expect.isNone started.StartedBy "started_by optional → None"

/// 2. A complete started event decodes with all five payload fields.
let testCompleteStarted () =
    let started = decodeStarted "valid/started-complete.json"
    Expect.equal (RepositoryRef.value started.Repository) "k9b" "repository_ref"

    match started.ActId with
    | Some act -> Expect.equal (ActId.value act) "ACT-K9B-EXAMPLE01" "act_id"
    | None -> failtest "act_id must be present"

    Expect.equal (LeamasVersion.value started.LeamasVersion) "0.1.0" "leamas_version"

    match started.GitRevision with
    | Some rev -> Expect.equal rev "0123456789abcdef" "git_revision"
    | None -> failtest "git_revision must be present"

    match started.StartedBy with
    | Some user -> Expect.equal user "alex" "started_by"
    | None -> failtest "started_by must be present"

/// 3. Missing `repository_ref` is rejected with a typed violation.
let testRepositoryRefRequired () =
    let result =
        EventDecoder.decode maxBytes (Fixtures.bytes "invalid-started/started-missing-repository.json")

    Expect.isTrue
        (Assertions.hasInvalidKnownPayload (Assertions.contractViolations result))
        "must reject as InvalidKnownPayload"

    Expect.isTrue
        (Assertions.payloadViolations (Assertions.contractViolations result)
         |> List.exists (function
             | PayloadMissingField "repository_ref" -> true
             | _ -> false))
        "PayloadMissingField repository_ref"

/// 4. `leamas_version` is required.
let testLeamasVersionRequired () =
    let bogus =
        """{"specversion":"1.0","id":"019b0437-2766-7a20-9225-4ab1645ba131","source":"urn:leamas:instance:builder-07","type":"io.leamas.execution.started.v1","subject":"run/019b0437-1ef2-7abc-a38d-23472513f519","time":"2026-07-12T20:00:00Z","datacontenttype":"application/json","circusinstance":"builder-07","circusepoch":"019b0400-2f61-720d-94a5-c84e928eae19","circusseq":511,"runid":"019b0437-1ef2-7abc-a38d-23472513f519","data":{"repository_ref":"k9b"}}"""

    let bytes = System.Text.Encoding.UTF8.GetBytes bogus
    let result = EventDecoder.decode maxBytes bytes

    Expect.isTrue
        (Assertions.hasInvalidKnownPayload (Assertions.contractViolations result))
        "must reject as InvalidKnownPayload"

/// 5. Optional fields remain optional.
let testOptionalFieldsRemainOptional () =
    let started = decodeStarted "valid/started-minimal.json"
    Expect.isNone started.ActId "act_id optional → None"
    Expect.isNone started.GitRevision "git_revision optional → None"
    Expect.isNone started.StartedBy "started_by optional → None"

/// 6. Oversized strings are rejected with a typed violation.
let testOversizedStringsRejected () =
    let result =
        EventDecoder.decode maxBytes (Fixtures.bytes "invalid-started/started-invalid-leamas-version.json")

    Expect.isTrue
        (Assertions.hasInvalidKnownPayload (Assertions.contractViolations result))
        "InvalidKnownPayload present"

/// 7. A malformed started payload does NOT become an unrecognized event.
let testMalformedKnownPayloadIsNotUnknown () =
    let result =
        EventDecoder.decode maxBytes (Fixtures.bytes "invalid-started/started-missing-repository.json")

    match result with
    | Ok _ -> failtest "must reject the envelope, not convert to UnrecognizedEvent"
    | Error _ -> ()

let bundle =
    testList
        "Started Event Contract"
        [ testCase "minimal started event decodes" testMinimalStarted
          testCase "complete started event decodes" testCompleteStarted
          testCase "repository_ref is required" testRepositoryRefRequired
          testCase "leamas_version is required" testLeamasVersionRequired
          testCase "optional values remain optional" testOptionalFieldsRemainOptional
          testCase "oversized strings are rejected" testOversizedStringsRejected
          testCase "malformed known payload is not unknown" testMalformedKnownPayloadIsNotUnknown ]
