module Circus.Contracts.Tests.UnknownEventContractTests

open Circus.Contracts
open Circus.Domain
open Circus.Contracts.Tests.Support.Fixtures

let private ok =
    EventDecoder.decode EventDecoder.DefaultMaximumBytes

let private unknownFromResult result =
    match result with
    | Ok v ->
        match v.Event with
        | UnrecognizedEvent u -> u
        | other -> failwithf "expected unrecognized, got %A" other
    | Error errs ->
        errs
        |> NonEmptyList.toList
        |> List.map (sprintf "%A")
        |> String.concat "; "
        |> failwithf "expected Ok, got errors: %s"

/// 1. An unknown `type` is accepted as `UnrecognizedEvent`.
let testUnknownTypeAccepted () =
    let result = ok (Fixtures.bytes "valid/unknown-event.json")
    let u = unknownFromResult result

    Expect.equal
        u.EventType
        "io.leamas.execution.artefact.published.v3"
        "event type preserved"

/// 2. The unknown `data` payload is preserved as raw JSON.
let testUnknownDataPreserved () =
    let result = ok (Fixtures.bytes "valid/unknown-event.json")
    let u = unknownFromResult result

    match u.Data with
    | Some rawJson ->
        let text = RawJson.value rawJson
        Expect.stringContains text "artifact_kind" "raw payload contains artifact_kind"
        Expect.stringContains text "sha256:" "raw payload contains digest"
    | None -> failtest "unknown data should be preserved"

/// 3. Common envelope metadata survives decoding the unknown event.
let testCommonMetadataPreserved () =
    let result = ok (Fixtures.bytes "valid/unknown-event.json")
    let u = unknownFromResult result
    let v =
        match result with
        | Ok v -> v
        | Error _ -> failwith "impossible"

    Expect.equal
        (EventId.value v.EventId)
        "019b0437-2766-7a20-9225-4ab1645ba119"
        "EventId"
    Expect.equal
        (InstanceId.value v.InstanceId)
        "builder-07"
        "InstanceId"
    Expect.equal v.Subject "run/019b0437-1ef2-7abc-a38d-23472513f50a" "Subject"

/// 4. Valid unknown extensions are preserved on unrecognized events.
let testExtensionsPreservedForUnknownEvents () =
    let result = ok (Fixtures.bytes "valid/unknown-extension.json")
    let u = unknownFromResult result
    let v =
        match result with
        | Ok v -> v
        | Error _ -> failwith "impossible"

    Expect.equal u.EventType "io.leamas.execution.started.v1" "started type recorded"

    Expect.isTrue
        (v.Extensions.ContainsKey "tenant")
        "tenant extension preserved"
    Expect.isTrue
        (v.Extensions.ContainsKey "trace_id")
        "trace_id extension preserved"

/// 5. Repeated decoding of the same fixture yields equivalent values.
let testRepeatedDecodingIsStable () =
    let first =
        ok (Fixtures.bytes "valid/unknown-event.json")
        |> unknownFromResult

    let second =
        ok (Fixtures.bytes "valid/unknown-event.json")
        |> unknownFromResult

    Expect.equal first.EventType second.EventType "stable event type"
    Expect.equal (Option.map RawJson.value first.Data) (Option.map RawJson.value second.Data) "stable data"

let tests =
    testList
        "Unknown Event Behaviour"
        [
            test "unknown type is accepted" testUnknownTypeAccepted
            test "unknown data is preserved" testUnknownDataPreserved
            test "common metadata is preserved" testCommonMetadataPreserved
            test "extensions are preserved on unknown events" testExtensionsPreservedForUnknownEvents
            test "repeated decoding is stable" testRepeatedDecodingIsStable
        ]
