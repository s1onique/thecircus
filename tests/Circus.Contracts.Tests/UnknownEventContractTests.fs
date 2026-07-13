module Circus.Contracts.Tests.UnknownEventContractTests

/// Silence FS3391 — we explicitly construct ReadOnlyMemory<byte> via Fixtures.bytes.
#nowarn "3391"

open Expecto
open Circus.Contracts
open Circus.Domain
open Circus.Contracts.Tests.Support.Fixtures

let private maxBytes = EventDecoder.DefaultMaximumBytes

/// Local wrapper that anchors `test` to a single function value,
/// avoiding the FS0003 cascade on F#'s `test` overload set.
let private mkTest (name: string) (body: unit -> unit) = Tests.test name body

let private decodeOk relativePath : ValidatedEvent =
    match EventDecoder.decode maxBytes (Fixtures.bytes relativePath) with
    | Ok validated -> validated
    | Error errs ->
        let msg = errs |> NonEmptyList.toList |> List.map (sprintf "%A") |> String.concat "; "
        failwithf "expected Ok, got errors: %s" msg

/// 1. An unknown `type` is accepted as `UnrecognizedEvent`.
let testUnknownTypeAccepted () =
    let validated = decodeOk "valid/unknown-event.json"
    match validated.Event with
    | UnrecognizedEvent u ->
        Expect.equal u.EventType "io.leamas.execution.artefact.published.v3" "event type preserved"
    | other -> failtestf "expected UnrecognizedEvent but got %A" other

/// 2. The unknown `data` payload is preserved as raw JSON.
let testUnknownDataPreserved () =
    let validated = decodeOk "valid/unknown-event.json"
    match validated.Event with
    | UnrecognizedEvent u ->
        match u.Data with
        | Some rawJson ->
            let text = RawJson.value rawJson
            Expect.stringContains text "artifact_kind" "raw payload contains artifact_kind"
            Expect.stringContains text "sha256:" "raw payload contains digest"
        | None -> failtest "unknown data should be preserved"
    | other -> failtestf "expected UnrecognizedEvent, got %A" other

/// 3. Common envelope metadata survives decoding the unknown event.
let testCommonMetadataPreserved () =
    let validated = decodeOk "valid/unknown-event.json"
    Expect.equal (EventId.value validated.EventId) "019b0437-2766-7a20-9225-4ab1645ba119" "EventId"
    Expect.equal (InstanceId.value validated.InstanceId) "builder-07" "InstanceId"
    Expect.equal validated.Subject "run/019b0437-1ef2-7abc-a38d-23472513f50a" "Subject"

/// 4. Valid unknown extensions are preserved on unrecognized events.
let testExtensionsPreservedForUnknownEvents () =
    let validated = decodeOk "valid/unknown-extension.json"
    match validated.Event with
    | UnrecognizedEvent u ->
        Expect.equal u.EventType "io.leamas.execution.started.v1" "started type recorded"
        Expect.isTrue (validated.Extensions.ContainsKey "tenant") "tenant extension preserved"
        Expect.isTrue (validated.Extensions.ContainsKey "trace_id") "trace_id extension preserved"
    | other -> failtestf "expected UnrecognizedEvent, got %A" other

/// 5. Repeated decoding of the same fixture yields equivalent values.
let testRepeatedDecodingIsStable () =
    let first = decodeOk "valid/unknown-event.json"
    let second = decodeOk "valid/unknown-event.json"
    let firstType, firstData =
        match first.Event with
        | UnrecognizedEvent u -> u.EventType, u.Data
        | _ -> failwith "expected UnrecognizedEvent"
    let secondType, secondData =
        match second.Event with
        | UnrecognizedEvent u -> u.EventType, u.Data
        | _ -> failwith "expected UnrecognizedEvent"
    Expect.equal firstType secondType "stable event type"
    let firstText = firstData |> Option.map RawJson.value
    let secondText = secondData |> Option.map RawJson.value
    Expect.equal firstText secondText "stable data"

let tests =
    testList "Unknown Event Behaviour" [mkTest "unknown type is accepted" testUnknownTypeAccepted
        mkTest "unknown data is preserved" testUnknownDataPreserved
        mkTest "common metadata is preserved" testCommonMetadataPreserved
        mkTest "extensions are preserved on unknown events" testExtensionsPreservedForUnknownEvents
        mkTest "repeated decoding is stable" testRepeatedDecodingIsStable
    ]
