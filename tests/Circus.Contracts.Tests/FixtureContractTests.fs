module Circus.Contracts.Tests.FixtureContractTests

/// Silence FS3391 — we explicitly construct ReadOnlyMemory<byte> via Fixtures.bytes.
#nowarn "3391"

open System.Text
open Expecto
open Circus.Contracts
open Circus.Domain
open Circus.Contracts.Tests.Support.Fixtures

let private maxBytes = EventDecoder.DefaultMaximumBytes

/// Local wrapper that anchors `test` to a single function value,
/// avoiding the FS0003 cascade on F#'s `test` overload set.
let private mkTest (name: string) (body: unit -> unit) = Tests.test name body

/// 1. The invalid-fixture corpus does not throw for any committed file.
let testInvalidFixturesDoNotThrow () =
    let invalidPaths =
        Fixtures.allInvalidEnvelope ()
        @ Fixtures.allInvalidStarted ()
        @ Fixtures.allInvalidFinished ()

    Expect.isGreaterThan (List.length invalidPaths) 0 "invalid fixture corpus must be non-empty"

    let results =
        invalidPaths
        |> List.map (fun path ->
            let fileName = System.IO.Path.GetFileName path
            try
                let bytes = Fixtures.bytes fileName
                let outcome = EventDecoder.decode maxBytes bytes
                match outcome with
                | Ok _ -> sprintf "%s: returned Ok for invalid fixture" fileName
                | Error _ -> sprintf "%s: rejected as expected" fileName
            with ex -> sprintf "%s: THREW %s" fileName ex.Message)

    let unexpected =
        results
        |> List.filter (fun r -> r.Contains("returned Ok for invalid fixture") || r.Contains("THREW"))

    Expect.isEmpty
        unexpected
        (sprintf "All invalid fixtures must reject without throwing. Offenders:\n%s" (String.concat "\n" unexpected))

/// 2. Diagnostics do not echo back the submitted body.
let testDiagnosticsDoNotEchoBody () =
    let oversized = Fixtures.readFixture "valid/started-minimal.json"
    let hugeText = oversized + String.init 4096 (fun _ -> "a")
    let hugeBytes = Encoding.UTF8.GetBytes hugeText
    let result = EventDecoder.decode 64 hugeBytes
    match result with
    | Error errs ->
        let text = errs |> NonEmptyList.toList |> List.map (sprintf "%A") |> String.concat "; "
        Expect.isFalse (text.Contains("repository_ref")) "no field values leaked into the diagnostic"
        Expect.isFalse (text.Contains("k9b")) "no body literal leaked"
    | Ok _ -> failtest "expected BodyTooLarge"

/// 3. Malformed JSON diagnostics are bounded to a small window.
let testMalformedDiagnosticsBounded () =
    let malicious = "{ this is not json " + String.init 4000 (fun _ -> "AAA")
    let bytes = Encoding.UTF8.GetBytes malicious
    let result = EventDecoder.decode 262144 bytes
    match result with
    | Error errs ->
        let violation = NonEmptyList.toList errs |> List.tryPick (function
            | MalformedJson m -> Some m
            | _ -> None)
        match violation with
        | Some msg ->
            Expect.isLessThanOrEqual msg.Length Limits.MalformedJsonMessageLimit "diagnostic bounded by the documented limit"
        | None -> failtest "expected MalformedJson"
    | Ok _ -> failtest "malformed JSON must reject"

/// 4. The decoder does not retain borrowed references to disposed `JsonDocument`s.
let testDecoderDoesNotRetainDisposedDocuments () =
    let firstDecode = EventDecoder.decode maxBytes (Fixtures.bytes "valid/unknown-event.json")
    let secondDecode = EventDecoder.decode maxBytes (Fixtures.bytes "valid/unknown-extension.json")
    let thirdDecode = EventDecoder.decode maxBytes (Fixtures.bytes "valid/properties-reordered.json")
    Expect.isTrue
        (match firstDecode, secondDecode, thirdDecode with
         | Ok a, Ok b, Ok c ->
             (EventId.value a.EventId).Length > 0
             && (EventId.value b.EventId).Length > 0
             && (EventId.value c.EventId).Length > 0
         | _ -> false)
        "decoded values remain valid through subsequent decodes"
    Expect.isTrue
        (match firstDecode, secondDecode, thirdDecode with
         | Ok a, Ok b, Ok c -> a.Subject <> b.Subject && b.Subject <> c.Subject
         | _ -> false)
        "subjects isolated across decodes"

let tests =
    testList "Safety" [mkTest "invalid fixture corpus does not throw" testInvalidFixturesDoNotThrow
        mkTest "diagnostics do not echo full body" testDiagnosticsDoNotEchoBody
        mkTest "malformed diagnostics are bounded" testMalformedDiagnosticsBounded
        mkTest "decoder does not retain disposed documents" testDecoderDoesNotRetainDisposedDocuments
    ]
